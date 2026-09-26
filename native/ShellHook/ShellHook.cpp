#include <windows.h>
#include <commctrl.h>
#include <UIAutomation.h>
#include <string>

#pragma comment(lib, "uiautomationcore.lib")
#pragma comment(lib, "comctl32.lib")
#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "oleaut32.lib")

static HWND g_tray = nullptr;
static RECT g_startRect = {};
static volatile LONG g_subclassed = 0;

static void LogLine(const char* text)
{
    wchar_t path[MAX_PATH];
    DWORD len = GetTempPathW(MAX_PATH, path);
    if (len == 0 || len + 32 >= MAX_PATH) return;
    lstrcatW(path, L"ShellHook.log");
    HANDLE file = CreateFileW(path, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE,
        nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return;
    DWORD written = 0;
    WriteFile(file, text, (DWORD)lstrlenA(text), &written, nullptr);
    CloseHandle(file);
}

static void LogRect(const char* prefix, const RECT& r)
{
    char buffer[256];
    wsprintfA(buffer, "%s %ld,%ld,%ld,%ld\r\n", prefix, r.left, r.top, r.right, r.bottom);
    LogLine(buffer);
}

static bool FindStartRect(RECT* out)
{
    IUIAutomation* automation = nullptr;
    if (FAILED(CoCreateInstance(__uuidof(CUIAutomation), nullptr, CLSCTX_INPROC_SERVER,
        IID_PPV_ARGS(&automation))))
    {
        return false;
    }

    bool found = false;
    IUIAutomationElement* root = nullptr;
    if (SUCCEEDED(automation->ElementFromHandle(g_tray, &root)) && root)
    {
        VARIANT value;
        VariantInit(&value);
        value.vt = VT_BSTR;
        value.bstrVal = SysAllocString(L"StartButton");
        IUIAutomationCondition* condition = nullptr;
        if (SUCCEEDED(automation->CreatePropertyCondition(UIA_AutomationIdPropertyId, value, &condition)) && condition)
        {
            IUIAutomationElement* element = nullptr;
            if (SUCCEEDED(root->FindFirst(TreeScope_Descendants, condition, &element)) && element)
            {
                RECT rect;
                if (SUCCEEDED(element->get_CurrentBoundingRectangle(&rect)))
                {
                    *out = rect;
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

static void SignalEvent(const wchar_t* name)
{
    HANDLE event = OpenEventW(EVENT_MODIFY_STATE, FALSE, name);
    if (event)
    {
        SetEvent(event);
        CloseHandle(event);
    }
}

static LRESULT CALLBACK TrayProc(HWND hWnd, UINT msg, WPARAM wParam, LPARAM lParam,
    UINT_PTR, DWORD_PTR)
{
    if (msg == WM_LBUTTONDOWN || msg == WM_LBUTTONUP || msg == WM_LBUTTONDBLCLK)
    {
        POINT pt;
        GetCursorPos(&pt);
        if (PtInRect(&g_startRect, pt))
        {
            if (msg == WM_LBUTTONDOWN)
            {
                LogLine("Start click down suppressed\r\n");
            }
            else if (msg == WM_LBUTTONUP)
            {
                LogLine("Start click up -> ShellIslandStart\r\n");
                SignalEvent(L"ShellIslandStart");
            }
            return 0;
        }
    }

    return DefSubclassProc(hWnd, msg, wParam, lParam);
}


static HHOOK g_mouseHook = nullptr;

static LRESULT CALLBACK MouseProc(int nCode, WPARAM wParam, LPARAM lParam)
{
    if (nCode >= 0 && (wParam == WM_LBUTTONDOWN || wParam == WM_LBUTTONUP || wParam == WM_LBUTTONDBLCLK))
    {
        MSLLHOOKSTRUCT* info = reinterpret_cast<MSLLHOOKSTRUCT*>(lParam);
        if (PtInRect(&g_startRect, info->pt))
        {
            HANDLE receiver = OpenEventW(EVENT_MODIFY_STATE, FALSE, L"ShellIslandStart");
            if (!receiver)
            {
                return CallNextHookEx(g_mouseHook, nCode, wParam, lParam);
            }

            if (wParam == WM_LBUTTONDOWN)
            {
                LogLine("LL Start down suppressed\r\n");
                CloseHandle(receiver);
                return 1;
            }

            if (wParam == WM_LBUTTONUP)
            {
                LogLine("LL Start up -> ShellIslandStart\r\n");
                SetEvent(receiver);
                CloseHandle(receiver);
                return 1;
            }

            CloseHandle(receiver);
        }
    }

    return CallNextHookEx(g_mouseHook, nCode, wParam, lParam);
}

static DWORD WINAPI MouseThread(LPVOID)
{
    g_mouseHook = SetWindowsHookExW(WH_MOUSE_LL, MouseProc, GetModuleHandleW(L"ShellHook.dll"), 0);
    if (!g_mouseHook)
    {
        LogLine("LL mouse hook failed\r\n");
        return 1;
    }

    LogLine("LL mouse hook installed\r\n");
    MSG msg;
    while (GetMessageW(&msg, nullptr, 0, 0))
    {
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }

    return 0;
}
static DWORD WINAPI Worker(LPVOID)
{
    CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    LogLine("ShellHook worker started\r\n");

    for (int i = 0; i < 120; ++i)
    {
        g_tray = FindWindowW(L"Shell_TrayWnd", nullptr);
        if (g_tray)
        {
            if (InterlockedCompareExchange(&g_subclassed, 1, 0) == 0)
            {
                SetWindowSubclass(g_tray, TrayProc, 0x5348, 0);
                LogLine("taskbar subclass installed\r\n");
            }

            RECT rect;
            if (FindStartRect(&rect) && rect.right > rect.left)
            {
                if (!EqualRect(&g_startRect, &rect))
                {
                    g_startRect = rect;
                    LogRect("Start rect:", g_startRect);
                }
            }
        }

        Sleep(1000);
    }

    CoUninitialize();
    return 0;
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        DisableThreadLibraryCalls(module);
        char buffer[128];
        wsprintfA(buffer, "ShellHook loaded in pid=%lu\r\n", GetCurrentProcessId());
        LogLine(buffer);
        CreateThread(nullptr, 0, Worker, nullptr, 0, nullptr);
        CreateThread(nullptr, 0, MouseThread, nullptr, 0, nullptr);
    }
    return TRUE;
}
