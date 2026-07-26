using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Halo.Update;

// Silent background updater: once a day, ask GitHub for the latest release, and if it is newer than what
// is running, fetch the signed installer and apply it. No prompt and no elevation — the install lives
// under %LOCALAPPDATA%, so replacing it needs nothing the user has to agree to.
//
// The one thing that is NOT skipped is proving where the installer came from. Running a downloaded exe
// with no questions asked is only safe if it is the exe we published, so the signer certificate is pinned
// by thumbprint. Chain validation would have been the obvious check and is the wrong one here: the
// certificate is self-signed, so it is trusted on the machine that built it and untrusted everywhere else
// — Status would come back Valid for the author and invalid for every actual user. A thumbprint pin is
// both stronger and portable. Rotating the certificate means changing SignerThumbprint, and until it is
// changed updates stop rather than silently accepting a different signer.
internal static class AutoUpdate
{
    private const string LatestApi = "https://api.github.com/repos/phoseinq/DynamicWin/releases/latest";
    private const string AssetName = "DynamicWinSetup.exe";
    private const string SignerThumbprint = "2EB268F09FEA535E92FB395FA2FAB4409EC22E1D";

    private static readonly TimeSpan Cadence = TimeSpan.FromHours(24);
    // Retry ladder after a failed attempt. Most failures here are "this machine is offline", and offline
    // machines are the normal case for this app, so a failed check must not turn into a poll: half an hour,
    // then a few hours, then a day — each step tried once before moving to the next.
    private static readonly TimeSpan[] Backoff =
        { TimeSpan.FromMinutes(30), TimeSpan.FromHours(6), TimeSpan.FromHours(12), TimeSpan.FromHours(24) };

    private static Timer? _timer;

    public static void Start()
    {
        // Wake up often enough to notice a due check without the timer itself being the schedule; the
        // schedule lives on disk, so it survives restarts instead of resetting every launch.
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

    // ── schedule, persisted next to the rest of the loose runtime state ───────────────────────────────
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

    // ── the attempt ───────────────────────────────────────────────────────────────────────────────────
    private static async Task RunOnce()
    {
        var (_, fails) = State();
        bool ok = false;
        try { ok = await TryUpdate(); }
        catch { }
        // "nothing new" is a successful check: it proves we reached GitHub, so the ladder resets.
        Save(DateTime.UtcNow, ok ? 0 : fails + 1);
    }

    private static async Task<bool> TryUpdate()
    {
        // A portable copy has no installer to run, and running one would install a SECOND copy somewhere
        // else instead of updating this one.
        string exe = Environment.ProcessPath ?? "";
        string installed = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Halo");
        if (!exe.StartsWith(installed, StringComparison.OrdinalIgnoreCase)) return true;

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Halo-Updater");   // GitHub rejects a missing UA

        string json = await http.GetStringAsync(LatestApi);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        string tag = root.GetProperty("tag_name").GetString() ?? "";
        var current = typeof(AutoUpdate).Assembly.GetName().Version ?? new Version(0, 0);
        if (!IsNewer(tag, current)) return true;   // already current — a good outcome, not a failure

        string? url = null;
        foreach (var a in root.GetProperty("assets").EnumerateArray())
            if (string.Equals(a.GetProperty("name").GetString(), AssetName, StringComparison.OrdinalIgnoreCase))
            { url = a.GetProperty("browser_download_url").GetString(); break; }
        if (url == null) return true;   // release without an installer is not a failure to retry quickly

        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Halo", "update");
        Directory.CreateDirectory(dir);
        string part = Path.Combine(dir, AssetName + ".part"), final = Path.Combine(dir, AssetName);

        // Download to .part and only then rename. A connection dropped mid-transfer therefore leaves no
        // half-written installer that a later run could mistake for a complete one — the retry ladder
        // handles it as an ordinary failure and starts again.
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

    // AssemblyVersion is four-part (3.1.0.0) and a tag is three ("v3.1.0"), and Version compares the parts
    // it has: parsed "3.1.0" has Revision -1, which is LESS than 3.1.0.0, so a straight comparison would
    // call an identical release "older" — or, with the operands the other way, offer the same build
    // forever. Both sides are flattened to three parts before comparing. An unparseable tag is treated as
    // "nothing to do", never as an update.
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
            // SYSLIB0057 points at X509CertificateLoader, which loads certificate BLOBS and cannot read an
            // Authenticode signer out of a file — there is no managed replacement for this call. The
            // alternative is CryptQueryObject/WinVerifyTrust interop for the one hash we need.
            // GetCertHashString is the SHA-1 thumbprint, the same value signtool prints.
#pragma warning disable SYSLIB0057
            using var cert = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            return string.Equals(cert.GetCertHashString(), SignerThumbprint, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }   // unsigned, or not signed in a way we can read → do not run it
    }

    // The installer replaces the very exe running this code, so the last steps cannot live in this process.
    // Hand them to PowerShell, which lives in System32 and so is never one of the files being replaced:
    // wait for us to exit, install, start the new build. Halo.Hooks would also have worked, except it is
    // part of the install and would be replaced mid-run.
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
