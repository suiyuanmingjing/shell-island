using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
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
    private static readonly HttpClient CoverHttp = new(new HttpClientHandler { UseProxy = false })
    {
        Timeout = TimeSpan.FromSeconds(8),
    };
    private static readonly Dictionary<string, BitmapSource?> CoverCache = new();
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
            var title = props.Title ?? string.Empty;
            var artist = props.Artist ?? string.Empty;
            var artwork = await ReadArtworkAsync(props.Thumbnail);
            artwork ??= await FetchCoverAsync(title, artist);

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

    private static async Task<BitmapSource?> FetchCoverAsync(string title, string artist)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var cacheKey = $"{title}\u001f{artist}";
        lock (CoverCache)
        {
            if (CoverCache.TryGetValue(cacheKey, out var cached)) return cached;
        }

        try
        {
            var query = Uri.EscapeDataString($"{title} {artist}".Trim());
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://c.y.qq.com/soso/fcgi-bin/client_search_cp?w={query}&format=json&n=3&p=1");
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0");
            request.Headers.Referrer = new Uri("https://y.qq.com/");
            using var response = await CoverHttp.SendAsync(request);
            if (!response.IsSuccessStatusCode) return CacheCover(cacheKey, null);
            var text = await response.Content.ReadAsStringAsync();
            var start = text.IndexOf('{');
            var end = text.LastIndexOf('}');
            if (start < 0 || end <= start) return CacheCover(cacheKey, null);
            using var doc = JsonDocument.Parse(text[start..(end + 1)]);
            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("song", out var song) ||
                !song.TryGetProperty("list", out var list) ||
                list.GetArrayLength() == 0)
            {
                return CacheCover(cacheKey, null);
            }

            var target = Normalize(title);
            string albumMid = string.Empty;
            foreach (var item in list.EnumerateArray())
            {
                var songName = item.TryGetProperty("songname", out var sn) ? Normalize(sn.GetString() ?? string.Empty) : string.Empty;
                if (songName == target || songName.Contains(target) || target.Contains(songName))
                {
                    albumMid = item.TryGetProperty("albummid", out var am) ? am.GetString() ?? string.Empty : string.Empty;
                    break;
                }
            }
            if (string.IsNullOrWhiteSpace(albumMid)) return CacheCover(cacheKey, null);

            var bytes = await CoverHttp.GetByteArrayAsync($"https://y.gtimg.cn/music/photo_new/T002R300x300M000{albumMid}.jpg");
            if (bytes.Length < 100) return CacheCover(cacheKey, null);
            using var ms = new MemoryStream(bytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = ms;
            bitmap.EndInit();
            bitmap.Freeze();
            AppLog.Write($"cover api hit {title}");
            return CacheCover(cacheKey, bitmap);
        }
        catch (Exception ex)
        {
            AppLog.Write($"cover api failed: {ex.Message}");
            return CacheCover(cacheKey, null);
        }
    }

    private static BitmapSource? CacheCover(string key, BitmapSource? bitmap)
    {
        lock (CoverCache)
        {
            CoverCache[key] = bitmap;
        }
        return bitmap;
    }

    private static string Normalize(string text)
    {
        return System.Text.RegularExpressions.Regex.Replace(text.ToLowerInvariant(), @"[^\p{L}\p{N}]", string.Empty);
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
        catch (Exception ex)
        {
            AppLog.Write($"artwork failed: {ex.Message}");
            return null;
        }
    }
}
