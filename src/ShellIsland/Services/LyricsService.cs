using System.Net;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ShellIsland.Services;

public sealed record LyricLine(TimeSpan Time, string Text, string Translation = "");

public sealed class LyricsService
{
    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        UseProxy = false,
    })
    {
        Timeout = TimeSpan.FromSeconds(8),
    };

    private static readonly ConcurrentDictionary<string, string> MachineCache = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string _key = string.Empty;
    private IReadOnlyList<LyricLine> _lines = Array.Empty<LyricLine>();

    public async Task<LyricLine?> GetCurrentLyricAsync(string title, string artist, TimeSpan position)
    {
        await _gate.WaitAsync();
        try
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
            var lineIndex = 0;
            position += TimeSpan.FromSeconds(1);
            for (var i = 0; i < _lines.Count; i++)
            {
                if (_lines[i].Time > position) break;
                line = _lines[i];
                lineIndex = i;
            }

            return line;
        }
        finally
        {
            _gate.Release();
        }
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

        var local = TryLocal(title, artist);
        if (local.Count > 0)
        {
            AppLog.Write($"lyrics local hit {local.Count}");
            return local;
        }

        foreach (var (queryTitle, queryArtist) in BuildVariants(title, artist))
        {
            AppLog.Write($"lyrics try qq title={queryTitle} artist={queryArtist}");
            var qq = await TryQqAsync(queryTitle, queryArtist);
            if (qq.Count > 0)
            {
                AppLog.Write($"lyrics qq hit {qq.Count} trans={qq.Count(l => !string.IsNullOrWhiteSpace(l.Translation))}");
                return qq;
            }

            AppLog.Write($"lyrics try netease title={queryTitle} artist={queryArtist}");
            var netease = await TryNeteaseAsync(queryTitle, queryArtist);
            if (netease.Count > 0)
            {
                AppLog.Write($"lyrics netease hit {netease.Count}");
                return netease;
            }

            AppLog.Write($"lyrics try kugou title={queryTitle} artist={queryArtist}");
            var kugou = await TryKugouAsync(queryTitle, queryArtist);
            if (kugou.Count > 0)
            {
                AppLog.Write($"lyrics kugou hit {kugou.Count}");
                return kugou;
            }

            var lrclib = await TryLrclibAsync(queryTitle, queryArtist);
            if (lrclib.Count > 0)
            {
                AppLog.Write($"lyrics lrclib hit {lrclib.Count}");
                return lrclib;
            }
        }

        AppLog.Write("lyrics no match");
        return Array.Empty<LyricLine>();
    }

    private static IEnumerable<(string Title, string Artist)> BuildVariants(string title, string artist)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cleanTitle = Regex.Replace(title, @"\s*[\(（\[].*?[\)）\]]", string.Empty).Trim();
        cleanTitle = Regex.Replace(cleanTitle, @"\s+-\s*.*$", string.Empty).Trim();
        var cleanArtist = Regex.Replace(artist, @"\s*[\(（\[].*?[\)）\]]", string.Empty).Trim();
        cleanArtist = cleanArtist.Split('/', '、', '&').FirstOrDefault()?.Trim() ?? string.Empty;

        foreach (var pair in new[]
        {
            (title, artist),
            (cleanTitle, cleanArtist),
            (cleanTitle, string.Empty),
            (title, string.Empty),
        })
        {
            if (string.IsNullOrWhiteSpace(pair.Item1)) continue;
            var key = $"{pair.Item1}\u001f{pair.Item2}";
            if (seen.Add(key)) yield return pair;
        }
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

            var targetTitle = Normalize(title);
            var targetArtist = Normalize(artist);
            JsonElement bestSong = list[0];
            var bestScore = -1;
            foreach (var candidate in list.EnumerateArray())
            {
                var songName = candidate.TryGetProperty("songname", out var sn) ? Normalize(sn.GetString() ?? string.Empty) : string.Empty;
                var singerName = candidate.TryGetProperty("singer", out var singers) && singers.ValueKind == JsonValueKind.Array
                    ? Normalize(string.Join(" ", singers.EnumerateArray().Select(s => s.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty)))
                    : string.Empty;
                var score = 0;
                if (songName == targetTitle) score += 100;
                else if (songName.Contains(targetTitle) || targetTitle.Contains(songName)) score += 50;
                if (!string.IsNullOrEmpty(targetArtist) && singerName.Contains(targetArtist)) score += 30;
                if (score > bestScore)
                {
                    bestScore = score;
                    bestSong = candidate;
                }
            }

            var songmid = bestSong.GetProperty("songmid").GetString();
            if (string.IsNullOrWhiteSpace(songmid))
            {
                return Array.Empty<LyricLine>();
            }

            var payload = "{\"req_0\":{\"module\":\"music.musichallSong.PlayLyricInfo\",\"method\":\"GetPlayLyricInfo\",\"param\":{\"songMID\":\"" + songmid + "\"}}}";
            using var lyricRequest = new HttpRequestMessage(HttpMethod.Get,
                $"https://u.y.qq.com/cgi-bin/musicu.fcg?data={Uri.EscapeDataString(payload)}");
            lyricRequest.Headers.UserAgent.ParseAdd("Mozilla/5.0");
            lyricRequest.Headers.Referrer = new Uri("https://y.qq.com/");
            using var lyricResponse = await Http.SendAsync(lyricRequest);
            if (!lyricResponse.IsSuccessStatusCode)
            {
                return Array.Empty<LyricLine>();
            }

            using var lyricDoc = JsonDocument.Parse(await lyricResponse.Content.ReadAsStringAsync());
            if (!lyricDoc.RootElement.TryGetProperty("req_0", out var req0) ||
                !req0.TryGetProperty("data", out var lyricData))
            {
                return Array.Empty<LyricLine>();
            }

            var original = DecodeQqLyric(lyricData, "lyric");
            // QQ 的 trans 是日文注音/歌词标注，不是中文翻译；翻译交给网易云 tlyric。
            return ParseLrc(original);
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

    private static async Task<IReadOnlyList<LyricLine>> AddTranslationAsync(
        IReadOnlyList<LyricLine> lines, string title, string artist)
    {
        if (lines.Any(line => IsChineseTranslation(line.Translation)))
        {
            return lines;
        }

        foreach (var (queryTitle, queryArtist) in BuildVariants(title, artist))
        {
            try
            {
                var translated = await TryNeteaseAsync(queryTitle, queryArtist);
                if (translated.Any(line => IsChineseTranslation(line.Translation)))
                {
                    return Merge(lines, translated);
                }
            }
            catch
            {
            }
        }

        return lines;
    }
    private static IReadOnlyList<LyricLine> TryLocal(string title, string artist)
    {
        var target = Normalize(title);
        if (target.Length < 2) return Array.Empty<LyricLine>();
        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Music"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShellIsland", "Lyrics"),
        }.Where(Directory.Exists).Distinct();

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
        };

        foreach (var root in roots)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(root, "*.lrc", options))
                {
                    var stem = Normalize(Path.GetFileNameWithoutExtension(file));
                    if (!stem.Contains(target) && !target.Contains(stem)) continue;
                    var lines = ParseLrc(File.ReadAllText(file));
                    if (lines.Count > 0)
                    {
                        AppLog.Write($"lyrics local file {file}");
                        return lines;
                    }
                }
            }
            catch
            {
            }
        }

        return Array.Empty<LyricLine>();
    }

    private static async Task<IReadOnlyList<LyricLine>> TryKugouAsync(string title, string artist)
    {
        try
        {
            var query = Uri.EscapeDataString($"{title} {artist}".Trim());
            using var searchRequest = new HttpRequestMessage(HttpMethod.Get,
                $"https://krcs.kugou.com/search?ver=1&man=yes&client=mobi&keyword={query}");
            searchRequest.Headers.UserAgent.ParseAdd("Mozilla/5.0");
            using var searchResponse = await Http.SendAsync(searchRequest);
            if (!searchResponse.IsSuccessStatusCode) return Array.Empty<LyricLine>();
            using var searchDoc = JsonDocument.Parse(await searchResponse.Content.ReadAsStringAsync());
            if (!searchDoc.RootElement.TryGetProperty("candidates", out var candidates) ||
                candidates.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<LyricLine>();
            }

            var target = Normalize(title);
            JsonElement? best = null;
            var bestScore = -1;
            foreach (var candidate in candidates.EnumerateArray())
            {
                var song = candidate.TryGetProperty("song", out var s) ? Normalize(s.GetString() ?? string.Empty) : string.Empty;
                var score = song == target ? 100 : song.Contains(target) || target.Contains(song) ? 50 : 0;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = candidate;
                }
            }

            if (best is null) return Array.Empty<LyricLine>();
            var id = best.Value.GetProperty("id").GetString();
            var accessKey = best.Value.GetProperty("accesskey").GetString();
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(accessKey)) return Array.Empty<LyricLine>();

            using var lyricRequest = new HttpRequestMessage(HttpMethod.Get,
                $"https://lyrics.kugou.com/download?ver=1&client=pc&id={Uri.EscapeDataString(id)}&accesskey={Uri.EscapeDataString(accessKey)}&fmt=lrc&charset=utf8");
            lyricRequest.Headers.UserAgent.ParseAdd("Mozilla/5.0");
            using var lyricResponse = await Http.SendAsync(lyricRequest);
            if (!lyricResponse.IsSuccessStatusCode) return Array.Empty<LyricLine>();
            using var lyricDoc = JsonDocument.Parse(await lyricResponse.Content.ReadAsStringAsync());
            if (!lyricDoc.RootElement.TryGetProperty("content", out var content)) return Array.Empty<LyricLine>();
            var encoded = content.GetString() ?? string.Empty;
            string lrc;
            try
            {
                lrc = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            }
            catch
            {
                lrc = encoded;
            }
            return ParseLrc(lrc);
        }
        catch
        {
            return Array.Empty<LyricLine>();
        }
    }

    private static bool IsChineseText(string text)
    {
        return !string.IsNullOrWhiteSpace(text) && Regex.IsMatch(text, @"[\u4e00-\u9fff]") && !Regex.IsMatch(text, @"[\u3040-\u30ff]");
    }

    private static async Task<string> TranslateTextAsync(string text)
    {
        if (MachineCache.TryGetValue(text, out var cached)) return cached;
        try
        {
            var url = "https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&tl=zh-CN&dt=t&q=" + Uri.EscapeDataString(text);
            using var response = await Http.GetAsync(url);
            if (response.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                if (doc.RootElement.ValueKind == JsonValueKind.Array &&
                    doc.RootElement.GetArrayLength() > 0 &&
                    doc.RootElement[0].ValueKind == JsonValueKind.Array)
                {
                    var translated = string.Join(string.Empty, doc.RootElement[0].EnumerateArray()
                        .Where(x => x.ValueKind == JsonValueKind.Array && x.GetArrayLength() > 0)
                        .Select(x => x[0].GetString() ?? string.Empty));
                    if (IsChineseText(translated))
                    {
                        MachineCache[text] = translated;
                        AppLog.Write($"lyrics machine hit {translated}");
                        return translated;
                    }
                }
            }
        }
        catch
        {
        }

        try
        {
            var url = "https://api.mymemory.translated.net/get?q=" + Uri.EscapeDataString(text) + "&langpair=ja|zh-CN";
            using var response = await Http.GetAsync(url);
            if (response.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                if (doc.RootElement.TryGetProperty("responseData", out var data) &&
                    data.TryGetProperty("translatedText", out var translatedElement))
                {
                    var translated = WebUtility.HtmlDecode(translatedElement.GetString() ?? string.Empty);
                    if (IsChineseText(translated))
                    {
                        MachineCache[text] = translated;
                        AppLog.Write($"lyrics machine hit {translated}");
                        return translated;
                    }
                }
            }
        }
        catch
        {
        }

        return string.Empty;
    }
    private static bool IsMetadataTranslation(string text)
    {
        return Regex.IsMatch(text, @"^(作词|作曲|编曲|制作人|词|曲|演唱|混音|母带|出品|监制|OP|SP)\\s*[:：]");
    }

    private static bool IsChineseTranslation(string text)
    {
        return !string.IsNullOrWhiteSpace(text)
            && !IsMetadataTranslation(text)
            && !Regex.IsMatch(text, @"[\u3040-\u30ff]")
            && Regex.IsMatch(text, @"[\u4e00-\u9fff]");
    }
    private static string DecodeQqLyric(JsonElement data, string property)
    {
        if (!data.TryGetProperty(property, out var element)) return string.Empty;
        var value = element.GetString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        try
        {
            var bytes = Convert.FromBase64String(value);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return WebUtility.HtmlDecode(value);
        }
    }
    private static string Normalize(string text)
    {
        return Regex.Replace(text.ToLowerInvariant(), @"[^\p{L}\p{N}]", string.Empty);
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
