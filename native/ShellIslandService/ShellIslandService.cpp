#include <windows.h>
#include <tlhelp32.h>
#include <string>

static SERVICE_STATUS_HANDLE g_statusHandle = nullptr;
static SERVICE_STATUS g_status{};
static HANDLE g_stopEvent = nullptr;
static wchar_t g_dllPath[MAX_PATH] = {};
static wchar_t g_sourceDll[MAX_PATH] = {};

static void LogLine(const char* text)
{
    CreateDirectoryW(L"C:\\ProgramData\\ShellIsland", nullptr);
    HANDLE file = CreateFileW(L"C:\\ProgramData\\ShellIsland\\injector.log",
        FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return;
    DWORD written = 0;
    WriteFile(file, text, (DWORD)lstrlenA(text), &written, nullptr);
    CloseHandle(file);
}

static DWORD FindExplorer()
{
    HWND shell = GetShellWindow();
    if (shell)
    {
        DWORD shellPid = 0;
        GetWindowThreadProcessId(shell, &shellPid);
        if (shellPid != 0) return shellPid;
    }

    HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snapshot == INVALID_HANDLE_VALUE) return 0;
    PROCESSENTRY32W entry{};
    entry.dwSize = sizeof(entry);
    DWORD pid = 0;
    if (Process32FirstW(snapshot, &entry))
    {
        do
        {
            if (!_wcsicmp(entry.szExeFile, L"explorer.exe"))
            {
                pid = entry.th32ProcessID;
                break;
            }
        } while (Process32NextW(snapshot, &entry));
    }
    CloseHandle(snapshot);
    return pid;
}

static bool Inject(DWORD pid)
{
    HANDLE process = OpenProcess(
        PROCESS_CREATE_THREAD | PROCESS_QUERY_INFORMATION | PROCESS_VM_OPERATION | PROCESS_VM_WRITE,
        FALSE, pid);
    if (!process)
    {
        char buffer[128];
        wsprintfA(buffer, "OpenProcess failed: %lu\r\n", GetLastError());
        LogLine(buffer);
        return false;
    }

    SIZE_T bytes = (lstrlenW(g_dllPath) + 1) * sizeof(wchar_t);
    void* remote = VirtualAllocEx(process, nullptr, bytes, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (!remote)
    {
        LogLine("VirtualAllocEx failed\r\n");
        CloseHandle(process);
        return false;
    }

    if (!WriteProcessMemory(process, remote, g_dllPath, bytes, nullptr))
    {
        LogLine("WriteProcessMemory failed\r\n");
        CloseHandle(process);
        return false;
    }

    HMODULE kernel = GetModuleHandleW(L"kernel32.dll");
    auto loadLibrary = reinterpret_cast<LPTHREAD_START_ROUTINE>(GetProcAddress(kernel, "LoadLibraryW"));
    HANDLE thread = CreateRemoteThread(process, nullptr, 0, loadLibrary, remote, 0, nullptr);
    if (!thread)
    {
        LogLine("CreateRemoteThread failed\r\n");
        CloseHandle(process);
        return false;
    }

    WaitForSingleObject(thread, 5000);
    CloseHandle(thread);
    CloseHandle(process);
    char buffer[128];
    wsprintfA(buffer, "injected pid=%lu\r\n", pid);
    LogLine(buffer);
    return true;
}

static bool InjectCopy(DWORD pid, int attempt)
{
    CreateDirectoryW(L"C:\\ProgramData\\ShellIsland", nullptr);
    wchar_t copyPath[MAX_PATH];
    wsprintfW(copyPath, L"C:\\ProgramData\\ShellIsland\\XamlTap_%lu_%d.dll", pid, attempt);
    if (!CopyFileW(g_sourceDll, copyPath, FALSE))
    {
        char buffer[160];
        wsprintfA(buffer, "CopyFile failed: %lu\r\n", GetLastError());
        LogLine(buffer);
        return false;
    }

    lstrcpynW(g_dllPath, copyPath, MAX_PATH);
    return Inject(pid);
}

static DWORD WINAPI Worker(LPVOID)
{
    DWORD lastPid = 0;
    ULONGLONG lastAttempt = 0;
    int attempt = 0;

    while (WaitForSingleObject(g_stopEvent, 250) == WAIT_TIMEOUT)
    {
        DWORD pid = FindExplorer();
        if (pid == 0)
        {
            lastPid = 0;
            continue;
        }

        const ULONGLONG now = GetTickCount64();
        if (pid != lastPid || now - lastAttempt >= 180000)
        {
            ++attempt;
            InjectCopy(pid, attempt);
            lastPid = pid;
            lastAttempt = now;
        }
    }
    return 0;
}

static void WINAPI Handler(DWORD control)
{
    if (control == SERVICE_CONTROL_STOP)
    {
        g_status.dwCurrentState = SERVICE_STOP_PENDING;
        SetServiceStatus(g_statusHandle, &g_status);
        SetEvent(g_stopEvent);
    }
}

static void WINAPI ServiceMain(DWORD, LPWSTR*)
{
    g_statusHandle = RegisterServiceCtrlHandlerW(L"ShellIslandInjector", Handler);
    if (!g_statusHandle) return;

    g_status.dwServiceType = SERVICE_WIN32_OWN_PROCESS;
    g_status.dwCurrentState = SERVICE_START_PENDING;
    g_status.dwControlsAccepted = 0;
    SetServiceStatus(g_statusHandle, &g_status);

    GetModuleFileNameW(nullptr, g_sourceDll, MAX_PATH);
    wchar_t* slash = wcsrchr(g_sourceDll, L'\\');
    if (slash) *(slash + 1) = L'\0';
    lstrcatW(g_sourceDll, L"XamlTap.dll");
    lstrcpynW(g_dllPath, g_sourceDll, MAX_PATH);

    g_stopEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    CreateThread(nullptr, 0, Worker, nullptr, 0, nullptr);

    g_status.dwCurrentState = SERVICE_RUNNING;
    g_status.dwControlsAccepted = SERVICE_ACCEPT_STOP;
    SetServiceStatus(g_statusHandle, &g_status);
    LogLine("service started\r\n");

    WaitForSingleObject(g_stopEvent, INFINITE);
    g_status.dwCurrentState = SERVICE_STOPPED;
    SetServiceStatus(g_statusHandle, &g_status);
}

int wmain()
{
    SERVICE_TABLE_ENTRYW table[] =
    {
        { (LPWSTR)L"ShellIslandInjector", ServiceMain },
        { nullptr, nullptr }
    };

    if (!StartServiceCtrlDispatcherW(table))
    {
        if (GetLastError() == ERROR_FAILED_SERVICE_CONTROLLER_CONNECT)
        {
            // Console debug mode: inject once and exit.
            GetModuleFileNameW(nullptr, g_sourceDll, MAX_PATH);
            wchar_t* slash = wcsrchr(g_sourceDll, L'\\');
            if (slash) *(slash + 1) = L'\0';
            lstrcatW(g_sourceDll, L"XamlTap.dll");
            DWORD pid = FindExplorer();
            if (pid) InjectCopy(pid, 1);
            return 0;
        }
        return 1;
    }
    return 0;
}
