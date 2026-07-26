using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Halo.Tests;

public sealed class CodexHookTests
{
    [Fact]
    public async Task CodexPrompt_WritesSurfaceSpecificWorkingStatus()
    {
        using var directory = new TempDirectory();

        var result = await RunHooks("codex prompt", "{\"cwd\":\"C:\\\\repo\",\"prompt\":\"fix it\"}", directory.Path,
            new Dictionary<string, string?> { ["HALO_CODEX_SURFACE"] = "desktop" });

        var json = ReadStatus(directory.Path, "desktop");
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("working", json["state"]!.GetValue<string>());
        Assert.Equal("desktop", json["source"]!.GetValue<string>());
        Assert.Equal("C:\\repo", json["cwd"]!.GetValue<string>());
        Assert.False(File.Exists(Path.Combine(directory.Path, "desktop.json.tmp")));
    }

    [Theory]
    [InlineData("session-start", "idle")]
    [InlineData("prompt", "working")]
    [InlineData("tool", "working")]
    [InlineData("tool-done", "working")]
    [InlineData("pre-compact", "compacting")]
    [InlineData("post-compact", "working")]
    [InlineData("stop", "idle")]
    public async Task CodexLifecycle_MapsEveryInstalledEvent(string command, string expectedState)
    {
        using var directory = new TempDirectory();

        var result = await RunHooks($"codex {command}", "{\"session_id\":\"session-1\",\"cwd\":\"C:\\\\repo\",\"tool_name\":\"shell\"}", directory.Path,
            new Dictionary<string, string?> { ["HALO_CODEX_SURFACE"] = "cli" });

        var json = ReadStatus(directory.Path, "cli");
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expectedState, json["state"]!.GetValue<string>());
        Assert.Equal("cli", json["source"]!.GetValue<string>());
        if (command == "tool")
            Assert.Equal("shell", json["currentTool"]!.GetValue<string>());
        if (command == "post-compact")
            Assert.NotNull(json["compactedAt"]);
    }

    [Fact]
    public async Task CodexInstaller_PrefersInstalledHookBinaryForShellSessions()
    {
        using var root = new TempDirectory();
        var codexDirectory = Path.Combine(root.Path, ".codex");
        Directory.CreateDirectory(codexDirectory);
        File.WriteAllText(Path.Combine(codexDirectory, "hooks.json"), "{}");
        var installed = Path.Combine(root.Path, "AppData", "Local", "Programs", "Halo", "Halo.Hooks.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(installed)!);
        CopyHookHost(Path.GetDirectoryName(installed)!);

        var result = await RunInstaller(root.Path);

        var settings = JsonNode.Parse(File.ReadAllText(Path.Combine(codexDirectory, "hooks.json")))!.AsObject();
        var command = settings["hooks"]!["SessionStart"]![0]!["hooks"]![0]!["command"]!.GetValue<string>();
        Assert.True(result.ExitCode == 0, result.Error);
        Assert.Contains(installed, command, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CodexInstaller_PreservesUnrelatedHandlersAndReplacesCodexHandlers()
    {
        using var root = new TempDirectory();
        var codexDirectory = Path.Combine(root.Path, ".codex");
        Directory.CreateDirectory(codexDirectory);
        var settingsPath = Path.Combine(codexDirectory, "hooks.json");
        const string existing = """
            {
              "hooks": {
                "SessionStart": [{
                  "hooks": [
                    { "type": "command", "command": "keep.exe --still-here" },
                    { "type": "command", "command": "\\\"C:\\\\old\\\\Halo.Hooks.exe\\\" codex obsolete" }
                  ]
                }]
              }
            }
            """;
        File.WriteAllText(settingsPath, existing);

        var result = await RunInstaller(root.Path);

        var settings = JsonNode.Parse(File.ReadAllText(settingsPath))!.AsObject();
        var commands = settings["hooks"]!["SessionStart"]!.AsArray()
            .SelectMany(entry => entry!["hooks"]!.AsArray())
            .Select(hook => hook!["command"]!.GetValue<string>())
            .ToArray();
        Assert.True(result.ExitCode == 0, result.Error);
        Assert.Contains("keep.exe --still-here", commands);
        Assert.DoesNotContain(commands, command => command.Contains("Halo.Hooks.exe\" codex obsolete", StringComparison.Ordinal));
        Assert.Single(commands, command => command.Contains("Halo.Hooks.exe\" codex session-start", StringComparison.Ordinal));
        Assert.Equal(existing, File.ReadAllText(settingsPath + ".halo-bak"));
    }

    [Fact]
    public async Task OfflineInstaller_MergesHooksAndIsIdempotent()
    {
        using var root = new TempDirectory();
        var settingsPath = Path.Combine(root.Path, "hooks.json");
        const string existing = """
            {
              "hooks": {
                "SessionStart": [{ "hooks": [{ "type": "command", "command": "keep.exe --still-here" }] }]
              }
            }
            """;
        File.WriteAllText(settingsPath, existing);
        var hookExe = Path.Combine(root.Path, "installed app", "Halo.Hooks.exe");

        var first = await RunSetupCommand(settingsPath, "install-codex-hooks", hookExe);
        var second = await RunSetupCommand(settingsPath, "install-codex-hooks", hookExe);

        var settings = JsonNode.Parse(File.ReadAllText(settingsPath))!.AsObject();
        var commands = AllCommands(settings);
        Assert.True(first.ExitCode == 0, first.Error);
        Assert.True(second.ExitCode == 0, second.Error);
        Assert.Equal(7, commands.Count(command => IsHaloCommand(command)));
        Assert.All(commands.Where(IsHaloCommand), command => Assert.Contains(hookExe, command, StringComparison.OrdinalIgnoreCase));
        Assert.Contains("keep.exe --still-here", commands);
        Assert.True(File.Exists(settingsPath + ".halo-bak"));
    }

    [Fact]
    public async Task OfflineInstaller_UninstallRemovesOnlyHaloHandlers()
    {
        using var root = new TempDirectory();
        var settingsPath = Path.Combine(root.Path, "hooks.json");
        File.WriteAllText(settingsPath, """
            {
              "hooks": {
                "SessionStart": [{ "hooks": [{ "type": "command", "command": "keep.exe --still-here" }] }]
              }
            }
            """);
        var hookExe = Path.Combine(root.Path, "Halo.Hooks.exe");
        var install = await RunSetupCommand(settingsPath, "install-codex-hooks", hookExe);
        var backupAfterInstall = File.ReadAllText(settingsPath + ".halo-bak");

        var uninstall = await RunSetupCommand(settingsPath, "uninstall-codex-hooks");

        var commands = AllCommands(JsonNode.Parse(File.ReadAllText(settingsPath))!.AsObject());
        Assert.True(install.ExitCode == 0, install.Error);
        Assert.True(uninstall.ExitCode == 0, uninstall.Error);
        Assert.Contains("keep.exe --still-here", commands);
        Assert.DoesNotContain(commands, IsHaloCommand);
        Assert.Equal(backupAfterInstall, File.ReadAllText(settingsPath + ".halo-bak"));
    }

    [Fact]
    public async Task OfflineInstaller_MalformedJsonFailsWithoutChangingFile()
    {
        using var root = new TempDirectory();
        var settingsPath = Path.Combine(root.Path, "hooks.json");
        const string malformed = "{ definitely not json";
        File.WriteAllText(settingsPath, malformed);

        var result = await RunSetupCommand(settingsPath, "install-codex-hooks", Path.Combine(root.Path, "Halo.Hooks.exe"));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(malformed, File.ReadAllText(settingsPath));
        Assert.False(File.Exists(settingsPath + ".halo-bak"));
    }

    private static async Task<ProcessResult> RunSetupCommand(string settingsPath, params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Halo.Hooks.dll"));
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        start.Environment["HALO_CODEX_HOOKS_PATH"] = settingsPath;

        using var process = Process.Start(start)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new ProcessResult(process.ExitCode, output, error);
    }

    private static string[] AllCommands(JsonObject settings)
    {
        var commands = new List<string>();
        if (settings["hooks"] is not JsonObject hooks) return [];
        foreach (var (_, eventNode) in hooks)
        {
            if (eventNode is not JsonArray entries) continue;
            foreach (var entry in entries.OfType<JsonObject>())
            {
                if (entry["hooks"] is not JsonArray handlers) continue;
                commands.AddRange(handlers.OfType<JsonObject>()
                    .Select(handler => handler["command"]?.GetValue<string>())
                    .Where(command => command is not null)!);
            }
        }
        return [.. commands];
    }

    private static bool IsHaloCommand(string command) =>
        command.Contains("Halo.Hooks.exe", StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void InstallerScript_WiresOfflineCodexInstallAndUninstall()
    {
        var script = File.ReadAllText(RequireRepoFile("installer", "Halo.iss"));

        Assert.Contains("Name: \"codexhooks\"", script, StringComparison.Ordinal);
        Assert.Contains("install-codex-hooks \"\"{app}\\Halo.Hooks.exe\"\"", script, StringComparison.Ordinal);
        Assert.Contains("Parameters: \"uninstall-codex-hooks\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("pwsh", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("dotnet", script, StringComparison.OrdinalIgnoreCase);
    }

    private static JsonObject ReadStatus(string directory, string surface) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(directory, $"{surface}.json")))!.AsObject();

    private static async Task<ProcessResult> RunHooks(
        string arguments, string input, string directory, IReadOnlyDictionary<string, string?> environment)
    {
        var start = new ProcessStartInfo("dotnet", $"\"{Path.Combine(AppContext.BaseDirectory, "Halo.Hooks.dll")}\" {arguments}")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.Environment["HALO_CODEX_STATUS_DIR"] = directory;
        foreach (var pair in environment)
            start.Environment[pair.Key] = pair.Value;

        using var process = Process.Start(start)!;
        await process.StandardInput.WriteAsync(input);
        process.StandardInput.Close();
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new ProcessResult(process.ExitCode, output, error);
    }

    private static async Task<ProcessResult> RunInstaller(string userProfile)
    {
        var repository = FindRepositoryRoot();
        var script = RequireRepoFile("hooks", "install-codex-hooks.ps1");
        var start = new ProcessStartInfo("pwsh",
            $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -Repo \"{repository}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.Environment["USERPROFILE"] = userProfile;
        start.Environment["LOCALAPPDATA"] = Path.Combine(userProfile, "AppData", "Local");

        using var process = Process.Start(start)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new ProcessResult(process.ExitCode, output, error);
    }

    private static void CopyHookHost(string destination)
    {
        foreach (var name in new[]
        {
            "Halo.Hooks.exe",
            "Halo.Hooks.dll",
            "Halo.Hooks.deps.json",
            "Halo.Hooks.runtimeconfig.json",
        })
            File.Copy(Path.Combine(AppContext.BaseDirectory, name), Path.Combine(destination, name), overwrite: true);
    }

    private static string FindRepositoryRoot()
    {
        for (var current = new DirectoryInfo(Directory.GetCurrentDirectory()); current is not null; current = current.Parent)
            if (File.Exists(Path.Combine(current.FullName, "Halo.sln")))
                return current.FullName;
        throw new DirectoryNotFoundException("Could not find Halo.sln.");
    }

    // installer/ and hooks/ are private: the public mirror ships src/ and tests/ only, so on the public
    // repo's CI these files genuinely are not there. Skipping is the honest answer — locally the file
    // exists and the assertion runs for real; a hard failure would just mean the mirror can never be
    // green, and passing silently would hide a real regression here.
    // xunit 2.x has no Assert.Skip; a message starting with this token is how its runner is told to
    // report a skip from inside a running test
    private const string DynamicSkip = "$XunitDynamicSkip$";

    private static string RequireRepoFile(params string[] parts)
    {
        var path = Path.Combine([FindRepositoryRoot(), .. parts]);
        if (!File.Exists(path))
            throw new InvalidOperationException(
                $"{DynamicSkip}{Path.Combine(parts)} is not part of this checkout (private build asset)");
        return path;
    }

    private sealed record ProcessResult(int ExitCode, string Output, string Error);

    private sealed class TempDirectory : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"halo-codex-hook-tests-{Guid.NewGuid():N}");

        internal TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
