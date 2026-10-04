using System.Windows.Media;

namespace ShellIsland.Models;

public sealed record AppEntry(string Name, string Path)
{
    public string Initial => string.IsNullOrWhiteSpace(Name)
        ? "?"
        : Name[..1].ToUpperInvariant();

    public ImageSource? Icon { get; init; }
}
