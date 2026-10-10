using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ShellIsland.Models;
using ShellIsland.Services;

namespace ShellIsland;

public partial class StartMenuWindow : Window
{
    private readonly MainWindow _host;
    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _keepOpen;

    public StartMenuWindow(MainWindow host)
    {
        _host = host;
        InitializeComponent();
        Deactivated += (_, _) =>
        {
            if (!_keepOpen)
            {
                HidePanel();
            }
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                HidePanel();
                e.Handled = true;
            }
        };
        _clockTimer.Tick += (_, _) => UpdateClock();
        _clockTimer.Start();
        UpdateClock();
        LoadPhotoTile();
        BuildPowerMenu();
        AppsList.ItemsSource = _host.Search.Apps;
        AppCount.Text = $"{_host.Search.Apps.Count} 个应用";
    }

    public async void ShowFor()
    {
        var taskbar = _host.Taskbar.GetTaskbar(this);
        var work = SystemParameters.WorkArea;
        StartButtonLocator.Refresh();
        var rect = StartButtonLocator.Rect;
        var dpi = VisualTreeHelper.GetDpi(this);
        var startLeft = rect.Right > rect.Left ? rect.Left / dpi.DpiScaleX : _host.Taskbar.GetTaskbar(this).Left;
        Height = Math.Min(760, Math.Max(520, taskbar.Top - work.Top - 16));
        Left = Math.Clamp(startLeft - 4, work.Left + 8, work.Right - Width - 8);
        Top = Math.Max(work.Top + 8, taskbar.Top - Height - 6);
        Show();
        Activate();
        SearchBox.Focus();
        SearchBox.SelectAll();
        await _host.Search.LoadAsync();
        AppsList.ItemsSource = _host.Search.Apps;
        AppCount.Text = $"{_host.Search.Apps.Count} 个应用";
        RefreshPinnedApps();
        UpdateClock();
    }

    public void HidePanel()
    {
        Hide();
        _keepOpen = false;
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        ClockTime.Text = now.ToString("HH:mm");
        ClockDate.Text = now.ToString("M月d日 dddd", new System.Globalization.CultureInfo("zh-CN"));
    }

    private void LoadPhotoTile()
    {
        try
        {
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var wallpapers = new[]
            {
                Path.Combine(windows, "Web", "4K", "Wallpaper", "Windows", "img0.jpg"),
                Path.Combine(windows, "Web", "Wallpaper", "Windows", "img0.jpg"),
                Path.Combine(windows, "Web", "4K", "Wallpaper", "Windows", "img1.jpg"),
            };
            var file = wallpapers.FirstOrDefault(File.Exists);
            if (file is null)
            {
                var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
                file = Directory.Exists(pictures)
                    ? Directory.EnumerateFiles(pictures, "*.*", SearchOption.TopDirectoryOnly)
                        .FirstOrDefault(p => p.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                                             p.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                    : null;
            }
            if (file is null)
            {
                return;
            }

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(file);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 520;
            bitmap.EndInit();
            bitmap.Freeze();
            PhotoTile.Source = bitmap;
            PhotoFallback.Visibility = Visibility.Collapsed;
        }
        catch
        {
            // keep the gradient fallback
        }
    }

    private void BuildPowerMenu()
    {
        var menu = new ContextMenu();
        menu.Opened += (_, _) => _keepOpen = true;
        menu.Closed += (_, _) => _keepOpen = false;
        menu.Items.Add(MenuItem("锁定", () => Run("rundll32.exe", "user32.dll,LockWorkStation")));
        menu.Items.Add(MenuItem("注销", () => Run("shutdown.exe", "/l")));
        menu.Items.Add(MenuItem("重启", () => Run("shutdown.exe", "/r /t 0")));
        menu.Items.Add(MenuItem("关机", () => Run("shutdown.exe", "/s /t 0")));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("退出 ShellIsland", () => Application.Current.Shutdown()));
        PowerButton.ContextMenu = menu;
    }

    private static MenuItem MenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    private void PowerButton_Click(object sender, RoutedEventArgs e)
    {
        if (PowerButton.ContextMenu is null)
        {
            return;
        }

        PowerButton.ContextMenu.PlacementTarget = PowerButton;
        PowerButton.ContextMenu.IsOpen = true;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var text = SearchBox.Text.Trim();
        SearchHint.Visibility = text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var results = _host.Search.Search(text, 200);
        AppsList.ItemsSource = results;
        AppCount.Text = text.Length == 0 ? $"{results.Count} 个应用" : $"{results.Count} 个结果";
        if (results.Count > 0)
        {
            AppsList.SelectedIndex = 0;
        }
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            var app = AppsList.SelectedItem as AppEntry;
            if (app is not null)
            {
                Launch(app);
            }
            else if (SearchBox.Text.Trim().Length > 0)
            {
                _host.Search.OpenWeb(SearchBox.Text);
                HidePanel();
            }

            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            HidePanel();
            e.Handled = true;
        }
    }

    private void AppsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (AppsList.SelectedItem is AppEntry app)
        {
            Launch(app);
        }
    }

    private void AppsList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && AppsList.SelectedItem is AppEntry app)
        {
            Launch(app);
            e.Handled = true;
        }
    }


    private void RefreshPinnedApps()
    {
        PinnedApps.Children.Clear();
        foreach (var app in _host.Search.Apps.Take(6))
        {
            var button = new Button
            {
                Style = (Style)FindResource("FlatButton"),
                ToolTip = app.Name,
                Margin = new Thickness(2),
                Padding = new Thickness(2),
            };
            var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            if (app.Icon is not null)
            {
                panel.Children.Add(new Image
                {
                    Source = app.Icon,
                    Width = 30,
                    Height = 30,
                    Stretch = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Center,
                });
            }
            else
            {
                panel.Children.Add(new TextBlock
                {
                    Text = app.Initial,
                    FontSize = 18,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 5, 0, 5),
                });
            }

            panel.Children.Add(new TextBlock
            {
                Text = app.Name,
                FontSize = 9,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(180, 255, 255, 255)),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 78,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 3, 0, 0),
            });
            button.Content = panel;
            button.Click += (_, _) => Launch(app);
            PinnedApps.Children.Add(button);
        }
    }

    private void Launch(AppEntry app)
    {
        _host.Search.Launch(app);
        HidePanel();
    }

    private static void Run(string file, string args)
    {
        try
        {
            Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }
}
