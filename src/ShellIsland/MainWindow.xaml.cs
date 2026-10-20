using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ShellIsland.Services;

namespace ShellIsland;

public partial class MainWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;

    private readonly TaskbarService _taskbar = new();
    private readonly SearchService _search = new();
    private readonly LyricsService _lyrics = new();
    private readonly MediaService _media = new();
    private TaskbarIslandHost? _islandHost;
    private string _lastArtworkKey = string.Empty;
    private readonly DispatcherTimer _explorerTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _explorerPid;
    private int _explorerRestarts;
    private DateTime _lastExplorerChange = DateTime.MinValue;
    private bool _explorerExitRequested;
    private readonly System.Threading.EventWaitHandle _islandOpenEvent = new(false, System.Threading.EventResetMode.AutoReset, "ShellIslandOpen");
    private readonly System.Threading.Thread _islandOpenThread;
    private IslandPanelWindow? _islandPanel;
    private readonly DispatcherTimer _startRectTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    private readonly DispatcherTimer _mediaTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private IslandPopup? _island;
    private StartMenuWindow? _startMenu;
    private MediaSnapshot _lastMedia = MediaSnapshot.Empty;
    private bool _shownCapsule;
    private bool _keepCapsule;
    private readonly string _settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ShellIsland", "settings.json");

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => HideFromAltTab();

        _islandOpenThread = new System.Threading.Thread(() =>
        {
            while (true)
            {
                _islandOpenEvent.WaitOne();
                Dispatcher.BeginInvoke(ToggleIslandPanel);
            }
        }) { IsBackground = true };
        _islandOpenThread.Start();
        LoadSettings();
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    public TaskbarService Taskbar => _taskbar;

    public MediaService Media => _media;

    public SearchService Search => _search;

    private void HideFromAltTab()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var style = GetWindowLong(hwnd, GwlExStyle);
        SetWindowLong(hwnd, GwlExStyle, style | WsExToolWindow);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        StartButtonLocator.Refresh();
        _startRectTimer.Tick += (_, _) => StartButtonLocator.Refresh();
        _startRectTimer.Start();
        _islandHost = new TaskbarIslandHost();
        _islandHost.Clicked += () => ShowIsland(false);
        _islandHost.Attach();
        await _search.LoadAsync();

        _mediaTimer.Tick += async (_, _) => await PollMediaAsync();
        _mediaTimer.Start();
        _explorerTimer.Tick += (_, _) => CheckExplorer();
        _explorerTimer.Start();
        _ = TapBootstrapper.EnsureInjectedAsync();
        await PollMediaAsync();
    }

    private void WriteArtwork(MediaSnapshot snapshot)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShellIsland");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "artwork.png");
            if (snapshot.Artwork is null)
            {
                File.Delete(path);
                _lastArtworkKey = string.Empty;
                return;
            }

            var key = $"{snapshot.Title}\u001f{snapshot.Artist}\u001f{snapshot.Album}";
            if (key == _lastArtworkKey && File.Exists(path))
            {
                return;
            }

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(snapshot.Artwork));
            var temp = path + ".tmp";
            using (var stream = File.Create(temp))
            {
                encoder.Save(stream);
            }
            File.Move(temp, path, true);
            _lastArtworkKey = key;
        }
        catch
        {
            // artwork bridge is best effort
        }
    }
    private static void WriteNowPlaying(MediaSnapshot snapshot, string lyricLine)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShellIsland");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "nowplaying.txt");
            if (!snapshot.HasMedia)
            {
                File.Delete(path);
                return;
            }

            var lyric = (lyricLine ?? string.Empty).Replace("\r", " ").Replace("\n", " / ");
            File.WriteAllLines(path, new[]
            {
                snapshot.Title ?? string.Empty,
                snapshot.Artist ?? string.Empty,
                lyric,
                snapshot.Position.TotalSeconds.ToString("F0"),
            });
        }
        catch
        {
            // bridge is best effort
        }
    }
    private async Task PollMediaAsync()
    {
        var next = await _media.RefreshAsync();
        if (next.Artwork is not null)
        {
            IslandArt.Source = next.Artwork;
            IslandArt.Visibility = Visibility.Visible;
            IslandGlyph.Visibility = Visibility.Collapsed;
        }
        else
        {
            IslandArt.Source = null;
            IslandArt.Visibility = Visibility.Collapsed;
            IslandGlyph.Visibility = Visibility.Visible;
        }


        _lastMedia = next;
        var lyricLine = await _lyrics.GetCurrentLineAsync(next.Title, next.Artist, next.Position);
        WriteNowPlaying(next, lyricLine);
        WriteArtwork(next);
        _islandHost?.View?.UpdateMedia(next);
        _island?.UpdateMedia(next);
    }

    private void LoadSettings()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var json = File.ReadAllText(_settingsPath);
                using var doc = JsonDocument.Parse(json);
                _keepCapsule = doc.RootElement.TryGetProperty("keepCapsule", out var value) && value.GetBoolean();
            }
        }
        catch
        {
            _keepCapsule = false;
        }
    }

    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            File.WriteAllText(_settingsPath, JsonSerializer.Serialize(new { keepCapsule = _keepCapsule }));
        }
        catch
        {
            // ignore settings write errors
        }
    }

    private void BuildIslandMenuUnused()
    {
        var menu = new ContextMenu();
        var keep = new MenuItem { Header = "播放时保持胶囊", IsCheckable = true, IsChecked = _keepCapsule };
        keep.Click += (_, _) =>
        {
            _keepCapsule = keep.IsChecked;
            SaveSettings();
        };
        menu.Items.Add(keep);
        menu.Items.Add(new Separator());
        var exit = new MenuItem { Header = "退出 ShellIsland" };
        exit.Click += (_, _) => Application.Current.Shutdown();
        menu.Items.Add(exit);
        IslandButton.ContextMenu = menu;
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        _island?.HidePanel();
        _startMenu ??= new StartMenuWindow(this);
        _startMenu.ShowFor();
    }

    private void ToggleStartMenu()
    {
        if (_startMenu is { IsVisible: true })
        {
            _startMenu.HidePanel();
            return;
        }

        _island?.HidePanel();
        _startMenu ??= new StartMenuWindow(this);
        _startMenu.ShowFor();
    }

    private void IslandButton_Click(object sender, RoutedEventArgs e)
    {
        ShowIsland(autoCollapse: false);
    }

    private void IslandButton_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_lastMedia.HasMedia)
        {
            ShowIsland(autoCollapse: false);
        }
    }

    private void ShowIsland(bool autoCollapse)
    {
        _startMenu?.HidePanel();
        _island ??= new IslandPopup(this);
        _island.ShowFor(autoCollapse);
    }

    private void _startTimerStop() => _startRectTimer.Stop();

    private void ToggleIslandPanel()
    {
        if (_islandPanel is { IsVisible: true })
        {
            _islandPanel.Hide();
            _islandPanel.Close();
            _islandPanel = null;
            return;
        }

        _islandPanel = new IslandPanelWindow(this);
        _islandPanel.Closed += (_, _) => _islandPanel = null;
        _islandPanel.ShowAboveTaskbar();
    }
    private void CheckExplorer()
    {
        if (_explorerExitRequested) return;

        try
        {
            var processes = Process.GetProcessesByName("explorer");
            if (processes.Length == 0) return;
            var pid = processes[0].Id;
            if (_explorerPid == 0)
            {
                _explorerPid = pid;
                _lastExplorerChange = DateTime.UtcNow;
                return;
            }

            if (pid == _explorerPid) return;

            _explorerPid = pid;
            var now = DateTime.UtcNow;
            _explorerRestarts = now - _lastExplorerChange <= TimeSpan.FromSeconds(45)
                ? _explorerRestarts + 1
                : 1;
            _lastExplorerChange = now;

            if (_explorerRestarts < 3) return;

            _explorerExitRequested = true;
            _explorerTimer.Stop();
            MessageBox.Show(
                "检测到文件资源管理器连续重启 3 次，ShellIsland 将退出。",
                "ShellIsland",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            Application.Current.Shutdown();
        }
        catch
        {
            // ignore monitor failures
        }
    }
    private void OnClosed(object? sender, EventArgs e)
    {

        _mediaTimer.Stop();
        _startTimerStop();
        _islandHost?.Dispose();
        _islandOpenEvent.Dispose();
        _island?.Close();
        _startMenu?.Close();
        Application.Current.Shutdown();
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
