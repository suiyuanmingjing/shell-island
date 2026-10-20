using System.IO;
using System.Windows.Media.Imaging;
using Windows.Media.Control;

namespace ShellIsland.Services;

public sealed record MediaSnapshot(
    bool HasMedia,
    bool IsPlaying,
    string Title,
    string Artist,
    string Album,
    string SourceApp,
    TimeSpan Position,
    TimeSpan Duration,
    BitmapSource? Artwork)
{
    public static readonly MediaSnapshot Empty = new(
        false, false, string.Empty, string.Empty, string.Empty,
        string.Empty, TimeSpan.Zero, TimeSpan.Zero, null);
}

public sealed class MediaService
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private DateTime _lastInitAttempt = DateTime.MinValue;

    public MediaSnapshot Current { get; private set; } = MediaSnapshot.Empty;

    public async Task<MediaSnapshot> RefreshAsync()
    {
        try
        {
            if (_manager is null && DateTime.UtcNow - _lastInitAttempt > TimeSpan.FromSeconds(5))
            {
                _lastInitAttempt = DateTime.UtcNow;
                _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            }

            var session = _manager?.GetCurrentSession();
            if (_manager is not null)
            {
                foreach (var candidate in _manager.GetSessions())
                {
                    try
                    {
                        var candidateProps = await candidate.TryGetMediaPropertiesAsync();
                        if (!string.IsNullOrWhiteSpace(candidateProps.Title))
                        {
                            session = candidate;
                            break;
                        }
                    }
                    catch
                    {
                    }
                }
            }
            if (session is null)
            {
                Current = MediaSnapshot.Empty;
                return Current;
            }

            var props = await session.TryGetMediaPropertiesAsync();
            var playback = session.GetPlaybackInfo();
            var timeline = session.GetTimelineProperties();
            var artwork = await ReadArtworkAsync(props.Thumbnail);
            var title = props.Title ?? string.Empty;
            var artist = props.Artist ?? string.Empty;

            Current = new MediaSnapshot(
                !string.IsNullOrWhiteSpace(title) || !string.IsNullOrWhiteSpace(artist),
                playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                title,
                artist,
                props.AlbumTitle ?? string.Empty,
                session.SourceAppUserModelId ?? string.Empty,
                timeline.Position,
                timeline.EndTime - timeline.StartTime,
                artwork);
            return Current;
        }
        catch
        {
            Current = MediaSnapshot.Empty;
            return Current;
        }
    }

    public async Task TogglePlayPauseAsync()
    {
        var session = _manager?.GetCurrentSession();
        if (session is null)
        {
            return;
        }

        try
        {
            if (Current.IsPlaying)
            {
                await session.TryPauseAsync();
            }
            else
            {
                await session.TryPlayAsync();
            }
        }
        catch
        {
            // ignore media app refusing control
        }
    }

    public async Task NextAsync()
    {
        try
        {
            await _manager?.GetCurrentSession()?.TrySkipNextAsync()!;
        }
        catch
        {
            // ignore
        }
    }

    public async Task PreviousAsync()
    {
        try
        {
            await _manager?.GetCurrentSession()?.TrySkipPreviousAsync()!;
        }
        catch
        {
            // ignore
        }
    }

    private static async Task<BitmapSource?> ReadArtworkAsync(Windows.Storage.Streams.IRandomAccessStreamReference? reference)
    {
        if (reference is null)
        {
            return null;
        }

        try
        {
            using var stream = await reference.OpenReadAsync();
            using var net = stream.AsStreamForRead();
            using var ms = new MemoryStream();
            await net.CopyToAsync(ms);
            ms.Position = 0;
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = ms;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }
}
