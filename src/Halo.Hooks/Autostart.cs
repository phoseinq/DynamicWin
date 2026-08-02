using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Halo.Hooks;

// "Start with Windows", as a scheduled task rather than a Startup-folder shortcut.
//
// The shortcut worked, it was just last in line every single boot. Explorer does not launch what is in
// the Startup folder (or in the Run key) when it finds it: it holds those items back until the desktop
// has settled, then releases them ONE AT A TIME with their I/O priority knocked down, so a pill that is
// meant to be sitting at the top of the screen when you arrive shows up thirty seconds into the session,
// after everything else. Nothing about Halo is slow - it is being queued.
//
// A logon-triggered task is not in that queue. The knobs below are the ones that actually matter, and
// each of them has bitten somebody:
//   Delay PT0S                  - the task UI's default is a randomised delay, which is the same disease
//   Priority 4                  - tasks default to 7 (below normal), which hands back the sluggish start
//                                 we came here to fix. 4 is what a normally-launched process gets.
//   ExecutionTimeLimit PT0S     - unlimited. The default is three days, after which Task Scheduler would
//                                 terminate a perfectly healthy pill.
//   DisallowStartIfOnBatteries  - default TRUE, which means it silently never starts on a laptop.
//   MultipleInstancesPolicy     - IgnoreNew, so a manual launch plus the task cannot race.
//
// schtasks.exe rather than the Task Scheduler COM API: it is already on every Windows box, a standard
// user may register a task that runs as themselves without any elevation, and the whole thing stays
// readable as a file you can `type`. No new dependency, which is the rule here.
internal static class Autostart
{
    internal const string TaskName = "Halo";

    internal static void Install(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) throw new ArgumentException("autostart needs an executable path.");
        string xml = Path.Combine(Path.GetTempPath(), $"halo-autostart-{Guid.NewGuid():n}.xml");
        try
        {
            // UTF-16 with a BOM: schtasks /XML rejects a UTF-8 file with an unhelpful "The task XML is
            // malformed", which is a long way from "wrong encoding".
            File.WriteAllText(xml, Xml(exePath), new UnicodeEncoding(false, true));
            if (Run("/Create", "/TN", TaskName, "/XML", xml, "/F") != 0)
                throw new InvalidOperationException("schtasks could not register the logon task.");
        }
        finally
        {
            try { File.Delete(xml); } catch { }
            // Older installs put a shortcut in the Startup folder. Leaving it would launch Halo twice -
            // survivable, since the app holds a single-instance mutex, but the second copy would be doing
            // exactly the slow queued start this task exists to escape.
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

    // Schema 1.2 on purpose: everything used here exists in it, and it is understood by every Windows
    // this app runs on. UserId is spelled out on both the trigger and the principal so the task belongs
    // to this account and fires for this account only.
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
