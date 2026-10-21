#include <windows.h>
#include <ocidl.h>
#include <xamlOM.h>
#undef GetCurrentTime
#include <winrt/Windows.UI.Xaml.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.UI.Xaml.Controls.h>
#include <winrt/Windows.UI.Xaml.Media.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Media.Control.h>
#include <winrt/Windows.UI.h>
#include <mutex>
#include <thread>
#include <chrono>
#include <vector>
#include <algorithm>
#include <winrt/Windows.UI.Text.h>
#include <winrt/Windows.UI.Xaml.Input.h>
#include <winrt/Windows.UI.Xaml.Media.Animation.h>
#include <winrt/Windows.UI.Xaml.Automation.h>
#include <winrt/Windows.UI.Xaml.Media.Imaging.h>
#include <winrt/Windows.UI.Xaml.Shapes.h>
#include <winrt/Windows.Foundation.h>
#include <UIAutomation.h>
#include <atomic>
#include <string>
#include <atomic>

// {6E2A4C1B-7F3D-4D2A-9C5E-1B2A3C4D5E6F}
static const CLSID CLSID_XamlTap =
{ 0x6e2a4c1b, 0x7f3d, 0x4d2a, { 0x9c, 0x5e, 0x1b, 0x2a, 0x3c, 0x4d, 0x5e, 0x6f } };

extern "C" const IID IID_IVisualTreeServiceCallback =
{ 0xaa7a8931, 0x80e4, 0x4fec, { 0x8f, 0x3b, 0x55, 0x3f, 0x87, 0xb4, 0x96, 0x6e } };
extern "C" const IID IID_IVisualTreeServiceCallback2 =
{ 0xbad9eb88, 0xae77, 0x4397, { 0xb9, 0x48, 0x5f, 0xa2, 0xdb, 0x0a, 0x19, 0xea } };
static std::mutex g_mediaMutex;
static std::wstring g_mediaText;
static std::wstring g_mediaArtist;
static bool g_hasMedia = false;
static bool g_isPlaying = false;
static std::atomic<bool> g_foundSearchRoot = false;
static void SetSearchboxMode(DWORD mode);
static DWORD SearchboxMode();
static void EnsureSearchTree();
static HANDLE g_readyEvent = nullptr;
static void SignalTapReady();
static std::once_flag g_mediaOnce;
static RECT g_searchRect = {};
static std::atomic<bool> g_mediaActive = false;
static HHOOK g_searchHook = nullptr;
static void SignalTapReady()
{
    HANDLE ready = OpenEventW(EVENT_MODIFY_STATE, FALSE, L"Local\\ShellIslandTapReady");
    if (ready)
    {
        SetEvent(ready);
        CloseHandle(ready);
    }
}
static void LogText(const char* text);

static bool ReadLyricBridge(std::wstring& title, std::wstring& lyric)
{
    wchar_t dir[MAX_PATH];
    DWORD length = GetEnvironmentVariableW(L"LOCALAPPDATA", dir, MAX_PATH);
    if (length == 0 || length >= MAX_PATH) return false;
    lstrcatW(dir, L"\\ShellIsland\\nowplaying.txt");

    HANDLE file = CreateFileW(dir, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
        nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return false;

    char buffer[4096];
    DWORD read = 0;
    BOOL ok = ReadFile(file, buffer, sizeof(buffer) - 1, &read, nullptr);
    CloseHandle(file);
    if (!ok || read == 0) return false;
    buffer[read] = '\0';

    int wideLength = MultiByteToWideChar(CP_UTF8, 0, buffer, (int)read, nullptr, 0);
    if (wideLength <= 0) return false;
    std::wstring all(wideLength, L'\0');
    MultiByteToWideChar(CP_UTF8, 0, buffer, (int)read, all.data(), wideLength);

    std::wstring current;
    std::wstring lines[3];
    int index = 0;
    for (wchar_t ch : all)
    {
        if (ch == L'\n')
        {
            if (index < 3) lines[index] = current;
            ++index;
            current.clear();
            if (index >= 3) break;
        }
        else if (ch != L'\r')
        {
            current.push_back(ch);
        }
    }
    if (index < 3 && index < 3) lines[index] = current;

    if (lines[0].empty() || lines[2].empty()) return false;
    title = lines[0];
    lyric = lines[2];
    return true;
}
static void MediaThread()
{
    try { winrt::init_apartment(winrt::apartment_type::multi_threaded); }
    catch (...) { return; }

    while (true)
    {
        std::wstring text;
        std::wstring artistText;
        bool playing = false;
        bool hasMedia = false;
        try
        {
            using namespace winrt::Windows::Media::Control;
            static auto manager = GlobalSystemMediaTransportControlsSessionManager::RequestAsync().get();
            auto session = manager.GetCurrentSession();
            for (auto const& candidate : manager.GetSessions())
            {
                auto candidateProps = candidate.TryGetMediaPropertiesAsync().get();
                if (!candidateProps.Title().empty())
                {
                    session = candidate;
                    break;
                }
            }
            if (session)
            {
                auto props = session.TryGetMediaPropertiesAsync().get();
                auto title = props.Title();
                auto artist = props.Artist();
                if (!title.empty())
                {
                    text = std::wstring(title);
                    artistText = std::wstring(artist);
                }
                auto info = session.GetPlaybackInfo();
                playing = info.PlaybackStatus() == winrt::Windows::Media::Control::GlobalSystemMediaTransportControlsSessionPlaybackStatus::Playing;
                auto status = info.PlaybackStatus();
                const bool active = status != winrt::Windows::Media::Control::GlobalSystemMediaTransportControlsSessionPlaybackStatus::Closed &&
                                    status != winrt::Windows::Media::Control::GlobalSystemMediaTransportControlsSessionPlaybackStatus::Stopped &&
                                    !text.empty();
                hasMedia = active && !text.empty();
            }
        }
        catch (...) { }

        {
            std::wstring bridgeTitle, bridgeLyric;
            if (ReadLyricBridge(bridgeTitle, bridgeLyric) && !bridgeLyric.empty())
            {
                artistText = bridgeTitle;
                text = bridgeLyric;
                hasMedia = true;
            }
            std::lock_guard<std::mutex> lock(g_mediaMutex);
            g_mediaText = text;
            g_mediaArtist = artistText;
            g_hasMedia = hasMedia;
            g_mediaActive = hasMedia;
            g_isPlaying = playing;
        }
        (void)playing;
        Sleep(1000);
    }
}
static DWORD SearchboxMode()
{
    DWORD mode = 1;
    DWORD size = sizeof(mode);
    if (RegGetValueW(HKEY_CURRENT_USER,
        L"Software\\Microsoft\\Windows\\CurrentVersion\\Search",
        L"SearchboxTaskbarMode", RRF_RT_REG_DWORD, nullptr, &mode, &size) != ERROR_SUCCESS)
    {
        mode = 1;
    }
    return mode;
}
static void SetSearchboxMode(DWORD mode)
{
    HKEY key = nullptr;
    if (RegOpenKeyExW(HKEY_CURRENT_USER,
        L"Software\\Microsoft\\Windows\\CurrentVersion\\Search",
        0, KEY_SET_VALUE, &key) != ERROR_SUCCESS)
    {
        return;
    }
    RegSetValueExW(key, L"SearchboxTaskbarMode", 0, REG_DWORD,
        reinterpret_cast<const BYTE*>(&mode), sizeof(mode));
    RegCloseKey(key);
    SendMessageTimeoutW(HWND_BROADCAST, WM_SETTINGCHANGE, 0,
        reinterpret_cast<LPARAM>(L"TraySettings"), SMTO_ABORTIFHUNG, 2000, nullptr);
}

static void EnsureSearchTree()
{
    Sleep(2500);
    if (g_foundSearchRoot)
    {
        return;
    }

    const DWORD mode = SearchboxMode();
    const DWORD alternate = mode == 3 ? 2 : 3;
    LogText("search tree missing, forcing rebuild\r\n");
    SetSearchboxMode(alternate);
    Sleep(1500);
    SetSearchboxMode(mode);
}
static double SearchModeWidth()
{
    DWORD mode = 1;
    DWORD size = sizeof(mode);
    if (RegGetValueW(HKEY_CURRENT_USER,
        L"Software\\Microsoft\\Windows\\CurrentVersion\\Search",
        L"SearchboxTaskbarMode", RRF_RT_REG_DWORD, nullptr, &mode, &size) != ERROR_SUCCESS)
    {
        mode = 1;
    }

    switch (mode)
    {
    case 1: return 44.0;
    case 2: return 220.0;
    case 3: return 120.0;
    default: return 44.0;
    }
}
static bool FindSearchRect(RECT* result)
{
    IUIAutomation* automation = nullptr;
    if (FAILED(CoCreateInstance(__uuidof(CUIAutomation), nullptr, CLSCTX_INPROC_SERVER,
        IID_PPV_ARGS(&automation))))
    {
        return false;
    }

    bool found = false;
    HWND tray = FindWindowW(L"Shell_TrayWnd", nullptr);
    IUIAutomationElement* root = nullptr;
    if (tray && SUCCEEDED(automation->ElementFromHandle(tray, &root)) && root)
    {
        VARIANT value;
        VariantInit(&value);
        value.vt = VT_BSTR;
        value.bstrVal = SysAllocString(L"SearchButton");
        IUIAutomationCondition* condition = nullptr;
        if (SUCCEEDED(automation->CreatePropertyCondition(UIA_AutomationIdPropertyId, value, &condition)) && condition)
        {
            IUIAutomationElement* element = nullptr;
            if (SUCCEEDED(root->FindFirst(TreeScope_Descendants, condition, &element)) && element)
            {
                RECT rect;
                if (SUCCEEDED(element->get_CurrentBoundingRectangle(&rect)))
                {
                    IUIAutomationTreeWalker* walker = nullptr;
                    if (SUCCEEDED(automation->get_ControlViewWalker(&walker)) && walker)
                    {
                        IUIAutomationElement* parent = nullptr;
                        if (SUCCEEDED(walker->GetParentElement(element, &parent)) && parent)
                        {
                            RECT parentRect{};
                            if (SUCCEEDED(parent->get_CurrentBoundingRectangle(&parentRect)))
                            {
                                const long parentWidth = parentRect.right - parentRect.left;
                                const long buttonWidth = rect.right - rect.left;
                                if (parentWidth > buttonWidth && parentWidth < 620)
                                {
                                    rect = parentRect;
                                }
                            }
                            parent->Release();
                        }
                        walker->Release();
                    }
                    rect.left -= 6;
                    rect.right += 6;
                    *result = rect;
                    found = true;
                }
                element->Release();
            }
            condition->Release();
        }
        VariantClear(&value);
        root->Release();
    }
    automation->Release();
    return found;
}

static bool FindIslandRect(RECT* result)
{
    IUIAutomation* automation = nullptr;
    if (FAILED(CoCreateInstance(__uuidof(CUIAutomation), nullptr, CLSCTX_INPROC_SERVER,
        IID_PPV_ARGS(&automation))))
    {
        return false;
    }

    bool found = false;
    HWND tray = FindWindowW(L"Shell_TrayWnd", nullptr);
    IUIAutomationElement* root = nullptr;
    if (tray && SUCCEEDED(automation->ElementFromHandle(tray, &root)) && root)
    {
        VARIANT value;
        VariantInit(&value);
        value.vt = VT_BSTR;
        value.bstrVal = SysAllocString(L"ShellIslandIsland");
        IUIAutomationCondition* condition = nullptr;
        if (SUCCEEDED(automation->CreatePropertyCondition(UIA_AutomationIdPropertyId, value, &condition)) && condition)
        {
            IUIAutomationElement* element = nullptr;
            if (SUCCEEDED(root->FindFirst(TreeScope_Descendants, condition, &element)) && element)
            {
                RECT rect;
                if (SUCCEEDED(element->get_CurrentBoundingRectangle(&rect)))
                {
                    rect.left -= 4;
                    rect.right += 4;
                    *result = rect;
                    found = true;
                }
                element->Release();
            }
            condition->Release();
        }
        VariantClear(&value);
        root->Release();
    }
    automation->Release();
    return found;
}

static DWORD WINAPI RectThread(LPVOID)
{
    CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    while (true)
    {
        RECT rect{};
        if (FindIslandRect(&rect) || FindSearchRect(&rect))
        {
            g_searchRect = rect;
        }
        Sleep(500);
    }
    return 0;
}
static LRESULT CALLBACK SearchMouseProc(int nCode, WPARAM wParam, LPARAM lParam)
{
    if (nCode >= 0 && g_mediaActive &&
        (wParam == WM_LBUTTONDOWN || wParam == WM_LBUTTONUP))
    {
        auto info = reinterpret_cast<MSLLHOOKSTRUCT*>(lParam);
        if (PtInRect(&g_searchRect, info->pt))
        {
            if (wParam == WM_LBUTTONUP)
            {
                HANDLE event = OpenEventW(EVENT_MODIFY_STATE, FALSE, L"ShellIslandOpen");
                if (event)
                {
                    SetEvent(event);
                    CloseHandle(event);
                }
            }
            return 1;
        }
    }
    return CallNextHookEx(g_searchHook, nCode, wParam, lParam);
}

static DWORD WINAPI SearchHookThread(LPVOID)
{
    CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    RECT rect{};
    bool found = FindIslandRect(&rect) || FindSearchRect(&rect);
    if (found) g_searchRect = rect;
    char buffer[256];
    wsprintfA(buffer, "search hook rect found=%d %ld,%ld,%ld,%ld\\r\\n", found ? 1 : 0, rect.left, rect.top, rect.right, rect.bottom);
    LogText(buffer);
    g_searchHook = SetWindowsHookExW(WH_MOUSE_LL, SearchMouseProc, GetModuleHandleW(L"XamlTap.dll"), 0);
    CreateThread(nullptr, 0, RectThread, nullptr, 0, nullptr);
    wsprintfA(buffer, "search hook=%p\\r\\n", g_searchHook);
    LogText(buffer);
    MSG message;
    while (GetMessageW(&message, nullptr, 0, 0))
    {
        TranslateMessage(&message);
        DispatchMessageW(&message);
    }
    return 0;
}
static void LogText(const char* text)
{
    wchar_t path[MAX_PATH];
    DWORD len = GetTempPathW(MAX_PATH, path);
    if (!len) return;
    lstrcatW(path, L"XamlTap.log");
    HANDLE file = CreateFileW(path, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE,
        nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return;
    DWORD written = 0;
    WriteFile(file, text, (DWORD)lstrlenA(text), &written, nullptr);
    CloseHandle(file);
}

static void LogElement(const char* prefix, const VisualElement& element)
{
    char buffer[1024];
    const wchar_t* type = element.Type ? element.Type : L"(null)";
    const wchar_t* name = element.Name ? element.Name : L"(null)";
    wsprintfA(buffer, "%s handle=%p type=%ls name=%ls\r\n",
        prefix, (void*)(uintptr_t)element.Handle, type, name);
    LogText(buffer);
}

struct VisibilityState
{
    winrt::Windows::UI::Xaml::UIElement Element{ nullptr };
    winrt::Windows::UI::Xaml::Visibility Visibility{ winrt::Windows::UI::Xaml::Visibility::Visible };
};

class VisualCallback : public IVisualTreeServiceCallback2
{
    std::atomic<ULONG> m_ref{ 1 };
    IXamlDiagnostics* m_diag;
public:
    winrt::Windows::UI::Xaml::DispatcherTimer m_timer{ nullptr };
    winrt::Windows::UI::Xaml::Controls::Panel m_panel{ nullptr };
    winrt::Windows::UI::Xaml::Controls::Border m_island{ nullptr };
    winrt::Windows::UI::Xaml::Controls::FontIcon m_icon{ nullptr };
    winrt::Windows::UI::Xaml::Shapes::Ellipse m_artwork{ nullptr };
    winrt::Windows::UI::Xaml::Media::ImageBrush m_artBrush{ nullptr };
    winrt::Windows::UI::Xaml::Media::RotateTransform m_artRotate{ nullptr };
    winrt::Windows::UI::Xaml::Media::Animation::Storyboard m_rotateStoryboard{ nullptr };
    bool m_rotateRunning = false;
    uint64_t m_artStamp = 0;
    winrt::Windows::UI::Xaml::Controls::TextBlock m_title{ nullptr };
    winrt::Windows::UI::Xaml::Controls::TextBlock m_titleCopy{ nullptr };
    winrt::Windows::UI::Xaml::Controls::TextBlock m_artist{ nullptr };
    winrt::Windows::UI::Xaml::Controls::StackPanel m_textPanel{ nullptr };
    winrt::Windows::UI::Xaml::Controls::StackPanel m_row{ nullptr };
    double m_nativeWidth = 44;
    double m_targetWidth = 0;
    bool m_lightTheme = false;
    winrt::Windows::UI::Xaml::Controls::Canvas m_titleCanvas{ nullptr };
    winrt::Windows::UI::Xaml::Media::TranslateTransform m_titleTransform{ nullptr };
    winrt::Windows::UI::Xaml::DispatcherTimer m_scrollTimer{ nullptr };
    double m_scrollOffset = 0;
    std::wstring m_lastTitle;
    std::vector<VisibilityState> m_original;
    std::vector<VisibilityState> m_nativeIcons;
    explicit VisualCallback(IXamlDiagnostics* diag) : m_diag(diag)
    {
        std::call_once(g_mediaOnce, [] { std::thread(MediaThread).detach(); CreateThread(nullptr, 0, SearchHookThread, nullptr, 0, nullptr); });
    }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        if (riid == IID_IUnknown || riid == IID_IVisualTreeServiceCallback2 ||
            riid == IID_IVisualTreeServiceCallback)
        {
            *ppv = static_cast<IVisualTreeServiceCallback2*>(this);
            AddRef();
            return S_OK;
        }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }

    ULONG STDMETHODCALLTYPE AddRef() override { return ++m_ref; }
    ULONG STDMETHODCALLTYPE Release() override
    {
        ULONG value = --m_ref;
        if (!value) delete this;
        return value;
    }

    HRESULT STDMETHODCALLTYPE OnVisualTreeChange(
        ParentChildRelation relation, VisualElement element, VisualMutationType mutationType) override
    {
        LogElement(mutationType == Add ? "ADD" : "REMOVE", element);
        if (element.Name && wcsstr(element.Name, L"Search"))
        {
            LogText("search hit\r\n");
        }
        if (mutationType == Add && element.Name)
        {
            std::wstring typeName(element.Type ? element.Type : L"");
            std::wstring elementName(element.Name ? element.Name : L"");
            if (typeName.find(L"AnimatedVisualPlayer") != std::wstring::npos &&
                elementName == L"Icon")
            {
                CaptureSearchIcon(element.Handle, relation.Parent);
            }            if (typeName.find(L"SearchButtonRootGrid") != std::wstring::npos ||
                elementName.find(L"SearchBoxButtonRootPanel") != std::wstring::npos)
            {
                g_foundSearchRoot = true;
                LogText("ModifySearch call\r\n");
                ModifySearch(element.Handle, relation.Parent, typeName);
            }
        }
        if (element.Type) SysFreeString(element.Type);
        if (element.Name) SysFreeString(element.Name);
        if (element.SrcInfo.FileName) SysFreeString(element.SrcInfo.FileName);
        if (element.SrcInfo.Hash) SysFreeString(element.SrcInfo.Hash);
        return S_OK;
    }

    void CaptureSearchIcon(InstanceHandle iconHandle, InstanceHandle parentHandle)
    {
        try
        {
            ::IInspectable* parentRaw = nullptr;
            if (FAILED(m_diag->GetIInspectableFromHandle(parentHandle, &parentRaw)) || !parentRaw)
            {
                return;
            }
            winrt::Windows::Foundation::IInspectable parent{ parentRaw, winrt::take_ownership_from_abi };
            std::wstring className(winrt::get_class_name(parent).c_str());
            if (className.find(L"Search") == std::wstring::npos)
            {
                return;
            }

            ::IInspectable* iconRaw = nullptr;
            if (FAILED(m_diag->GetIInspectableFromHandle(iconHandle, &iconRaw)) || !iconRaw)
            {
                return;
            }
            winrt::Windows::Foundation::IInspectable iconObject{ iconRaw, winrt::take_ownership_from_abi };
            if (auto icon = iconObject.try_as<winrt::Windows::UI::Xaml::UIElement>())
            {
                m_nativeIcons.push_back({ icon, icon.Visibility() });
            }
        }
        catch (...)
        {
        }
    }
    void SignalOpen()
    {
        HANDLE event = OpenEventW(EVENT_MODIFY_STATE, FALSE, L"ShellIslandOpen");
        if (event)
        {
            SetEvent(event);
            CloseHandle(event);
        }
        LogText("island tapped -> ShellIslandOpen\r\n");
    }
    static bool IsLightTheme()
    {
        DWORD value = 0;
        DWORD size = sizeof(value);
        if (RegGetValueW(HKEY_CURRENT_USER,
            L"Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize",
            L"SystemUsesLightTheme", RRF_RT_REG_DWORD, nullptr, &value, &size) != ERROR_SUCCESS)
        {
            return false;
        }
        return value != 0;
    }

    void RefreshArtwork()
    {
        if (!m_artwork || !m_icon) return;

        wchar_t dir[MAX_PATH];
        DWORD length = GetEnvironmentVariableW(L"LOCALAPPDATA", dir, MAX_PATH);
        if (length == 0 || length >= MAX_PATH) return;
        lstrcatW(dir, L"\\ShellIsland\\artwork.png");

        WIN32_FILE_ATTRIBUTE_DATA data{};
        if (!GetFileAttributesExW(dir, GetFileExInfoStandard, &data))
        {
            m_artwork.Visibility(winrt::Windows::UI::Xaml::Visibility::Collapsed);
            m_icon.Visibility(winrt::Windows::UI::Xaml::Visibility::Visible);
            m_artStamp = 0;
            return;
        }

        const uint64_t stamp = ((uint64_t)data.ftLastWriteTime.dwHighDateTime << 32) | data.ftLastWriteTime.dwLowDateTime;
        if (stamp == m_artStamp)
        {
            return;
        }

        try
        {
            std::wstring uri = L"file:///";
            uri += dir;
            for (auto& ch : uri) if (ch == L'\\') ch = L'/';
            uri += L"?v=" + std::to_wstring(stamp);
            auto bitmap = winrt::Windows::UI::Xaml::Media::Imaging::BitmapImage();
            bitmap.UriSource(winrt::Windows::Foundation::Uri(uri));
            m_artBrush.ImageSource(bitmap);
            m_artwork.Visibility(winrt::Windows::UI::Xaml::Visibility::Visible);
            m_icon.Visibility(winrt::Windows::UI::Xaml::Visibility::Collapsed);
            m_artStamp = stamp;
        }
        catch (...)
        {
        }
    }
    void ApplyIslandColors()
    {
        if (!m_island) return;
        const bool light = IsLightTheme();
        m_lightTheme = light;
        auto background = winrt::Windows::UI::Xaml::Media::SolidColorBrush(light
            ? winrt::Windows::UI::ColorHelper::FromArgb(242, 255, 255, 255)
            : winrt::Windows::UI::ColorHelper::FromArgb(230, 28, 28, 30));
        auto borderBrush = winrt::Windows::UI::Xaml::Media::SolidColorBrush(light
            ? winrt::Windows::UI::ColorHelper::FromArgb(34, 0, 0, 0)
            : winrt::Windows::UI::ColorHelper::FromArgb(32, 255, 255, 255));
        auto primary = winrt::Windows::UI::Xaml::Media::SolidColorBrush(light
            ? winrt::Windows::UI::ColorHelper::FromArgb(255, 26, 26, 26)
            : winrt::Windows::UI::ColorHelper::FromArgb(255, 255, 255, 255));
        auto secondary = winrt::Windows::UI::Xaml::Media::SolidColorBrush(light
            ? winrt::Windows::UI::ColorHelper::FromArgb(190, 96, 96, 96)
            : winrt::Windows::UI::ColorHelper::FromArgb(210, 188, 188, 188));

        m_island.Background(background);
        m_island.BorderBrush(borderBrush);
        if (m_icon) m_icon.Foreground(primary);
        if (m_title) m_title.Foreground(secondary);
        if (m_artist) m_artist.Foreground(secondary);
    }

    void AnimateWidth(double target)
    {
        if (!m_island) return;
        if (m_targetWidth <= 0)
        {
            m_targetWidth = target;
            m_island.Width(target);
            return;
        }
        if (std::abs(m_targetWidth - target) < 0.5)
        {
            return;
        }

        m_targetWidth = target;
        auto animation = winrt::Windows::UI::Xaml::Media::Animation::DoubleAnimation();
        animation.From(m_island.ActualWidth() > 0 ? m_island.ActualWidth() : target);
        animation.To(target);
        animation.Duration(winrt::Windows::UI::Xaml::Duration{
            winrt::Windows::Foundation::TimeSpan{ std::chrono::milliseconds(180) } });

        auto easing = winrt::Windows::UI::Xaml::Media::Animation::CubicEase();
        easing.EasingMode(winrt::Windows::UI::Xaml::Media::Animation::EasingMode::EaseOut);
        animation.EasingFunction(easing);

        auto storyboard = winrt::Windows::UI::Xaml::Media::Animation::Storyboard();
        winrt::Windows::UI::Xaml::Media::Animation::Storyboard::SetTarget(animation, m_island);
        winrt::Windows::UI::Xaml::Media::Animation::Storyboard::SetTargetProperty(animation, L"Width");
        storyboard.Children().Append(animation);
        storyboard.Begin();
    }
    void TryApplyExisting(winrt::Windows::Foundation::IInspectable const& root)
    {
        try
        {
            auto element = root.try_as<winrt::Windows::UI::Xaml::UIElement>();
            if (!element)
            {
                LogText("GetUiLayer is not UIElement\r\n");
                return;
            }
            WalkExisting(element, 0);
        }
        catch (...)
        {
            LogText("TryApplyExisting exception\r\n");
        }
    }

    void WalkExisting(winrt::Windows::UI::Xaml::UIElement const& element, int depth)
    {
        if (!element || depth > 80) return;
        try
        {
            auto name = winrt::get_class_name(element);
            std::wstring typeName(name.c_str());
            auto framework = element.try_as<winrt::Windows::UI::Xaml::FrameworkElement>();
            if (framework)
            {
                std::wstring elementName(framework.Name().c_str());
                if (elementName == L"SearchBoxButtonRootPanel" ||
                    typeName.find(L"SearchButtonRootGrid") != std::wstring::npos)
                {
                    if (auto panel = element.try_as<winrt::Windows::UI::Xaml::Controls::Panel>())
                    {
                        g_foundSearchRoot = true;
                        ApplyIsland(panel);
                        LogText("existing SearchButtonRootGrid applied\r\n");
                        return;
                    }
                }
            }

            const int count = winrt::Windows::UI::Xaml::Media::VisualTreeHelper::GetChildrenCount(element);
            for (int i = 0; i < count; ++i)
            {
                auto child = winrt::Windows::UI::Xaml::Media::VisualTreeHelper::GetChild(element, i);
                if (auto childElement = child.try_as<winrt::Windows::UI::Xaml::UIElement>())
                {
                    WalkExisting(childElement, depth + 1);
                }
                if (g_foundSearchRoot) return;
            }
        }
        catch (...)
        {
        }
    }
    void StartMediaTimer()
    {
        if (m_timer)
        {
            m_timer.Stop();
            m_timer = nullptr;
        }
        m_timer = winrt::Windows::UI::Xaml::DispatcherTimer();
        m_timer.Interval(std::chrono::seconds(1));
        m_timer.Tick([this](auto const&, auto const&)
        {
            std::wstring title, artist;
            bool hasMedia;
            bool playing;
            {
                std::lock_guard<std::mutex> lock(g_mediaMutex);
                title = g_mediaText;
                artist = g_mediaArtist;
                hasMedia = g_hasMedia;
                playing = g_isPlaying;
            }

            if (hasMedia)
            {
                std::wstring bridgeTitle, bridgeLyric;
                if (ReadLyricBridge(bridgeTitle, bridgeLyric) && !bridgeLyric.empty())
                {
                    title = bridgeLyric;
                    artist = bridgeTitle;
                }
            }

            if (!m_island || !m_title || !m_artist || !m_textPanel) return;

            if (hasMedia)
            {
                for (auto const& child : m_original)
                {
                    child.Element.Visibility(winrt::Windows::UI::Xaml::Visibility::Collapsed);
                }

                double width = SearchModeWidth();

                const DWORD searchMode = SearchboxMode();
                const bool showText = searchMode != 1;
                const bool showTitle = searchMode == 2 && !artist.empty();
                const double viewport = (std::max)(20.0, width - 44.0);
                AnimateWidth((std::max)(32.0, width - 4.0));
                m_textPanel.Width(viewport);
                m_titleCanvas.Width(viewport);
                if (m_row) m_row.HorizontalAlignment(showText
                    ? winrt::Windows::UI::Xaml::HorizontalAlignment::Left
                    : winrt::Windows::UI::Xaml::HorizontalAlignment::Center);
                m_artist.MaxWidth(viewport);
                auto clip = winrt::Windows::UI::Xaml::Media::RectangleGeometry();
                clip.Rect({ 0.0f, 0.0f, static_cast<float>(viewport), 15.0f });
                m_titleCanvas.Clip(clip);
                for (auto const& icon : m_nativeIcons)
                {
                    icon.Element.Visibility(winrt::Windows::UI::Xaml::Visibility::Collapsed);
                }
                m_island.Visibility(winrt::Windows::UI::Xaml::Visibility::Visible);
                ApplyIslandColors();
                RefreshArtwork();
                if (playing && m_artwork.Visibility() == winrt::Windows::UI::Xaml::Visibility::Visible)
                {
                    if (m_rotateStoryboard && !m_rotateRunning)
                    {
                        m_rotateStoryboard.Begin();
                        m_rotateRunning = true;
                    }
                }
                else
                {
                    if (m_rotateRunning && m_rotateStoryboard) m_rotateStoryboard.Stop();
                    m_rotateRunning = false;
                    if (m_artRotate) m_artRotate.Angle(0);
                }
                m_textPanel.Visibility(showText ? winrt::Windows::UI::Xaml::Visibility::Visible
                                                : winrt::Windows::UI::Xaml::Visibility::Collapsed);
                if (title != m_lastTitle)
                {
                    m_lastTitle = title;
                    m_scrollOffset = 0;
                    m_titleTransform.X(0);
                }
                m_title.Text(title);
                if (m_titleCopy) m_titleCopy.Text(title);
                m_artist.Text(artist);
                m_title.FontSize(showTitle ? 13.0 : 11.5);
                m_artist.Visibility(showTitle ? winrt::Windows::UI::Xaml::Visibility::Visible
                                               : winrt::Windows::UI::Xaml::Visibility::Collapsed);
            }
            else
            {
                m_island.Visibility(winrt::Windows::UI::Xaml::Visibility::Collapsed);
                m_scrollOffset = 0;
                if (m_titleTransform) m_titleTransform.X(0);
                for (auto const& icon : m_nativeIcons)
                {
                    icon.Element.Visibility(icon.Visibility);
                }
                for (auto const& child : m_original)
                {
                    child.Element.Visibility(child.Visibility);
                }
            }
        });
        m_timer.Start();
    }

    void ApplyIsland(winrt::Windows::UI::Xaml::Controls::Panel panel)
    {
        if (m_panel && m_panel != panel)
        {
            m_original.clear();
            m_island = nullptr;
            m_icon = nullptr;
            m_title = nullptr;
            m_artist = nullptr;
            m_textPanel = nullptr;
            m_panel = nullptr;
        }

        if (!m_island)
        {
            m_panel = panel;
            m_nativeWidth = panel.ActualWidth();
            if (m_nativeWidth < 20) m_nativeWidth = SearchModeWidth();

            m_original.clear();
            for (auto const& child : panel.Children())
            {
                m_original.push_back({ child, child.Visibility() });
            }

            auto border = winrt::Windows::UI::Xaml::Controls::Border();
            border.Background(winrt::Windows::UI::Xaml::Media::SolidColorBrush(
                winrt::Windows::UI::ColorHelper::FromArgb(230, 28, 28, 30)));
            border.BorderBrush(winrt::Windows::UI::Xaml::Media::SolidColorBrush(
                winrt::Windows::UI::ColorHelper::FromArgb(32, 255, 255, 255)));
            border.BorderThickness({ 1, 1, 1, 1 });
            border.CornerRadius({ 16, 16, 16, 16 });
            border.Padding({ 4, 0, 4, 0 });
            border.Height(32);
            border.Width((std::max)(32.0, m_nativeWidth - 4.0));
            border.HorizontalAlignment(winrt::Windows::UI::Xaml::HorizontalAlignment::Center);
            border.VerticalAlignment(winrt::Windows::UI::Xaml::VerticalAlignment::Center);
            border.Visibility(winrt::Windows::UI::Xaml::Visibility::Collapsed);

            auto row = winrt::Windows::UI::Xaml::Controls::StackPanel();
            row.Orientation(winrt::Windows::UI::Xaml::Controls::Orientation::Horizontal);
            row.Spacing(4);
            row.HorizontalAlignment(winrt::Windows::UI::Xaml::HorizontalAlignment::Center);
            row.VerticalAlignment(winrt::Windows::UI::Xaml::VerticalAlignment::Center);

            auto iconHost = winrt::Windows::UI::Xaml::Controls::Grid();
            iconHost.Width(16);
            iconHost.Height(16);
            iconHost.VerticalAlignment(winrt::Windows::UI::Xaml::VerticalAlignment::Center);

            auto icon = winrt::Windows::UI::Xaml::Controls::FontIcon();
            icon.Glyph(L"\uE8D6");
            icon.FontSize(14);
            icon.FontFamily(winrt::Windows::UI::Xaml::Media::FontFamily(L"Segoe Fluent Icons"));
            icon.Foreground(winrt::Windows::UI::Xaml::Media::SolidColorBrush(
                winrt::Windows::UI::ColorHelper::FromArgb(255, 255, 255, 255)));
            icon.HorizontalAlignment(winrt::Windows::UI::Xaml::HorizontalAlignment::Center);
            icon.VerticalAlignment(winrt::Windows::UI::Xaml::VerticalAlignment::Center);

            auto artwork = winrt::Windows::UI::Xaml::Shapes::Ellipse();
            artwork.Width(16);
            artwork.Height(16);
            artwork.Visibility(winrt::Windows::UI::Xaml::Visibility::Collapsed);
            auto artBrush = winrt::Windows::UI::Xaml::Media::ImageBrush();
            artBrush.Stretch(winrt::Windows::UI::Xaml::Media::Stretch::UniformToFill);
            artwork.Fill(artBrush);

            iconHost.Children().Append(icon);
            iconHost.Children().Append(artwork);

            auto textPanel = winrt::Windows::UI::Xaml::Controls::StackPanel();
            textPanel.Spacing(0);
            textPanel.VerticalAlignment(winrt::Windows::UI::Xaml::VerticalAlignment::Center);
            textPanel.Visibility(winrt::Windows::UI::Xaml::Visibility::Collapsed);

            auto title = winrt::Windows::UI::Xaml::Controls::TextBlock();
            title.Foreground(winrt::Windows::UI::Xaml::Media::SolidColorBrush(
                winrt::Windows::UI::ColorHelper::FromArgb(255, 255, 255, 255)));
            title.FontFamily(winrt::Windows::UI::Xaml::Media::FontFamily(L"Segoe UI Variable Text"));
            title.FontSize(11);
            title.FontWeight(winrt::Windows::UI::Text::FontWeights::SemiBold());
            title.TextWrapping(winrt::Windows::UI::Xaml::TextWrapping::NoWrap);
            title.TextTrimming(winrt::Windows::UI::Xaml::TextTrimming::None);

            auto titleCanvas = winrt::Windows::UI::Xaml::Controls::Canvas();
            titleCanvas.Height(15);
            titleCanvas.Clip(winrt::Windows::UI::Xaml::Media::RectangleGeometry());
            auto titleTransform = winrt::Windows::UI::Xaml::Media::TranslateTransform();
            title.RenderTransform(titleTransform);
            winrt::Windows::UI::Xaml::Controls::Canvas::SetLeft(title, 0);
            winrt::Windows::UI::Xaml::Controls::Canvas::SetTop(title, 0);
            titleCanvas.Children().Append(title);
            auto titleCopy = winrt::Windows::UI::Xaml::Controls::TextBlock();
            titleCopy.Foreground(winrt::Windows::UI::Xaml::Media::SolidColorBrush(
                winrt::Windows::UI::ColorHelper::FromArgb(255, 255, 255, 255)));
            titleCopy.FontFamily(winrt::Windows::UI::Xaml::Media::FontFamily(L"Segoe UI Variable Text"));
            titleCopy.FontSize(11);
            titleCopy.FontWeight(winrt::Windows::UI::Text::FontWeights::SemiBold());
            titleCopy.TextWrapping(winrt::Windows::UI::Xaml::TextWrapping::NoWrap);
            titleCopy.RenderTransform(titleTransform);
            winrt::Windows::UI::Xaml::Controls::Canvas::SetTop(titleCopy, 0);
            titleCanvas.Children().Append(titleCopy);

            auto artist = winrt::Windows::UI::Xaml::Controls::TextBlock();
            artist.Foreground(winrt::Windows::UI::Xaml::Media::SolidColorBrush(
                winrt::Windows::UI::ColorHelper::FromArgb(178, 255, 255, 255)));
            artist.FontFamily(winrt::Windows::UI::Xaml::Media::FontFamily(L"Segoe UI Variable Text"));
            artist.FontSize(9);
            artist.TextTrimming(winrt::Windows::UI::Xaml::TextTrimming::CharacterEllipsis);

            textPanel.Children().Append(titleCanvas);
            textPanel.Children().Append(artist);
            row.Children().Append(iconHost);
            row.Children().Append(textPanel);
            border.Child(row);
            panel.Children().Append(border);

            winrt::Windows::UI::Xaml::Automation::AutomationProperties::SetAutomationId(border, L"ShellIslandIsland");
            winrt::Windows::UI::Xaml::Automation::AutomationProperties::SetName(border, L"Shell Island");
            m_island = border;
            m_icon = icon;
            m_artwork = artwork;
            m_artBrush = artBrush;
            auto rotate = winrt::Windows::UI::Xaml::Media::RotateTransform();
            rotate.CenterX(8);
            rotate.CenterY(8);
            artwork.RenderTransform(rotate);
            auto spin = winrt::Windows::UI::Xaml::Media::Animation::DoubleAnimation();
            spin.From(0.0);
            spin.To(360.0);
            spin.Duration(winrt::Windows::UI::Xaml::Duration{
                winrt::Windows::Foundation::TimeSpan{ std::chrono::seconds(120) } });
            auto repeat = winrt::Windows::UI::Xaml::Media::Animation::RepeatBehavior{};
            repeat.Type = winrt::Windows::UI::Xaml::Media::Animation::RepeatBehaviorType::Forever;
            spin.RepeatBehavior(repeat);
            auto storyboard = winrt::Windows::UI::Xaml::Media::Animation::Storyboard();
            winrt::Windows::UI::Xaml::Media::Animation::Storyboard::SetTarget(spin, rotate);
            winrt::Windows::UI::Xaml::Media::Animation::Storyboard::SetTargetProperty(spin, L"Angle");
            storyboard.Children().Append(spin);
            m_artRotate = rotate;
            m_rotateStoryboard = storyboard;
            m_title = title;
            m_titleCopy = titleCopy;
            m_artist = artist;
            m_textPanel = textPanel;
            m_targetWidth = (std::max)(32.0, m_nativeWidth - 4.0);
            m_row = row;
            m_titleCanvas = titleCanvas;
            m_titleTransform = titleTransform;
            border.Tapped([this](winrt::Windows::Foundation::IInspectable const&,
                winrt::Windows::UI::Xaml::Input::TappedRoutedEventArgs const& args)
            {
                args.Handled(true);
                SignalOpen();
            });
            if (!m_scrollTimer)
            {
                m_scrollTimer = winrt::Windows::UI::Xaml::DispatcherTimer();
                m_scrollTimer.Interval(std::chrono::milliseconds(40));
                m_scrollTimer.Tick([this](auto const&, auto const&)
                {
                    if (!m_titleCanvas || !m_title || !m_titleTransform) return;
                    const double viewport = m_titleCanvas.ActualWidth();
                    const double textWidth = m_title.ActualWidth();
                    const double gap = 18.0;
                    if (m_titleCopy)
                    {
                        winrt::Windows::UI::Xaml::Controls::Canvas::SetLeft(m_titleCopy, textWidth + gap);
                    }
                    if (textWidth <= viewport + 2.0)
                    {
                        m_scrollOffset = 0;
                        m_titleTransform.X(0);
                        return;
                    }
                    const double stepWidth = textWidth + gap;
                    m_scrollOffset -= 1.2;
                    if (m_scrollOffset <= -stepWidth) m_scrollOffset += stepWidth;
                    m_titleTransform.X(m_scrollOffset);
                });
                m_scrollTimer.Start();
            }
        }

        StartMediaTimer();
    }
    void ModifySearch(InstanceHandle handle, InstanceHandle parentHandle, const std::wstring& typeName)
    {
        LogText("ModifySearch enter\r\n");
        try
        {
            char buffer[256];
            ::IInspectable* raw = nullptr;
            HRESULT hr = m_diag->GetIInspectableFromHandle(
                handle, reinterpret_cast<::IInspectable**>(&raw));
            wsprintfA(buffer, "GetIInspectable self hr=0x%08X raw=%p\r\n", hr, raw);
            LogText(buffer);
            if (FAILED(hr) || !raw)
            {
                ::IInspectable* parentRaw = nullptr;
                HRESULT parentHr = m_diag->GetIInspectableFromHandle(
                    parentHandle, reinterpret_cast<::IInspectable**>(&parentRaw));
                wsprintfA(buffer, "GetIInspectable parent hr=0x%08X raw=%p\r\n", parentHr, parentRaw);
                LogText(buffer);
                if (FAILED(parentHr) || !parentRaw) return;

                winrt::Windows::Foundation::IInspectable parent{ parentRaw, winrt::take_ownership_from_abi };
                if (auto parentControl = parent.try_as<winrt::Windows::UI::Xaml::Controls::ContentControl>())
                {
                    auto text = winrt::Windows::UI::Xaml::Controls::TextBlock();
                    text.Text(L"");
                    text.FontSize(9);
                    parentControl.Content(text);
                    StartMediaTimer();
                    LogText("Parent ContentControl replaced with ISLAND\r\n");
                }
                else if (auto parentPanel = parent.try_as<winrt::Windows::UI::Xaml::Controls::Panel>())
                {
                    ApplyIsland(parentPanel);
                    LogText("Parent Panel replaced with ISLAND\r\n");
                }
                else
                {
                    LogText("Parent is not ContentControl/Panel\r\n");
                }
                return;
            }

            winrt::Windows::Foundation::IInspectable object{ raw, winrt::take_ownership_from_abi };
            if (auto panel = object.try_as<winrt::Windows::UI::Xaml::Controls::Panel>())
            {
                ApplyIsland(panel);
                LogText("SearchButtonRootGrid replaced with media island\r\n");
            }
            else
            {
                LogText("SearchButtonRootGrid is not a Panel\r\n");
            }
        }
        catch (...)
        {
            LogText("ModifySearch exception\r\n");
        }
    }
    HRESULT STDMETHODCALLTYPE OnElementStateChanged(
        InstanceHandle, VisualElementState, LPCWSTR) override
    {
        return S_OK;
    }
};

class TapSite : public IObjectWithSite
{
    std::atomic<ULONG> m_ref{ 1 };
    IUnknown* m_site = nullptr;
    IXamlDiagnostics* m_diag = nullptr;
    IVisualTreeService3* m_service = nullptr;
    VisualCallback* m_callback = nullptr;
public:
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        if (riid == IID_IUnknown || riid == IID_IObjectWithSite)
        {
            *ppv = static_cast<IObjectWithSite*>(this);
            AddRef();
            return S_OK;
        }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }

    ULONG STDMETHODCALLTYPE AddRef() override { return ++m_ref; }
    ULONG STDMETHODCALLTYPE Release() override
    {
        ULONG value = --m_ref;
        if (!value) delete this;
        return value;
    }

    HRESULT STDMETHODCALLTYPE SetSite(IUnknown* site) override
    {
        LogText("TapSite::SetSite\r\n");
        if (m_site) { m_site->Release(); m_site = nullptr; }
        if (m_diag) { m_diag->Release(); m_diag = nullptr; }
        if (m_service) { m_service->Release(); m_service = nullptr; }
        if (m_callback) { m_callback->Release(); m_callback = nullptr; }

        m_site = site;
        if (m_site) m_site->AddRef();
        if (!site) return S_OK;

        HRESULT hr = site->QueryInterface(IID_PPV_ARGS(&m_diag));
        if (FAILED(hr)) { LogText("QI IXamlDiagnostics failed\r\n"); return hr; }
        hr = m_diag->QueryInterface(IID_PPV_ARGS(&m_service));
        if (FAILED(hr)) { LogText("QI IVisualTreeService3 failed\r\n"); return hr; }

        m_callback = new VisualCallback(m_diag);
        hr = m_service->AdviseVisualTreeChange(m_callback);
        char buffer[128];
        wsprintfA(buffer, "AdviseVisualTreeChange hr=0x%08X\r\n", hr);
        LogText(buffer);
        ::IInspectable* layerRaw = nullptr;
        HRESULT layerHr = m_diag->GetUiLayer(&layerRaw);
        char layerBuffer[128];
        wsprintfA(layerBuffer, "GetUiLayer hr=0x%08X\\r\\n", layerHr);
        LogText(layerBuffer);
        if (SUCCEEDED(layerHr) && layerRaw)
        {
            winrt::Windows::Foundation::IInspectable layer{ layerRaw, winrt::take_ownership_from_abi };
            m_callback->TryApplyExisting(layer);
        }
        std::thread(EnsureSearchTree).detach();
        return hr;
    }

    HRESULT STDMETHODCALLTYPE GetSite(REFIID riid, void** ppv) override
    {
        if (!m_site) return E_FAIL;
        return m_site->QueryInterface(riid, ppv);
    }
};

class TapFactory : public IClassFactory
{
    std::atomic<ULONG> m_ref{ 1 };
public:
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        if (riid == IID_IUnknown || riid == IID_IClassFactory)
        {
            *ppv = static_cast<IClassFactory*>(this);
            AddRef();
            return S_OK;
        }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }

    ULONG STDMETHODCALLTYPE AddRef() override { return ++m_ref; }
    ULONG STDMETHODCALLTYPE Release() override
    {
        ULONG value = --m_ref;
        if (!value) delete this;
        return value;
    }

    HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* outer, REFIID riid, void** ppv) override
    {
        if (outer) return CLASS_E_NOAGGREGATION;
        auto site = new TapSite();
        HRESULT hr = site->QueryInterface(riid, ppv);
        site->Release();
        return hr;
    }

    HRESULT STDMETHODCALLTYPE LockServer(BOOL) override { return S_OK; }
};

STDAPI DllGetClassObject(
    REFCLSID rclsid, REFIID riid, void** ppv)
{
    if (rclsid != CLSID_XamlTap)
    {
        return CLASS_E_CLASSNOTAVAILABLE;
    }

    auto factory = new TapFactory();
    HRESULT hr = factory->QueryInterface(riid, ppv);
    factory->Release();
    return hr;
}

STDAPI DllCanUnloadNow()
{
    return S_FALSE;
}

static DWORD WINAPI InstallThread(LPVOID)
{
    LogText("InstallThread start\r\n");
    HMODULE xaml = LoadLibraryExW(L"Windows.UI.Xaml.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (!xaml) { LogText("LoadLibrary Windows.UI.Xaml failed\r\n"); return 1; }
    auto init = reinterpret_cast<decltype(&InitializeXamlDiagnosticsEx)>(
        GetProcAddress(xaml, "InitializeXamlDiagnosticsEx"));
    if (!init) { LogText("GetProcAddress failed\r\n"); return 2; }

    wchar_t dllPath[MAX_PATH];
    GetModuleFileNameW(nullptr, dllPath, MAX_PATH);
    // GetModuleFileName gives explorer.exe; use this DLL via an argument is unavailable,
    // so derive the TAP DLL path from the module handle through a global set in DllMain.
    extern HMODULE g_module;
    GetModuleFileNameW(g_module, dllPath, MAX_PATH);

    for (int attempt = 1; attempt <= 600; ++attempt)
    {
        wchar_t endpoint[64];
        wsprintfW(endpoint, L"VisualDiagConnection%d", attempt);
        HRESULT hr = init(endpoint, GetCurrentProcessId(), nullptr, dllPath, CLSID_XamlTap, nullptr);
        char buffer[128];
        if (attempt == 1 || attempt % 10 == 0)
        {
            wsprintfA(buffer, "InitializeXamlDiagnosticsEx attempt=%d hr=0x%08X\r\n", attempt, hr);
            LogText(buffer);
        }
        if (SUCCEEDED(hr)) return 0;
        Sleep(500);
    }
    return 3;
}

HMODULE g_module = nullptr;

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        DisableThreadLibraryCalls(module);
        g_module = module;
        LogText("XamlTap loaded\r\n");
        CreateThread(nullptr, 0, InstallThread, nullptr, 0, nullptr);
    }
    return TRUE;
}
