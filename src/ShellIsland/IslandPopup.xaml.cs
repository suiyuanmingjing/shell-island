using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using ShellIsland.Models;
using ShellIsland.Services;

namespace ShellIsland;

public partial class IslandPopup : Window
{
    private readonly MainWindow _host;
    private readonly DispatcherTimer _autoTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool _autoCollapse;

    public IslandPopup(MainWindow host)
    {
        _host = host;
        InitializeComponent();
        Deactivated += (_, _) => HidePanel();
        MouseEnter += (_, _) => _autoTimer.Stop();
        MouseLeave += (_, _) =>
        {
            if (_autoCollapse)
            {
                _autoTimer.Start();
            }
        };
        _autoTimer.Tick += (_, _) =>
        {
            _autoTimer.Stop();
            HidePanel();
        };
    }

    public void ShowFor(bool autoCollapse)
    {
        _autoCollapse = autoCollapse;
        var taskbar = _host.Taskbar.GetTaskbar(this);
        var work = SystemParameters.WorkArea;
        StartButtonLocator.Refresh();
        var rect = StartButtonLocator.Rect;
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        var anchor = rect.Right > rect.Left ? rect.Right / dpi.DpiScaleX : work.Left + 16;
        Left = Math.Clamp(anchor - 24, work.Left + 8, work.Right - Width - 8);
        Top = Math.Max(work.Top + 8, taskbar.Top - Height - 8);
        Show();
        Activate();
        SearchBox.Focus();
        SearchBox.SelectAll();
        UpdateMedia(_host.Media.Current);
        UpdateContent();
        if (autoCollapse)
        {
            _autoTimer.Stop();
            _autoTimer.Start();
        }
    }

    public void HidePanel()
    {
        _autoTimer.Stop();
        Hide();
    }

    public void UpdateMedia(MediaSnapshot snapshot)
    {
        if (snapshot.HasMedia)
        {
            Caption.Text = string.IsNullOrWhiteSpace(snapshot.SourceApp)
                ? "正在播放"
                : $"正在播放 · {ShortApp(snapshot.SourceApp)}";
            TrackTitle.Text = snapshot.Title;
            TrackArtist.Text = snapshot.Artist;
            Artwork.Source = snapshot.Artwork;
            PlayGlyph.Text = snapshot.IsPlaying ? "\uE769" : "\uE768";
            var duration = snapshot.Duration.TotalSeconds;
            var position = snapshot.Position.TotalSeconds;
            TrackProgress.Value = duration > 0 ? Math.Clamp(position / duration * 100, 0, 100) : 0;
            PositionText.Text = Format(snapshot.Position);
            DurationText.Text = Format(snapshot.Duration);
        }
        else
        {
            Caption.Text = "搜索";
            Artwork.Source = null;
            TrackTitle.Text = string.Empty;
            TrackArtist.Text = string.Empty;
            TrackProgress.Value = 0;
        }

        UpdateContent();
    }

    private void UpdateContent()
    {
        var query = SearchBox.Text.Trim();
        var hasMedia = _host.Media.Current.HasMedia;
        SearchPlaceholder.Visibility = query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        WebButton.Visibility = query.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (query.Length == 0)
        {
            MusicPanel.Visibility = hasMedia ? Visibility.Visible : Visibility.Collapsed;
            SearchPanel.Visibility = hasMedia ? Visibility.Collapsed : Visibility.Visible;
            ResultsList.Visibility = Visibility.Collapsed;
            EmptyHint.Visibility = hasMedia ? Visibility.Collapsed : Visibility.Visible;
            EmptyHint.Text = "输入后搜索本机应用，Enter 打开第一条，或用网页搜索。";
            return;
        }

        MusicPanel.Visibility = Visibility.Collapsed;
        SearchPanel.Visibility = Visibility.Visible;
        ResultsList.Visibility = Visibility.Visible;
        EmptyHint.Visibility = Visibility.Collapsed;
        var results = _host.Search.Search(query);
        ResultsList.ItemsSource = results;
        if (results.Count > 0)
        {
            ResultsList.SelectedIndex = 0;
        }
    }

    private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => UpdateContent();

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            HidePanel();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            if (SearchBox.Text.Trim().Length > 0)
            {
                var app = ResultsList.SelectedItem as AppEntry ?? (ResultsList.Items.Count > 0 ? ResultsList.Items[0] as AppEntry : null);
                if (app is not null)
                {
                    _host.Search.Launch(app);
                    HidePanel();
                }
                else
                {
                    _host.Search.OpenWeb(SearchBox.Text);
                    HidePanel();
                }
            }
            e.Handled = true;
        }
    }

    private void ResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => LaunchSelected();

    private void ResultsList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            LaunchSelected();
            e.Handled = true;
        }
    }

    private void LaunchSelected()
    {
        if (ResultsList.SelectedItem is AppEntry app)
        {
            _host.Search.Launch(app);
            HidePanel();
        }
    }

    private void WebButton_Click(object sender, RoutedEventArgs e)
    {
        if (SearchBox.Text.Trim().Length > 0)
        {
            _host.Search.OpenWeb(SearchBox.Text);
            HidePanel();
        }
    }

    private async void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        await _host.Media.TogglePlayPauseAsync();
        await Task.Delay(250);
        UpdateMedia(await _host.Media.RefreshAsync());
    }

    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        await _host.Media.NextAsync();
        await Task.Delay(250);
        UpdateMedia(await _host.Media.RefreshAsync());
    }

    private async void Previous_Click(object sender, RoutedEventArgs e)
    {
        await _host.Media.PreviousAsync();
        await Task.Delay(250);
        UpdateMedia(await _host.Media.RefreshAsync());
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => HidePanel();

    private static string ShortApp(string app)
    {
        var name = app.Split('!')[0];
        var dot = name.LastIndexOf('.');
        return dot >= 0 && dot < name.Length - 1 ? name[(dot + 1)..] : name;
    }

    private static string Format(TimeSpan value) =>
        value.TotalHours >= 1 ? value.ToString(@"h\:mm\:ss") : value.ToString(@"mm\:ss");
}
