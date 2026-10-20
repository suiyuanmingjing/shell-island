using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ShellIsland.Services;

namespace ShellIsland.Controls;

public partial class TaskbarIslandView : UserControl
{
    public event Action? Clicked;

    public TaskbarIslandView()
    {
        InitializeComponent();
        ToolTip = "搜索 / 正在播放";
    }

    public void UpdateMedia(MediaSnapshot snapshot)
    {
        if (snapshot.HasMedia)
        {
            SearchGlyph.Visibility = Visibility.Collapsed;
            Artwork.Source = snapshot.Artwork;
            Artwork.Visibility = snapshot.Artwork is null ? Visibility.Collapsed : Visibility.Visible;
            ToolTip = $"{snapshot.Title} — {snapshot.Artist}";
        }
        else
        {
            SearchGlyph.Visibility = Visibility.Visible;
            Artwork.Source = null;
            Artwork.Visibility = Visibility.Collapsed;
            ToolTip = "搜索应用或网页";
        }
    }

    private void OnClick(object sender, MouseButtonEventArgs e)
    {
        Clicked?.Invoke();
        e.Handled = true;
    }

    private void OnMouseEnter(object sender, MouseEventArgs e) =>
        Root.Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x2A, 0x2A, 0x2A));

    private void OnMouseLeave(object sender, MouseEventArgs e) =>
        Root.Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x1C, 0x1C, 0x1C));
}
