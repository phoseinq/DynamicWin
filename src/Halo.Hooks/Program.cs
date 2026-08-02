using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Halo.Hooks;

internal static class Program
{
    private static readonly string ClaudeDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "notch");
    private static readonly string ClaudeStatusPath = Path.Combine(ClaudeDir, "status.json");
    private static readonly string CodexDir = Environment.GetEnvironmentVariable("HALO_CODEX_STATUS_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "notch");

    private static int Main(string[] args)
    {
        // The installer, the uninstaller and the settings panel all drive autostart through here, so there
        // is one definition of what "start with Windows" means instead of three that drift apart.
        if (args.Length > 0 && args[0] is "install-autostart" or "uninstall-autostart" or "query-autostart")
        {
            try
            {
                switch (args[0])
                {
                    case "install-autostart":
                        if (args.Length != 2) throw new ArgumentException("install-autostart requires an executable path.");
                        Autostart.Install(args[1]);
                        break;
                    case "uninstall-autostart":
                        Autostart.Uninstall();
                        break;
                    default:
                        return Autostart.IsInstalled() ? 0 : 2;   // exit code is the answer; nothing is printed
                }
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error.Message);
                return 1;
            }
        }

        if (args.Length > 0 && args[0] is "install-codex-hooks" or "uninstall-codex-hooks")
        {
            try
            {
                var settingsPath = Environment.GetEnvironmentVariable("HALO_CODEX_HOOKS_PATH");
                if (string.IsNullOrWhiteSpace(settingsPath))
                    settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "hooks.json");

                if (args[0] == "install-codex-hooks")
                {
                    if (args.Length != 2)
                        throw new ArgumentException("install-codex-hooks requires an executable path.");
                    CodexHookInstaller.Install(settingsPath, args[1]);
                }
                else
                {
                    CodexHookInstaller.Uninstall(settingsPath);
                }
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error.Message);
                return 1;
            }
        }

        try
        {
            if (args.Length == 0) return 0;
            var codex = args.Length >= 2 && args[0] == "codex";
            var cmd = codex ? args[1] : args[0];

            if (cmd == "cancel")
            {
                if (args.Length >= 2 && int.TryParse(args[1], out var pid))
                    Cancel(pid);
                return 0;
            }

            CodexSurface? surface = codex ? DetectCodexSurface() : null;
            var dir = codex ? CodexDir : ClaudeDir;
            // claude splits by surface: each CLI session gets status-{agentPid}.json (multi-session),
            // the desktop app gets app.json; pid unknown → legacy status.json
            uint agentPid = 0;
            var path = codex ? CodexStatusPath(surface!.Value)
                : IsClaudeApp() ? Path.Combine(ClaudeDir, "app.json") : ClaudeSessionPath(out agentPid);
            Directory.CreateDirectory(dir);
            var input = ReadInput();
            var status = LoadOrNew(path);
            // the file is keyed by this pid — stamp it on every event, or a session file born from
            // a mid-turn event stays pidless and evades the store's per-pid dedupe
            if (agentPid != 0) status["pid"] = (int)agentPid;

            if (cmd == "session-end" && !codex && path != ClaudeStatusPath)
            {
                try { File.Delete(path); } catch { }
                try { File.Delete(ClaudeStatusPath); } catch { } // stale pre-multi-session leftover
                return 0;
            }

            string? Field(string name) => input?[name]?.GetValue<string>();

            if (codex)
            {
                status["source"] = surface == CodexSurface.Desktop ? "desktop" : "cli";
                if (Field("cwd") is { } cwd) status["cwd"] = cwd;
            }

            switch (cmd)
            {
                case "session-start":
                    if (!codex) SweepDeadSessions(); // killed terminals never fire session-end
                    status["sessionId"] = Field("session_id");
                    status["cwd"] = Field("cwd");
                    status["state"] = "idle";
                    if (Field("source") == "compact") // session restarting after a compact = it finished
                        status["compactedAt"] = DateTimeOffset.UtcNow.ToString("o");
                    else if (Field("source") is "clear" or "startup") // fresh context — drop the stale numbers
                        status.Remove("session");
                    RecordProcess(status, codex);
                    break;
                case "pre-compact":
                    status["state"] = "compacting";
                    status["startedAt"] = DateTimeOffset.UtcNow.ToString("o");
                    status["message"] = null;
                    // the pill shows how full the context is while the compact runs, so read it here
                    // rather than leaving the figure at whatever the last tool call happened to see
                    UpdateContext(status, Field("transcript_path"));
                    break;
                case "prompt":
                    status["state"] = "working";
                    status["lastPrompt"] = Truncate(Field("prompt"), 120);
                    status["currentTool"] = null;
                    status["toolTarget"] = null;
                    status["startedAt"] = DateTimeOffset.UtcNow.ToString("o"); // turn start, for elapsed time
                    status["message"] = null;
                    RecordProcess(status, codex);
                    UpdateContext(status, Field("transcript_path"));
                    break;
                case "tool":
                    status["state"] = "working";
                    status["currentTool"] = Field("tool_name");
                    status["toolTarget"] = ToolTarget(input?["tool_name"]?.GetValue<string>(),
                        AsObject(input?["tool_input"]));
                    break;
                case "tool-done":
                    status["state"] = "working";
                    // tool finished → clear the label so the ring + verb flip to thinking (yellow) between
                    // tool calls, not just at turn start. (Was kept to avoid flicker, but the user wants
                    // "thinking" to read yellow; the next tool sets it green again.)
                    status["currentTool"] = null;
                    status["toolTarget"] = null;
                    UpdateContext(status, Field("transcript_path"));
                    break;
                case "post-compact":
                    // lastCompactMs used to be recorded here to pace a "~47%" progress estimate on the
                    // pill. That estimate is gone (nothing reports compaction progress, so it was invented),
                    // and with it the only reader of the duration.
                    // auto-compact happens mid-turn (the turn resumes); manual /compact goes idle
                    status["state"] = codex || Field("trigger") == "auto" ? "working" : "idle";
                    status["compactedAt"] = DateTimeOffset.UtcNow.ToString("o");
                    if (!codex)
                    {
                        if (Field("trigger") != "auto") status["startedAt"] = null;
                        UpdateContext(status, Field("transcript_path"));
                    }
                    break;
                case "notify":
                    // Notification fires for two different things: a mid-turn permission prompt, which
                    // genuinely wants the user (amber ring, "your move ;)"), and a plain "waiting for your
                    // input" once the turn is already over. Unconditionally writing waiting_input meant the
                    // ring sat amber after every finished turn instead of going white. The previous state
                    // separates them without depending on the notification's wording: mid-turn we are still
                    // working/compacting, whereas after stop we are already idle and must stay that way.
                    // This also survives either firing order, since stop still writes idle afterwards.
                    var prevState = status["state"]?.GetValue<string>();
                    if (prevState is "working" or "compacting") status["state"] = "waiting_input";
                    status["message"] = Truncate(Field("message"), 160); // what Claude is asking
                    break;
                case "stop":
                    status["state"] = "idle";
                    status["currentTool"] = null;
                    status["toolTarget"] = null;
                    status["startedAt"] = null;
                    status["message"] = null;
                    UpdateContext(status, Field("transcript_path"));
                    break;
                case "session-end":
                    status["state"] = "idle";
                    status["currentTool"] = null;
                    status["toolTarget"] = null;
                    status["startedAt"] = null;
                    break;
                default:
                    return 0;
            }

            status["updatedAt"] = DateTimeOffset.UtcNow.ToString("o");
            Save(status, path);

            // After the save, deliberately: the pill has to have this tool call on screen before the hook
            // parks itself for up to 20 seconds waiting for someone to click a chip. Claude only, because
            // Codex has no equivalent decision channel — that is a stated non-goal, not an omission.
            int askOwner = status["pid"] is JsonValue pv && pv.TryGetValue<int>(out var askPid) ? askPid : 0;
            if (cmd == "tool" && !codex)
                AskFlow.Run(ClaudeDir, input, Field("session_id"), Field("cwd"), askOwner);

            // The question is no longer answered by this hook, so nothing else would ever take it down:
            // the box in the terminal has been dealt with by the time the tool finishes, however it was
            // dealt with, and the mirrored banner has to go with it.
            //
            // tool-done alone was not enough, and the way it failed was ugly: picking "Chat about this" (or
            // Esc) REJECTS the call, and a rejected call never reaches PostToolUse. So the file stayed, and
            // the pill sat there mirroring a question nobody was being asked until the 30-minute backstop
            // expired it - verified live, banner still up five minutes after the box was gone. prompt and
            // stop are the two events that do fire on that path, and both mean the same thing: this agent
            // is doing something else now, so whatever it had parked is over.
            bool questionOver = cmd is "prompt" or "stop"
                || (cmd == "tool-done" && Field("tool_name") == "AskUserQuestion");
            if (questionOver && !codex) AskFlow.Clear(ClaudeDir, askOwner);

            return 0;
        }
        catch
        {
            return 0;
        }
    }

    private static JsonObject? ReadInput()
    {
        try
        {
            // Console.In decodes with the console's OEM code page, and the hook payload is UTF-8 JSON, so
            // every non-ASCII character arrived mangled: a Persian "د" is D8 AF, and CP437 renders those
            // two bytes as "╪»". That reached the pill through cwd and lastPrompt — a Persian prompt or a
            // path like ...\دسکتاپ\... showed as box-drawing garbage. Read the raw stream instead; this
            // bypasses the console code page entirely and cannot be undone by whatever the host set it to.
            using var stdin = Console.OpenStandardInput();
            using var reader = new System.IO.StreamReader(stdin, new System.Text.UTF8Encoding(false));
            var text = reader.ReadToEnd();
            if (string.IsNullOrWhiteSpace(text)) return null;
            return JsonNode.Parse(text) as JsonObject;
        }
        catch
        {
            return null;
        }
    }

    private static string CodexStatusPath(CodexSurface surface) =>
        Path.Combine(CodexDir, surface == CodexSurface.Desktop ? "desktop.json" : "cli.json");

    // drop per-session files whose claude process is gone (kill/crash skips the session-end hook)
    private static void SweepDeadSessions()
    {
        try
        {
            foreach (var f in Directory.GetFiles(ClaudeDir, "status-*.json"))
            {
                try
                {
                    var pid = (JsonNode.Parse(File.ReadAllText(f)) as JsonObject)?["pid"]?.GetValue<int>() ?? 0;
                    bool alive = false;
                    if (pid > 0)
                        try { using var p = System.Diagnostics.Process.GetProcessById(pid); alive = !p.HasExited; }
                        catch { }
                    if (!alive) File.Delete(f);
                }
                catch { }
            }
        }
        catch { }
    }

    // per-session file keyed by the claude process pid — stable across compact//clear, unique per terminal
    private static string ClaudeSessionPath(out uint pid)
    {
        pid = Ancestor(ProcessMap(), (uint)Environment.ProcessId,
            n => n.Contains("claude") || n == "node.exe");
        return pid == 0 ? ClaudeStatusPath : Path.Combine(ClaudeDir, $"status-{pid}.json");
    }

    private static JsonObject LoadOrNew(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var text = File.ReadAllText(path);
                if (JsonNode.Parse(text) is JsonObject o) return o;
            }
        }
        catch
        {
        }
        return new JsonObject();
    }

    private static void Save(JsonObject status, string path)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, status.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// What the tool is acting ON, from the hook's tool_input, in a form short enough for a 220px pill.
    /// "running…" cannot tell a three-second `git status` from a two-minute `dotnet build`, and that is the
    /// one thing the pill could not say about a tool call. A different key per tool because there is no
    /// general answer: a file for the file tools, the PROGRAM for a shell command (not the whole line - the
    /// verb is the news and the flags are noise), the host for a fetch, the pattern for a search.
    ///
    /// Returns null rather than guessing whenever the shape is not what is expected: an empty pill line is
    /// honest, an invented one is not, and the widget has a voice to fall back on.
    /// </summary>
    // tool_input arrives as an object from some surfaces and as a JSON STRING from others - measured live:
    // the field was written but always null, because `as JsonObject` on a stringified payload is null and
    // the extractor then had nothing to read. Accept both rather than guess which surface is talking.
    internal static JsonObject? AsObject(JsonNode? node)
    {
        if (node is JsonObject o) return o;
        try
        {
            if (node is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
                return JsonNode.Parse(s) as JsonObject;
        }
        catch { }
        return null;
    }

    internal static string? ToolTarget(string? tool, JsonObject? input)
    {
        if (tool is null || input is null) return null;
        string? Str(string key)
        {
            try { return input[key]?.GetValue<string>()?.Trim() is { Length: > 0 } v ? v : null; }
            catch { return null; }   // a non-string under a key we expected to be one
        }

        var raw = tool switch
        {
            "Edit" or "Write" or "MultiEdit" or "NotebookEdit" or "Read" => Leaf(Str("file_path")),
            "Bash" or "PowerShell" => Program_(Str("command")),
            "Grep" or "Glob" => Str("pattern"),
            "WebFetch" => Host(Str("url")),
            "WebSearch" => Str("query"),
            "Task" or "Agent" => Str("subagent_type"),
            "Skill" or "SlashCommand" => Str("skill") ?? Str("command"),
            _ => null,
        };
        return Truncate(raw, 24);
    }

    // the file, not the path: a pill has room for "Fx.cs" and none at all for the repo it lives in
    private static string? Leaf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var s = path.Replace('\\', '/').TrimEnd('/');
        var i = s.LastIndexOf('/');
        var leaf = i >= 0 ? s.Substring(i + 1) : s;
        return leaf.Length > 0 ? leaf : null;
    }

    // the program a shell line runs, which survives env prefixes ("VAR=x cmd"), paths and quoting. Anything
    // with a pipe or a chain is more than one program, so it names none of them.
    private static string? Program_(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var line = command.Trim();
        if (line.IndexOfAny(new[] { '|', ';', '&' }) >= 0) return null;

        // a quoted program keeps its spaces. Splitting on whitespace first turned
        // "C:\Program Files\nodejs\npm.cmd" install into the word `"C:\Program`, whose leaf is "Program" -
        // not a program, and on the pill it would have read as one.
        if (line[0] is '"' or '\'')
        {
            var end = line.IndexOf(line[0], 1);
            return end > 1 ? Clean(line.Substring(1, end - 1)) : null;
        }
        foreach (var word in line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (word.Contains('=')) continue;                       // an env prefix, not the program
            if (Clean(word.Trim('"', '\'', '(')) is { } name) return name;
        }
        return null;

        static string? Clean(string word)
        {
            var leaf = Leaf(word);
            if (string.IsNullOrEmpty(leaf)) return null;
            if (leaf.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) leaf = leaf[..^4];
            return leaf.Length is > 0 and <= 14 ? leaf : null;
        }
    }

    private static string? Host(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        try { return new Uri(url).Host is { Length: > 0 } h ? h : null; } catch { return null; }
    }

    private static string? Truncate(string? s, int max)
        => string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s[..max] + "…");

    private static void UpdateContext(JsonObject status, string? transcriptPath)
    {
        try
        {
            if (string.IsNullOrEmpty(transcriptPath) || !File.Exists(transcriptPath)) return;
            var lines = File.ReadAllLines(transcriptPath);

            var started = DateTimeOffset.MinValue;
            if (status["startedAt"] is JsonNode sn)
                DateTimeOffset.TryParse(sn.GetValue<string>(), null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out started);

            long latest = 0, turn = 0;
            string? model = null;
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;
                JsonNode? node;
                try { node = JsonNode.Parse(lines[i]); } catch { continue; }
                var usage = node?["message"]?["usage"] ?? node?["usage"];
                if (usage == null) continue;

                long ctx = Get(usage, "input_tokens") + Get(usage, "cache_read_input_tokens")
                    + Get(usage, "cache_creation_input_tokens");
                if (latest == 0 && ctx > 0)
                {
                    latest = ctx;
                    model = (node?["message"]?["model"] ?? node?["model"])?.GetValue<string>();
                }

                // this turn's real consumption: new input + cache writes + output for every API call
                // since the prompt started (cache reads excluded — that's the old context re-read)
                if (started == DateTimeOffset.MinValue) { if (latest > 0) break; continue; }
                var tsNode = node?["timestamp"]?.GetValue<string>();
                if (!DateTimeOffset.TryParse(tsNode, null,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var ts)) continue;
                if (ts < started) { if (latest > 0) break; continue; }
                turn += Get(usage, "input_tokens") + Get(usage, "cache_creation_input_tokens")
                    + Get(usage, "output_tokens");
            }
            if (latest <= 0) return;

            var session = status["session"] as JsonObject ?? new JsonObject();
            session["contextUsed"] = latest;
            session["contextMax"] = ContextWindow(model);
            session["promptTokens"] = turn;
            status["session"] = session;
        }
        catch
        {
        }
    }

    private static long Get(JsonNode usage, string key)
    {
        try { return usage[key]?.GetValue<long>() ?? 0; }
        catch { return 0; }
    }

    // context window per model family (Opus/Fable/Sonnet take 1M; Haiku 200K)
    private static long ContextWindow(string? model)
    {
        var m = (model ?? "").ToLowerInvariant();
        if (m.Contains("haiku")) return 200_000;
        if (m.Contains("opus") || m.Contains("fable") || m.Contains("sonnet")) return 1_000_000;
        return 200_000;
    }

    private static void RecordProcess(JsonObject status, bool codex = false)
    {
        var map = ProcessMap();
        uint start = (uint)Environment.ProcessId;

        uint agent = Ancestor(map, start, codex
            ? n => n is "codex.exe" or "codex-code-mode-host.exe" or "chatgpt.exe"
            : n => n.Contains("claude") || n == "node.exe");
        if (agent != 0) status["pid"] = (int)agent;

        uint term = Ancestor(map, start, IsTerminal);
        if (term != 0) status["consolePid"] = (int)term;
    }

    private enum CodexSurface { Cli, Desktop }

    // ponytail: CLI sessions always run under a terminal; the desktop app's engine doesn't
    private static bool IsClaudeApp()
    {
        var o = Environment.GetEnvironmentVariable("HALO_CLAUDE_SURFACE");
        if (!string.IsNullOrEmpty(o)) return o.Equals("app", StringComparison.OrdinalIgnoreCase);
        return Ancestor(ProcessMap(), (uint)Environment.ProcessId, IsTerminal) == 0;
    }

    private static CodexSurface DetectCodexSurface()
    {
        var overrideSurface = Environment.GetEnvironmentVariable("HALO_CODEX_SURFACE");
        if (string.Equals(overrideSurface, "desktop", StringComparison.OrdinalIgnoreCase))
            return CodexSurface.Desktop;
        if (string.Equals(overrideSurface, "cli", StringComparison.OrdinalIgnoreCase))
            return CodexSurface.Cli;

        var map = ProcessMap();
        uint start = (uint)Environment.ProcessId;
        if (Ancestor(map, start, n => n is "chatgpt.exe" or "codex-code-mode-host.exe") != 0)
            return CodexSurface.Desktop;
        if (Ancestor(map, start, IsTerminal) != 0)
            return CodexSurface.Cli;
        return CodexSurface.Cli;
    }

    private static bool IsTerminal(string name) => name is
        "windowsterminal.exe" or "wt.exe" or "conhost.exe" or "openconsole.exe" or
        "powershell.exe" or "pwsh.exe" or "cmd.exe" or "bash.exe" or "wsl.exe" or
        "alacritty.exe" or "wezterm-gui.exe" or "code.exe";

    private static uint Ancestor(Dictionary<uint, (uint parent, string name)> map, uint start, Func<string, bool> match)
    {
        uint cur = start;
        for (int i = 0; i < 16 && cur != 0 && map.TryGetValue(cur, out var e); i++)
        {
            if (match(e.name.ToLowerInvariant())) return cur == start ? e.parent : cur;
            cur = e.parent;
        }
        return 0;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW")]
    private static extern bool Process32First(IntPtr snap, ref PROCESSENTRY32 pe);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32NextW")]
    private static extern bool Process32Next(IntPtr snap, ref PROCESSENTRY32 pe);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);

    private static Dictionary<uint, (uint parent, string name)> ProcessMap()
    {
        var map = new Dictionary<uint, (uint, string)>();
        var snap = CreateToolhelp32Snapshot(0x2, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return map;
        try
        {
            var pe = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (Process32First(snap, ref pe))
                do { map[pe.th32ProcessID] = (pe.th32ParentProcessID, pe.szExeFile); }
                while (Process32Next(snap, ref pe));
        }
        finally { CloseHandle(snap); }
        return map;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AttachConsole(uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool FreeConsole();
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteConsoleInput(IntPtr h, INPUT_RECORD[] buffer, uint length, out uint written);

    [StructLayout(LayoutKind.Sequential)]
    private struct KEY_EVENT_RECORD
    {
        public int bKeyDown;
        public ushort wRepeatCount;
        public ushort wVirtualKeyCode;
        public ushort wVirtualScanCode;
        public ushort UnicodeChar;
        public uint dwControlKeyState;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT_RECORD
    {
        public ushort EventType;
        public ushort _pad;
        public KEY_EVENT_RECORD Key;
    }

    // Inject Esc into the Claude Code process's console — cancels the running turn WITHOUT
    // closing it (Ctrl+C would signal the whole console group and kill the terminal).
    private static void Cancel(int pid)
    {
        FreeConsole();
        if (!AttachConsole((uint)pid)) return;
        try
        {
            const uint GENERIC_RW = 0x80000000 | 0x40000000, SHARE_RW = 1 | 2, OPEN_EXISTING = 3;
            IntPtr hIn = CreateFile("CONIN$", GENERIC_RW, SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (hIn == IntPtr.Zero || hIn == new IntPtr(-1)) return;
            var recs = new[]
            {
                new INPUT_RECORD { EventType = 1, Key = new KEY_EVENT_RECORD { bKeyDown = 1, wRepeatCount = 1, wVirtualKeyCode = 0x1B, wVirtualScanCode = 0x01, UnicodeChar = 0x1B } },
                new INPUT_RECORD { EventType = 1, Key = new KEY_EVENT_RECORD { bKeyDown = 0, wRepeatCount = 1, wVirtualKeyCode = 0x1B, wVirtualScanCode = 0x01, UnicodeChar = 0x1B } },
            };
            WriteConsoleInput(hIn, recs, (uint)recs.Length, out _);
            CloseHandle(hIn);
        }
        finally { FreeConsole(); }
    }
}
