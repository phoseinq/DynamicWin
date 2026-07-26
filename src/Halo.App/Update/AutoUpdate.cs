using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Halo.Update;

internal static class AutoUpdate
{
    private const string LatestApi = "https://api.github.com/repos/phoseinq/DynamicWin/releases/latest";
    private const string AssetName = "DynamicWinSetup.exe";
    private const string SignerThumbprint = "2EB268F09FEA535E92FB395FA2FAB4409EC22E1D";

    private static readonly TimeSpan Cadence = TimeSpan.FromHours(24);

    private static readonly TimeSpan[] Backoff =
        { TimeSpan.FromMinutes(30), TimeSpan.FromHours(6), TimeSpan.FromHours(12), TimeSpan.FromHours(24) };

    private static Timer? _timer;

    public static void Start()
    {

        _timer ??= new Timer(_ => Tick(), null, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(15));
    }

    private static void Tick()
    {
        try
        {
            if (!Due()) return;
            _ = Task.Run(RunOnce);
        }
        catch { }
    }

    private static string StatePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Halo", "update-check");

    private static (DateTime last, int fails) State()
    {
        try
        {
            var parts = File.ReadAllText(StatePath).Split(' ');
            return (new DateTime(long.Parse(parts[0]), DateTimeKind.Utc),
                    parts.Length > 1 ? int.Parse(parts[1]) : 0);
        }
        catch { return (DateTime.MinValue, 0); }
    }

    private static void Save(DateTime last, int fails)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            File.WriteAllText(StatePath, $"{last.Ticks} {fails}");
        }
        catch { }
    }

    internal static TimeSpan Wait(int fails) =>
        fails <= 0 ? Cadence : Backoff[Math.Min(fails - 1, Backoff.Length - 1)];

    private static bool Due()
    {
        var (last, fails) = State();
        return DateTime.UtcNow - last >= Wait(fails);
    }

    private static async Task RunOnce()
    {
        var (_, fails) = State();
        bool ok = false;
        try { ok = await TryUpdate(); }
        catch (Exception ex) { Log("failed: " + ex.GetType().Name + ": " + ex.Message); }

        int next = ok ? 0 : fails + 1;
        Log($"attempt ok={ok} fails={next} nextIn={Wait(next)}");
        Save(DateTime.UtcNow, next);
    }

    internal static void Log(string s)
    {
        try
        {
            string p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                    "Halo", "update-log.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.AppendAllText(p, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {s}{Environment.NewLine}");
        }
        catch { }
    }

    private static async Task<bool> TryUpdate()
    {

        string exe = Environment.ProcessPath ?? "";
        string installed = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Halo");
        if (!exe.StartsWith(installed, StringComparison.OrdinalIgnoreCase)) return true;

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Halo-Updater");

        string json = await http.GetStringAsync(LatestApi);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        string tag = root.GetProperty("tag_name").GetString() ?? "";
        var current = typeof(AutoUpdate).Assembly.GetName().Version ?? new Version(0, 0);
        Log($"latest={tag} running={current}");
        if (!IsNewer(tag, current)) return true;

        string? url = null;
        foreach (var a in root.GetProperty("assets").EnumerateArray())
            if (string.Equals(a.GetProperty("name").GetString(), AssetName, StringComparison.OrdinalIgnoreCase))
            { url = a.GetProperty("browser_download_url").GetString(); break; }
        if (url == null) return true;

        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Halo", "update");
        Directory.CreateDirectory(dir);
        string part = Path.Combine(dir, AssetName + ".part"), final = Path.Combine(dir, AssetName);

        try
        {
            using (var src = await http.GetStreamAsync(url))
            using (var dst = File.Create(part))
                await src.CopyToAsync(dst);
            File.Move(part, final, overwrite: true);
        }
        catch { try { File.Delete(part); } catch { } throw; }

        if (!SignedByUs(final)) { try { File.Delete(final); } catch { } return false; }

        Apply(final, exe);
        return true;
    }

    internal static bool IsNewer(string tag, Version current)
    {
        if (!Version.TryParse((tag ?? "").TrimStart('v', 'V'), out var latest)) return false;
        return Flatten(latest) > Flatten(current);
    }

    private static Version Flatten(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    private static bool SignedByUs(string path)
    {
        try
        {

#pragma warning disable SYSLIB0057
            using var cert = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            return string.Equals(cert.GetCertHashString(), SignerThumbprint, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static void Apply(string installer, string exe)
    {
        try
        {
            string cmd =
                $"Wait-Process -Id {Environment.ProcessId} -Timeout 60 -ErrorAction SilentlyContinue; " +
                $"Start-Process '{installer.Replace("'", "''")}' " +
                "-ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART' -Wait; " +
                $"Start-Process '{exe.Replace("'", "''")}'";
            Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                                        @"WindowsPowerShell\v1.0\powershell.exe"),
                Arguments = "-NoProfile -NonInteractive -WindowStyle Hidden -Command \"" + cmd + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            Environment.Exit(0);
        }
        catch { }
    }
}
