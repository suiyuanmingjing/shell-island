#include <windows.h>
#include <cstdlib>

int wmain(int argc, wchar_t** argv)
{
    if (argc < 3) return 2;
    int x = _wtoi(argv[1]);
    int y = _wtoi(argv[2]);
    SetCursorPos(x, y);
    mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0);
    Sleep(50);
    mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 0);
    return 0;
}
