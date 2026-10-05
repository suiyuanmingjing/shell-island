using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ShellIsland.Services;

public sealed record LyricLine(TimeSpan Time, string Text, string Translation = "");

public sealed class LyricsService
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(8),
    };

    private string _key = string.Empty;
    private IReadOnlyList<LyricLine> _lines = Array.Empty<LyricLine>();

    public async Task<LyricLine?> GetCurrentLyricAsync(string title, string artist, TimeSpan position)
    {
        var key = $"{title}\u001f{artist}";
        if (!string.Equals(key, _key, StringComparison.Ordinal))
        {
            _key = key;
            _lines = await FetchAsync(title, artist);
        }

        if (_lines.Count == 0)
        {
            return null;
        }

        var line = _lines[0];
        foreach (var item in _lines)
        {
            if (item.Time > position)
            {
                break;
            }
            line = item;
        }

        return line;
    }

    public async Task<string> GetCurrentLineAsync(string title, string artist, TimeSpan position)
    {
        var line = await GetCurrentLyricAsync(title, artist, position);
        if (line is null)
        {
            return string.Empty;
        }

        return string.IsNullOrWhiteSpace(line.Translation)
            ? line.Text
            : $"{line.Text}\n{line.Translation}";
    }

    private static async Task<IReadOnlyList<LyricLine>> FetchAsync(string title, string artist)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return Array.Empty<LyricLine>();
        }

        var qq = await TryQqAsync(title, artist);
        if (qq.Count > 0)
        {
            return qq;
        }

        var netease = await TryNeteaseAsync(title, artist);
        if (netease.Count > 0)
        {
            return netease;
        }

        return await TryLrclibAsync(title, artist);
    }

    // 1. QQ Music
    private static async Task<IReadOnlyList<LyricLine>> TryQqAsync(string title, string artist)
    {
        try
        {
            var query = Uri.EscapeDataString($"{title} {artist}".Trim());
            using var searchRequest = new HttpRequestMessage(HttpMethod.Get,
                $"https://c.y.qq.com/soso/fcgi-bin/client_search_cp?w={query}&format=json&n=5&p=1");
            searchRequest.Headers.UserAgent.ParseAdd("Mozilla/5.0");
            searchRequest.Headers.Referrer = new Uri("https://y.qq.com/");
            using var searchResponse = await Http.SendAsync(searchRequest);
            if (!searchResponse.IsSuccessStatusCode)
            {
                return Array.Empty<LyricLine>();
            }

            var searchText = await searchResponse.Content.ReadAsStringAsync();
            var searchJson = NormalizeJson(searchText);
            if (searchJson is null)
            {
                return Array.Empty<LyricLine>();
            }

            using var searchDoc = JsonDocument.Parse(searchJson);
            if (!searchDoc.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("song", out var song) ||
                !song.TryGetProperty("list", out var list) ||
                list.GetArrayLength() == 0)
            {
                return Array.Empty<LyricLine>();
            }

            var songmid = list[0].GetProperty("songmid").GetString();
            if (string.IsNullOrWhiteSpace(songmid))
            {
                return Array.Empty<LyricLine>();
            }

            using var lyricRequest = new HttpRequestMessage(HttpMethod.Get,
                $"https://c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg?songmid={Uri.EscapeDataString(songmid)}&format=json&nobase64=1");
            lyricRequest.Headers.UserAgent.ParseAdd("Mozilla/5.0");
            lyricRequest.Headers.Referrer = new Uri("https://y.qq.com/");
            using var lyricResponse = await Http.SendAsync(lyricRequest);
            if (!lyricResponse.IsSuccessStatusCode)
            {
                return Array.Empty<LyricLine>();
            }

            var lyricJson = NormalizeJson(await lyricResponse.Content.ReadAsStringAsync());
            if (lyricJson is null)
            {
                return Array.Empty<LyricLine>();
            }

            using var lyricDoc = JsonDocument.Parse(lyricJson);
            var original = lyricDoc.RootElement.TryGetProperty("lyric", out var lyricElement)
                ? WebUtility.HtmlDecode(lyricElement.GetString() ?? string.Empty)
                : string.Empty;
            var translated = lyricDoc.RootElement.TryGetProperty("trans", out var transElement)
                ? WebUtility.HtmlDecode(transElement.GetString() ?? string.Empty)
                : string.Empty;

            return Merge(ParseLrc(original), ParseLrc(translated));
        }
        catch
        {
            return Array.Empty<LyricLine>();
        }
    }

    // 2. NetEase
    private static async Task<IReadOnlyList<LyricLine>> TryNeteaseAsync(string title, string artist)
    {
        try
        {
            var query = Uri.EscapeDataString($"{title} {artist}".Trim());
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://music.163.com/api/search/get/web?csrf_token=&s={query}&type=1&offset=0&total=true&limit=5");
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0");
            request.Headers.Referrer = new Uri("https://music.163.com/");
            using var response = await Http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                return Array.Empty<LyricLine>();
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (!document.RootElement.TryGetProperty("result", out var result) ||
                !result.TryGetProperty("songs", out var songs) ||
                songs.GetArrayLength() == 0)
            {
                return Array.Empty<LyricLine>();
            }

            var id = songs[0].GetProperty("id").GetInt64();
            using var lyricRequest = new HttpRequestMessage(HttpMethod.Get,
                $"https://music.163.com/api/song/lyric?id={id}&lv=1&kv=1&tv=-1");
            lyricRequest.Headers.UserAgent.ParseAdd("Mozilla/5.0");
            lyricRequest.Headers.Referrer = new Uri("https://music.163.com/");
            using var lyricResponse = await Http.SendAsync(lyricRequest);
            if (!lyricResponse.IsSuccessStatusCode)
            {
                return Array.Empty<LyricLine>();
            }

            using var lyricDocument = JsonDocument.Parse(await lyricResponse.Content.ReadAsStringAsync());
            var original = lyricDocument.RootElement.TryGetProperty("lrc", out var lrc) &&
                           lrc.TryGetProperty("lyric", out var lyricText)
                ? lyricText.GetString() ?? string.Empty
                : string.Empty;
            var translated = lyricDocument.RootElement.TryGetProperty("tlyric", out var tlyric) &&
                             tlyric.TryGetProperty("lyric", out var tlyricText)
                ? tlyricText.GetString() ?? string.Empty
                : string.Empty;

            return Merge(ParseLrc(original), ParseLrc(translated));
        }
        catch
        {
            return Array.Empty<LyricLine>();
        }
    }

    // 3. LRCLIB
    private static async Task<IReadOnlyList<LyricLine>> TryLrclibAsync(string title, string artist)
    {
        try
        {
            var url = "https://lrclib.net/api/get" +
                      $"?track_name={Uri.EscapeDataString(title)}" +
                      $"&artist_name={Uri.EscapeDataString(artist)}";
            using var response = await Http.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                return Array.Empty<LyricLine>();
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (document.RootElement.TryGetProperty("syncedLyrics", out var synced) &&
                synced.ValueKind == JsonValueKind.String)
            {
                return ParseLrc(synced.GetString() ?? string.Empty);
            }
        }
        catch
        {
            // no lyrics
        }

        return Array.Empty<LyricLine>();
    }

    private static string? NormalizeJson(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : null;
    }

    private static IReadOnlyList<LyricLine> Merge(
        IReadOnlyList<LyricLine> original,
        IReadOnlyList<LyricLine> translated)
    {
        if (translated.Count == 0)
        {
            return original;
        }

        var result = new List<LyricLine>(original.Count);
        foreach (var line in original)
        {
            var match = translated.FirstOrDefault(item => Math.Abs((item.Time - line.Time).TotalMilliseconds) < 800);
            result.Add(match is null ? line : line with { Translation = match.Text });
        }
        return result;
    }

    private static IReadOnlyList<LyricLine> ParseLrc(string lrc)
    {
        var lines = new List<LyricLine>();
        foreach (var raw in lrc.Split('\n'))
        {
            var text = raw.TrimEnd('\r');
            var matches = Regex.Matches(text, "\\[(\\d{1,2}):(\\d{2})(?:[.:](\\d{1,3}))?\\]");
            if (matches.Count == 0)
            {
                continue;
            }

            var content = Regex.Replace(text, "\\[\\d{1,2}:\\d{2}(?:[.:]\\d{1,3})?\\]", string.Empty).Trim();
            if (content.Length == 0)
            {
                continue;
            }

            foreach (Match match in matches)
            {
                var minutes = int.Parse(match.Groups[1].Value);
                var seconds = int.Parse(match.Groups[2].Value);
                var fraction = match.Groups[3].Success
                    ? int.Parse(match.Groups[3].Value.PadRight(3, '0')[..3])
                    : 0;
                lines.Add(new LyricLine(new TimeSpan(0, 0, minutes, seconds, fraction), content));
            }
        }

        return lines.OrderBy(line => line.Time).ToArray();
    }
}
