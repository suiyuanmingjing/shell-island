using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace ShellIsland.Services;

public static class SearchButtonLocator
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    public static (int Left, int Top, int Right, int Bottom) Rect { get; private set; }

    public static void Refresh()
    {
        try
        {
            var tray = FindWindow("Shell_TrayWnd", null);
            if (tray == IntPtr.Zero)
            {
                Rect = default;
                return;
            }

            var root = AutomationElement.FromHandle(tray);
            var condition = new PropertyCondition(AutomationElement.AutomationIdProperty, "SearchButton");
            var element = root.FindFirst(TreeScope.Descendants, condition);
            if (element is null)
            {
                Rect = default;
                return;
            }

            var bounds = element.Current.BoundingRectangle;
            Rect = bounds.IsEmpty
                ? default
                : ((int)bounds.Left, (int)bounds.Top, (int)bounds.Right, (int)bounds.Bottom);
        }
        catch
        {
            Rect = default;
        }
    }
}
