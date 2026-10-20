#include <windows.h>
#include <ocidl.h>
#include <xamlOM.h>
#include <string>
#include <atomic>

// {6E2A4C1B-7F3D-4D2A-9C5E-1B2A3C4D5E6F}
static const CLSID CLSID_XamlTap =
{ 0x6e2a4c1b, 0x7f3d, 0x4d2a, { 0x9c, 0x5e, 0x1b, 0x2a, 0x3c, 0x4d, 0x5e, 0x6f } };

extern "C" const IID IID_IVisualTreeServiceCallback =
{ 0xaa7a8931, 0x80e4, 0x4fec, { 0x8f, 0x3b, 0x55, 0x3f, 0x87, 0xb4, 0x96, 0x6e } };
extern "C" const IID IID_IVisualTreeServiceCallback2 =
{ 0xbad9eb88, 0xae77, 0x4397, { 0xb9, 0x48, 0x5f, 0xa2, 0xdb, 0x0a, 0x19, 0xea } };
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
    wsprintfA(buffer, "%s handle=%llu type=%ls name=%ls\r\n",
        prefix, (unsigned long long)element.Handle, type, name);
    LogText(buffer);
}

class VisualCallback : public IVisualTreeServiceCallback2
{
    std::atomic<ULONG> m_ref{ 1 };
public:
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
        ParentChildRelation, VisualElement element, VisualMutationType mutationType) override
    {
        LogElement(mutationType == Add ? "ADD" : "REMOVE", element);
        if (element.Type) SysFreeString(element.Type);
        if (element.Name) SysFreeString(element.Name);
        if (element.SrcInfo.FileName) SysFreeString(element.SrcInfo.FileName);
        if (element.SrcInfo.Hash) SysFreeString(element.SrcInfo.Hash);
        return S_OK;
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

        m_callback = new VisualCallback();
        hr = m_service->AdviseVisualTreeChange(m_callback);
        char buffer[128];
        wsprintfA(buffer, "AdviseVisualTreeChange hr=0x%08X\r\n", hr);
        LogText(buffer);
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

    for (int attempt = 1; attempt <= 60; ++attempt)
    {
        wchar_t endpoint[64];
        wsprintfW(endpoint, L"VisualDiagConnection%d", attempt);
        HRESULT hr = init(endpoint, GetCurrentProcessId(), nullptr, dllPath, CLSID_XamlTap, nullptr);
        char buffer[128];
        wsprintfA(buffer, "InitializeXamlDiagnosticsEx attempt=%d hr=0x%08X\r\n", attempt, hr);
        LogText(buffer);
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
