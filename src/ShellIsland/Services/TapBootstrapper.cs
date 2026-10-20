using System.Diagnostics;
using System.IO;

namespace ShellIsland.Services;

public static class TapBootstrapper
{
    public static async Task EnsureInjectedAsync()
    {
        var directory = AppContext.BaseDirectory;
        var tap = Path.Combine(directory, "XamlTap.dll");
        var injector = Path.Combine(directory, "Injector.exe");
        if (!File.Exists(tap) || !File.Exists(injector))
        {
            return;
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = injector,
                    Arguments = $"\"{tap}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = directory,
                });
                process?.WaitForExit(5000);
                if (process?.ExitCode == 0)
                {
                    return;
                }
            }
            catch
            {
                // Explorer may not be ready yet
            }

            await Task.Delay(1500);
        }
    }
}
