using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace ShellIsland.Services;

public readonly record struct ShellRect(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;
    public double Bottom => Top + Height;
}

public sealed class TaskbarService
{
    private const int SwpNoSize = 0x0001;
    private const int SwpNoMove = 0x0002;
    private const int SwpNoActivate = 0x0010;
    private static readonly IntPtr HwndTopmost = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string? className, string? windowName);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    public IntPtr GetTaskbarHandle() => FindWindow("Shell_TrayWnd", null);

    public ShellRect GetTaskbar(Window window) => ToDip(window, GetTaskbarPixels());

    public ShellRect GetStart(Window window) => ToDip(window, GetStartPixels());

    public (int Left, int Top, int Right, int Bottom) GetStartScreenRect()
    {
        var r = GetStartPixels();
        return (r.Left, r.Top, r.Right, r.Bottom);
    }

    public void BringToTop(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    public bool IsTaskbarVisible()
    {
        var tray = FindWindow("Shell_TrayWnd", null);
        return tray != IntPtr.Zero && IsWindowVisible(tray);
    }

    public void PositionOverlay(Window window, double widthDip = 188)
    {
        if (!GetWindowRect(FindWindow("Shell_TrayWnd", null), out var tray))
        {
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(window);
        var start = GetStartPixels();
        var startLeft = start.Right > start.Left ? start.Left : tray.Left + 8;
        var width = Math.Max(widthDip, (start.Right - startLeft) / dpi.DpiScaleX + 120);
        window.Left = startLeft / dpi.DpiScaleX;
        window.Top = tray.Top / dpi.DpiScaleY;
        window.Width = width;
        window.Height = (tray.Bottom - tray.Top) / dpi.DpiScaleY;

        var hwnd = new WindowInteropHelper(window).Handle;
        SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    private ShellRect ToDip(Window window, Rect rect)
    {
        var dpi = VisualTreeHelper.GetDpi(window);
        return new ShellRect(
            rect.Left / dpi.DpiScaleX,
            rect.Top / dpi.DpiScaleY,
            (rect.Right - rect.Left) / dpi.DpiScaleX,
            (rect.Bottom - rect.Top) / dpi.DpiScaleY);
    }

    private Rect GetTaskbarPixels()
    {
        GetWindowRect(FindWindow("Shell_TrayWnd", null), out var rect);
        return rect;
    }

    private Rect GetStartPixels()
    {
        var tray = FindWindow("Shell_TrayWnd", null);
        var start = tray == IntPtr.Zero ? IntPtr.Zero : FindWindowEx(tray, IntPtr.Zero, "Start", null);
        if (start == IntPtr.Zero || !GetWindowRect(start, out var rect))
        {
            return default;
        }

        return rect;
    }
}
