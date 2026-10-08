using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using ShellIsland.Controls;

namespace ShellIsland.Services;

public sealed class TaskbarIslandHost : IDisposable
{
    private HwndSource? _source;
    private TaskbarIslandView? _view;

    public TaskbarIslandView? View => _view;
    public event Action? Clicked;

    public bool Attach()
    {
        try
        {
            var taskbar = new TaskbarService().GetTaskbarHandle();
            if (taskbar == IntPtr.Zero)
            {
                return false;
            }

            StartButtonLocator.Refresh();
            var start = StartButtonLocator.Rect;
            if (start.Right <= start.Left)
            {
                return false;
            }

            var parameters = new HwndSourceParameters("ShellIslandTaskbarIsland")
            {
                ParentWindow = taskbar,
                WindowStyle = 0x40000000 | 0x10000000, // WS_CHILD | WS_VISIBLE
                PositionX = start.Right,
                PositionY = 0,
                Width = 44,
                Height = 48,
                UsesPerPixelOpacity = false,
            };

            _source = new HwndSource(parameters);
            _view = new TaskbarIslandView();
            _view.Clicked += () => Clicked?.Invoke();
            _source.RootVisual = _view;
            SetWindowPos(_source.Handle, IntPtr.Zero, start.Right, 0, 44, 48, 0x0040 | 0x0010);
            return true;
        }
        catch (Exception ex)
        {
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShellIsland");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "error.log"), $"[{DateTime.Now:O}] island attach: {ex}\n");
            }
            catch
            {
                // ignore
            }

            return false;
        }
    }

    private static FrameworkElement BuildContent()
    {
        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(28, 28, 30)),
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(2),
        };
        border.Child = new TextBlock
        {
            Text = "ISLAND",
            Foreground = Brushes.White,
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        return border;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    public void Dispose()
    {
        _source?.Dispose();
        _source = null;
    }
}
