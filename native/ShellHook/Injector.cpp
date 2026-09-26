#include <windows.h>
#include <tlhelp32.h>
#include <cstdio>

static DWORD FindProcess(const wchar_t* name)
{
    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snap == INVALID_HANDLE_VALUE) return 0;
    PROCESSENTRY32W entry{};
    entry.dwSize = sizeof(entry);
    DWORD pid = 0;
    if (Process32FirstW(snap, &entry))
    {
        do
        {
            if (!_wcsicmp(entry.szExeFile, name))
            {
                pid = entry.th32ProcessID;
                break;
            }
        } while (Process32NextW(snap, &entry));
    }
    CloseHandle(snap);
    return pid;
}

int wmain(int argc, wchar_t** argv)
{
    if (argc < 2)
    {
        wprintf(L"usage: Injector.exe <absolute-dll-path>\n");
        return 2;
    }

    const wchar_t* dll = argv[1];
    DWORD pid = FindProcess(L"explorer.exe");
    if (!pid)
    {
        wprintf(L"explorer.exe not found\n");
        return 3;
    }

    HANDLE process = OpenProcess(
        PROCESS_CREATE_THREAD | PROCESS_QUERY_INFORMATION | PROCESS_VM_OPERATION | PROCESS_VM_WRITE,
        FALSE, pid);
    if (!process)
    {
        wprintf(L"OpenProcess failed: %lu\n", GetLastError());
        return 4;
    }

    SIZE_T bytes = (wcslen(dll) + 1) * sizeof(wchar_t);
    void* remote = VirtualAllocEx(process, nullptr, bytes, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (!remote)
    {
        wprintf(L"VirtualAllocEx failed: %lu\n", GetLastError());
        CloseHandle(process);
        return 5;
    }

    if (!WriteProcessMemory(process, remote, dll, bytes, nullptr))
    {
        wprintf(L"WriteProcessMemory failed: %lu\n", GetLastError());
        CloseHandle(process);
        return 6;
    }

    HMODULE kernel = GetModuleHandleW(L"kernel32.dll");
    auto loadLibrary = reinterpret_cast<LPTHREAD_START_ROUTINE>(
        GetProcAddress(kernel, "LoadLibraryW"));
    HANDLE thread = CreateRemoteThread(process, nullptr, 0, loadLibrary, remote, 0, nullptr);
    if (!thread)
    {
        wprintf(L"CreateRemoteThread failed: %lu\n", GetLastError());
        CloseHandle(process);
        return 7;
    }

    WaitForSingleObject(thread, 10000);
    wprintf(L"injected pid=%lu dll=%ls\n", pid, dll);
    CloseHandle(thread);
    CloseHandle(process);
    return 0;
}
