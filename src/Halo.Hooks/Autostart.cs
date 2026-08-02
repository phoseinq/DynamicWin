using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Halo.Hooks;

internal static class Autostart
{
    internal const string TaskName = "Halo";

    internal static void Install(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) throw new ArgumentException("autostart needs an executable path.");
        string xml = Path.Combine(Path.GetTempPath(), $"halo-autostart-{Guid.NewGuid():n}.xml");
        try
        {

            File.WriteAllText(xml, Xml(exePath), new UnicodeEncoding(false, true));
            if (Run("/Create", "/TN", TaskName, "/XML", xml, "/F") != 0)
                throw new InvalidOperationException("schtasks could not register the logon task.");
        }
        finally
        {
            try { File.Delete(xml); } catch { }

            RemoveLegacyShortcut();
        }
    }

    internal static void Uninstall()
    {
        try { Run("/Delete", "/TN", TaskName, "/F"); } catch { }
        RemoveLegacyShortcut();
    }

    internal static bool IsInstalled()
    {
        try { return Run("/Query", "/TN", TaskName) == 0; }
        catch { return false; }
    }

    private static void RemoveLegacyShortcut()
    {
        try
        {
            string startup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            foreach (var name in new[] { "Halo.lnk", "DynamicWin.lnk" })
            {
                string path = Path.Combine(startup, name);
                if (File.Exists(path)) File.Delete(path);
            }
        }
        catch { }
    }

    private static int Run(params string[] args)
    {
        var psi = new ProcessStartInfo("schtasks.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("schtasks did not start.");
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit(20_000);
        return p.HasExited ? p.ExitCode : 1;
    }

    private static string Xml(string exePath)
    {
        string user = Escape($"{Environment.UserDomainName}\\{Environment.UserName}");
        return $"""
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Author>{user}</Author>
    <Description>Starts Halo as soon as you sign in, ahead of the Startup folder queue.</Description>
    <URI>\{TaskName}</URI>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>{user}</UserId>
      <Delay>PT0S</Delay>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>{user}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>LeastPrivilege</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>false</AllowHardTerminate>
    <StartWhenAvailable>false</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>4</Priority>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>{Escape(exePath)}</Command>
    </Exec>
  </Actions>
</Task>
""";
    }

    private static string Escape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
