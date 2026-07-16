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
        var script = Path.Combine(repository, "hooks", "install-codex-hooks.ps1");
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

    private static string FindRepositoryRoot()
    {
        for (var current = new DirectoryInfo(Directory.GetCurrentDirectory()); current is not null; current = current.Parent)
            if (File.Exists(Path.Combine(current.FullName, "Halo.sln")))
                return current.FullName;
        throw new DirectoryNotFoundException("Could not find Halo.sln.");
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
