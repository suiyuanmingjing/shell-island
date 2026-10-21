using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace ShellIsland.Services;

public static class AutoStartService
{
    private const string TaskName = "ShellIslandAutoStart";

    public static void Enable()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe)) return;
            if (CreateLogonTask(exe))
            {
                TryDeleteRunValue();
                TryDeleteStartupShortcut();
                AppLog.Write("autostart task installed");
                return;
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"autostart task failed: {ex.Message}");
        }

        TryEnableRun();
    }

    private static bool CreateLogonTask(string exe)
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value;
        if (string.IsNullOrWhiteSpace(sid)) return false;

        var xmlPath = Path.Combine(Path.GetTempPath(), "shellisland-autostart.xml");
        var escapedExe = System.Security.SecurityElement.Escape(exe);
        var escapedDir = System.Security.SecurityElement.Escape(Path.GetDirectoryName(exe) ?? string.Empty);
        var xml = $"""
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo><Description>ShellIsland 登录自启</Description></RegistrationInfo>
  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{sid}</UserId></LogonTrigger></Triggers>
  <Principals><Principal id="Author"><UserId>{sid}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context="Author"><Exec><Command>{escapedExe}</Command><WorkingDirectory>{escapedDir}</WorkingDirectory></Exec></Actions>
</Task>
""";
        File.WriteAllText(xmlPath, xml, new UTF8Encoding(false));
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            Arguments = $"/Create /TN \"{TaskName}\" /XML \"{xmlPath}\" /F",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        if (process is null) return false;
        process.WaitForExit(15000);
        AppLog.Write($"autostart schtasks exit={process.ExitCode} {process.StandardOutput.ReadToEnd()} {process.StandardError.ReadToEnd()}");
        return process.ExitCode == 0;
    }

    private static void TryDeleteRunValue()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            key?.DeleteValue("ShellIsland", throwOnMissingValue: false);
        }
        catch
        {
        }
    }

    private static void TryDeleteStartupShortcut()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Startup), "ShellIsland.lnk");
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
        }
    }

    private static void TryEnableRun()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe)) return;
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            key?.SetValue("ShellIsland", $"\"{exe}\"");
        }
        catch
        {
        }
    }
}
