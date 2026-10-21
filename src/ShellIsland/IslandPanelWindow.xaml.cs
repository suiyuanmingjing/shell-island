using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using ShellIsland.Services;

namespace ShellIsland;

public partial class IslandPanelWindow : Window
{
    private readonly MainWindow _host;
    private readonly LyricsService _lyrics;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private string _lyricKey = string.Empty;
    private bool _leftPanel;
    private bool _closing;
    private LyricLine? _currentLine;
    private string _sourceKey = string.Empty;
    private string _sourceName = string.Empty;
    private ImageSource? _sourceIcon;
    private readonly DispatcherTimer _autoHide = new() { Interval = TimeSpan.FromSeconds(3) };

    public IslandPanelWindow(MainWindow host)
    {
        _host = host;
        _lyrics = host.Lyrics;
        InitializeComponent();
        ApplyTheme();
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();
        _autoHide.Tick += (_, _) => { AppLog.Write("panel auto-hide 3s"); CloseSafely(); };
        PreviewMouseMove += (_, _) => ResetAutoHide();
        PreviewMouseDown += (_, _) => { _leftPanel = false; ResetAutoHide(); };
        MouseEnter += (_, _) => { _leftPanel = false; ResetAutoHide(); };
        MouseLeave += (_, _) => { _leftPanel = true; AppLog.Write("panel mouse leave"); };
        PreviewKeyDown += (_, _) => { if (_leftPanel) { AppLog.Write("panel close on key after leave"); CloseSafely(); } };
        Deactivated += (_, _) => { if (_leftPanel) { AppLog.Write("panel close on deactivate after leave"); CloseSafely(); } };
        Closed += (_, _) => { _closing = true; _timer.Stop(); _autoHide.Stop(); };
        ResetAutoHide();
    }

    public void ShowAboveTaskbar()
    {
        var taskbar = _host.Taskbar.GetTaskbar(this);
        var work = SystemParameters.WorkArea;
        SearchButtonLocator.Refresh();
        var search = SearchButtonLocator.Rect;
        var dpi = VisualTreeHelper.GetDpi(this);
        var anchor = search.Right > search.Left ? (search.Left + search.Right) / 2.0 / dpi.DpiScaleX : work.Left + 160;
        Left = Math.Clamp(anchor - Width / 2, work.Left + 12, work.Right - Width - 12);
        Top = Math.Max(work.Top + 12, taskbar.Top - Height - 10);
        AppLog.Write($"panel show left={Left} top={Top}");
        Show();
        Activate();
        ResetAutoHide();

    }

    private void CloseSafely()
    {
        if (_closing) return;
        _closing = true;
        _autoHide.Stop();
        _timer.Stop();
        Close();
    }
    private void ResetAutoHide()
    {
        _autoHide.Stop();
        _autoHide.Start();
    }
    private async Task RefreshAsync()
    {
        ApplyTheme();
        var snapshot = await _host.Media.RefreshAsync();
        TitleText.Text = string.IsNullOrWhiteSpace(snapshot.Title) ? "未在播放" : snapshot.Title;
        ArtistText.Text = snapshot.Artist;
        PlayGlyph.Text = snapshot.IsPlaying ? "\uE769" : "\uE768";

        var duration = snapshot.Duration.TotalMilliseconds;
        Progress.Maximum = Math.Max(1, duration);
        Progress.Value = Math.Clamp(snapshot.Position.TotalMilliseconds, 0, Progress.Maximum);
        PositionText.Text = Format(snapshot.Position);
        DurationText.Text = Format(snapshot.Duration);

        var key = $"{snapshot.Title}\u001f{snapshot.Artist}";
        if (key != _lyricKey)
        {
            _lyricKey = key;
            LyricText.Text = string.IsNullOrWhiteSpace(snapshot.Title) ? string.Empty : "正在加载歌词…";
        }

        ArtworkBrush.ImageSource = snapshot.Artwork;
        ArtworkFallback.Visibility = snapshot.Artwork is null ? Visibility.Visible : Visibility.Collapsed;

        if (snapshot.SourceApp != _sourceKey)
        {
            _sourceKey = snapshot.SourceApp;
            (_sourceName, _sourceIcon) = ResolveSource(snapshot.SourceApp);
        }
        SourceName.Text = _sourceName;
        SourceIcon.Source = _sourceIcon;
        SourceIcon.Visibility = _sourceIcon is null ? Visibility.Collapsed : Visibility.Visible;
        SourceFallback.Visibility = _sourceIcon is null ? Visibility.Visible : Visibility.Collapsed;

        _currentLine = await _lyrics.GetCurrentLyricAsync(snapshot.Title, snapshot.Artist, snapshot.Position);
        LyricText.Text = _currentLine?.Text ?? (string.IsNullOrWhiteSpace(TitleText.Text) ? string.Empty : TitleText.Text);

        if (!snapshot.HasMedia)
        {
            CloseSafely();
        }
    }

    private static string Format(TimeSpan value) =>
        value.TotalHours >= 1 ? value.ToString(@"h\:mm\:ss") : value.ToString(@"m\:ss");

    private async void OnPlayClick(object sender, RoutedEventArgs e)
    {
        await _host.Media.TogglePlayPauseAsync();
        await RefreshAsync();
    }

    private async void OnPreviousClick(object sender, RoutedEventArgs e)
    {
        await _host.Media.PreviousAsync();
        await RefreshAsync();
    }

    private async void OnNextClick(object sender, RoutedEventArgs e)
    {
        await _host.Media.NextAsync();
        await RefreshAsync();
    }

    private void OnSearchClick(object sender, MouseButtonEventArgs e)
    {
        AppLog.Write("panel search button");
        CloseSafely();
        NativeSearchLauncher.Launch(string.Empty);
    }

    private static (string Name, ImageSource? Icon) ResolveSource(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return (string.Empty, null);
        }

        var name = source;
        string? path = null;
        if (source.Contains(".exe", StringComparison.OrdinalIgnoreCase))
        {
            var exe = Path.GetFileName(source.Contains('\\') ? source : source);
            name = Path.GetFileNameWithoutExtension(exe);
            if (File.Exists(source))
            {
                path = source;
            }
            else
            {
                foreach (var process in Process.GetProcessesByName(name))
                {
                    try
                    {
                        path = process.MainModule?.FileName;
                        if (!string.IsNullOrWhiteSpace(path)) break;
                    }
                    catch
                    {
                    }
                }
            }
        }
        else if (File.Exists(source))
        {
            path = source;
            name = Path.GetFileNameWithoutExtension(source);
        }
        else if (source.Contains('!'))
        {
            name = source[(source.LastIndexOf('!') + 1)..];
        }
        else if (source.Contains('\\'))
        {
            name = source[(source.LastIndexOf('\\') + 1)..];
        }

        var icon = path is null ? null : IconLoader.Load(path);
        return (name, icon);
    }
    private void ApplyTheme()
    {
        var light = IsLightTheme();
        Root.Background = new SolidColorBrush(light ? Color.FromRgb(0xF2, 0xF2, 0xF2) : Color.FromRgb(0x1F, 0x1F, 0x1F));
        Root.BorderBrush = new SolidColorBrush(light ? Color.FromArgb(0x22, 0, 0, 0) : Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
        var primary = new SolidColorBrush(light ? Color.FromRgb(0x1A, 0x1A, 0x1A) : Colors.White);
        var secondary = new SolidColorBrush(light ? Color.FromRgb(0x60, 0x60, 0x60) : Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF));
        foreach (var text in new TextBlock[] { TitleText, LyricText, PlayGlyph, PositionText, DurationText, SearchGlyph, PrevGlyph, NextGlyph, SourceName, SourceFallback, ArtworkFallback })
        {
            text.Foreground = text == LyricText || text == TitleText ? primary : secondary;
        }
        foreach (var text in new[] { ArtistText })
        {
            text.Foreground = secondary;
        }

        SearchGlyph.Foreground = primary;
        PrevGlyph.Foreground = primary;
        NextGlyph.Foreground = primary;
        foreach (var button in new[] { PrevButton, PlayButton, NextButton })
        {
            button.Foreground = primary;
            button.Background = Brushes.Transparent;
            button.BorderThickness = new Thickness(0);
        }
        SearchButton.Background = new SolidColorBrush(light ? Color.FromArgb(0x0F, 0, 0, 0) : Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
        SearchButton.BorderBrush = Root.BorderBrush;
        Progress.Foreground = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4));
        Progress.Background = new SolidColorBrush(light ? Color.FromArgb(0x22, 0, 0, 0) : Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
    }

    private static bool IsLightTheme()
    {
        try
        {
            var value = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", 0);
            return Convert.ToInt32(value) != 0;
        }
        catch
        {
            return false;
        }
    }
}

internal static class NativeSearchLauncher
{
    private const int VkLWin = 0x5B;
    private const int VkS = 0x53;
    private const int VkReturn = 0x0D;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint InputKeyboard = 1;
    private const uint KeyEventUnicode = 0x0004;

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    public static void Launch(string query)
    {
        keybd_event(VkLWin, 0, 0, UIntPtr.Zero);
        keybd_event(VkS, 0, 0, UIntPtr.Zero);
        keybd_event(VkS, 0, KeyEventKeyUp, UIntPtr.Zero);
        keybd_event(VkLWin, 0, KeyEventKeyUp, UIntPtr.Zero);
        Thread.Sleep(350);

        foreach (var ch in query)
        {
            SendUnicode(ch);
        }

        if (query.Length > 0)
        {
            keybd_event(VkReturn, 0, 0, UIntPtr.Zero);
            keybd_event(VkReturn, 0, KeyEventKeyUp, UIntPtr.Zero);
        }
    }

    private static void SendUnicode(char ch)
    {
        var input = new INPUT
        {
            type = InputKeyboard,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = 0,
                    wScan = ch,
                    dwFlags = KeyEventUnicode,
                    time = 0,
                    dwExtraInfo = UIntPtr.Zero,
                },
            },
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
        Thread.Sleep(15);
    }
}
