using System.IO;

namespace ShellIsland.Services;

public static class AppLog
{
    private static readonly object Gate = new();
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShellIsland");
    private static readonly string FilePath = Path.Combine(DirectoryPath, "app.log");

    public static void Init()
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            File.WriteAllText(FilePath, $"[{DateTime.Now:O}] app start{Environment.NewLine}");
        }
        catch
        {
        }
    }

    public static void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            lock (Gate)
            {
                File.AppendAllText(FilePath, $"[{DateTime.Now:O}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
        }
    }
}
