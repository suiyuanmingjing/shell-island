using System.IO;
using System.Threading;
using System.Windows;

namespace ShellIsland;

public partial class App : Application
{
    private Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, "ShellIsland.SingleInstance", out var created);
        if (!created)
        {
            Shutdown();
            return;
        }

        base.OnStartup(e);
        Services.AppLog.Init();
        Services.AutoStartService.Enable();
        DispatcherUnhandledException += (_, args) =>
        {
            try
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ShellIsland");
                Directory.CreateDirectory(dir);
                File.AppendAllText(
                    Path.Combine(dir, "error.log"),
                    $"[{DateTime.Now:O}] {args.Exception}\n\n");
            }
            catch
            {
                // never let logging crash the shell
            }

            MessageBox.Show(args.Exception.Message, "ShellIsland", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        var shell = new MainWindow();
        MainWindow = shell;
        shell.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
