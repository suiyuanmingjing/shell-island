using System.Diagnostics;
using System.IO;
using ShellIsland.Models;

namespace ShellIsland.Services;

public sealed class SearchService
{
    private readonly List<AppEntry> _apps = new();

    public IReadOnlyList<AppEntry> Apps => _apps;

    public Task LoadAsync()
    {
        return Task.Run(() =>
        {
            var found = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in GetStartMenuRoots())
            {
                if (!Directory.Exists(root))
                {
                    continue;
                }

                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = 0,
                    MatchCasing = MatchCasing.CaseInsensitive,
                };

                try
                {
                    foreach (var file in Directory.EnumerateFiles(root, "*.lnk", options))
                    {
                        var name = Path.GetFileNameWithoutExtension(file);
                        if (string.IsNullOrWhiteSpace(name) ||
                            name.Contains("uninstall", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("卸载", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        found.TryAdd(name, new AppEntry(name, file) { Icon = IconLoader.Load(file) });
                    }
                }
                catch
                {
                    // ignore unreadable start menu folders
                }
            }

            _apps.Clear();
            _apps.AddRange(found.Values.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase));
        });
    }

    public IReadOnlyList<AppEntry> Search(string query, int max = 8)
    {
        var text = query.Trim();
        if (text.Length == 0)
        {
            return _apps.Take(max).ToArray();
        }

        return _apps
            .Where(a => a.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase))
            .OrderBy(a => a.Name.StartsWith(text, StringComparison.CurrentCultureIgnoreCase) ? 0 : 1)
            .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(max)
            .ToArray();
    }

    public void Launch(AppEntry app)
    {
        try
        {
            Process.Start(new ProcessStartInfo(app.Path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    public void OpenWeb(string query)
    {
        var text = Uri.EscapeDataString(query.Trim());
        if (text.Length == 0)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo($"https://www.bing.com/search?q={text}") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    private static IEnumerable<string> GetStartMenuRoots()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.StartMenu);
    }
}
