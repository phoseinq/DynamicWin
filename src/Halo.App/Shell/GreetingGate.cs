using System;
using System.IO;

namespace Halo.Shell;

internal enum GreetingKind
{
    None,
    Install,
    Login,
}

internal static class GreetingGate
{
    internal static GreetingKind Decide(string? marker, string version)
        => string.IsNullOrWhiteSpace(marker) || marker.Trim() != version
            ? GreetingKind.Install
            : GreetingKind.Login;

    internal static string Version =>
        typeof(GreetingGate).Assembly.GetName().Version?.ToString() ?? "0";

    internal static GreetingKind Read(string path)
    {
        try { return Decide(File.Exists(path) ? File.ReadAllText(path) : null, Version); }
        catch { return GreetingKind.Login; }
    }

    internal static void Mark(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, Version);
        }
        catch { }
    }
}
