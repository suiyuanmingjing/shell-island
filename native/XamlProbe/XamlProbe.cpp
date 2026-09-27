#include <windows.h>
#include <string>

static void Log(const char* text)
{
    wchar_t path[MAX_PATH];
    DWORD len = GetTempPathW(MAX_PATH, path);
    if (!len) return;
    lstrcatW(path, L"XamlProbe.log");
    HANDLE file = CreateFileW(path, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr,
        OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return;
    DWORD written = 0;
    WriteFile(file, text, (DWORD)lstrlenA(text), &written, nullptr);
    CloseHandle(file);
}

static void ProbeModule(const wchar_t* name)
{
    HMODULE module = GetModuleHandleW(name);
    char line[512];
    if (!module)
    {
        wsprintfA(line, "module %ls: not loaded\r\n", name);
        Log(line);
        return;
    }

    wchar_t path[MAX_PATH];
    GetModuleFileNameW(module, path, MAX_PATH);
    wsprintfA(line, "module %ls: base=%p path=%ls\r\n", name, module, path);
    Log(line);
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        DisableThreadLibraryCalls(module);
        Log("XamlProbe loaded\r\n");
        ProbeModule(L"Windows.UI.Xaml.dll");
        ProbeModule(L"Windows.UI.Composition.dll");
        ProbeModule(L"Taskbar.dll");
        ProbeModule(L"ExplorerFrame.dll");
        ProbeModule(L"StartUI.dll");
        ProbeModule(L"SearchUx.dll");
        ProbeModule(L"ShellExperienceHost.dll");

        HMODULE xaml = GetModuleHandleW(L"Windows.UI.Xaml.dll");
        if (xaml)
        {
            FARPROC diag = GetProcAddress(xaml, "InitializeXamlDiagnosticsEx");
            char line[256];
            wsprintfA(line, "InitializeXamlDiagnosticsEx=%p\r\n", diag);
            Log(line);
        }

        HWND tray = FindWindowW(L"Shell_TrayWnd", nullptr);
        char trayLine[128];
        wsprintfA(trayLine, "Shell_TrayWnd=%p\r\n", tray);
        Log(trayLine);
    }
    return TRUE;
}
