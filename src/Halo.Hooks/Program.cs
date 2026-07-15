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
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "notch");
    private static readonly string StatusPath = Path.Combine(Dir, "status.json");

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0) return 0;
            var cmd = args[0];

            if (cmd == "cancel")
            {
                if (args.Length >= 2 && int.TryParse(args[1], out var pid))
                    Cancel(pid);
                return 0;
            }

            Directory.CreateDirectory(Dir);
            var input = ReadInput();
            var status = LoadOrNew();

            string? Field(string name) => input?[name]?.GetValue<string>();

            switch (cmd)
            {
                case "session-start":
                    status["sessionId"] = Field("session_id");
                    status["cwd"] = Field("cwd");
                    status["state"] = "idle";
                    RecordProcess(status);
                    break;
                case "prompt":
                    status["state"] = "working";
                    status["lastPrompt"] = Truncate(Field("prompt"), 120);
                    status["currentTool"] = null;
                    RecordProcess(status);
                    UpdateContext(status, Field("transcript_path"));
                    break;
                case "tool":
                    status["state"] = "working";
                    status["currentTool"] = Field("tool_name");
                    break;
                case "tool-done":
                    status["state"] = "working";
                    UpdateContext(status, Field("transcript_path"));
                    break;
                case "notify":
                    status["state"] = "waiting_input";
                    break;
                case "stop":
                    status["state"] = "idle";
                    status["currentTool"] = null;
                    UpdateContext(status, Field("transcript_path"));
                    break;
                case "session-end":
                    status["state"] = "idle";
                    status["currentTool"] = null;
                    break;
                default:
                    return 0;
            }

            status["updatedAt"] = DateTimeOffset.UtcNow.ToString("o");
            Save(status);
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
            var text = Console.In.ReadToEnd();
            if (string.IsNullOrWhiteSpace(text)) return null;
            return JsonNode.Parse(text) as JsonObject;
        }
        catch
        {
            return null;
        }
    }

    private static JsonObject LoadOrNew()
    {
        try
        {
            if (File.Exists(StatusPath))
            {
                var text = File.ReadAllText(StatusPath);
                if (JsonNode.Parse(text) is JsonObject o) return o;
            }
        }
        catch
        {
        }
        return new JsonObject();
    }

    private static void Save(JsonObject status)
    {
        var tmp = StatusPath + ".tmp";
        File.WriteAllText(tmp, status.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, StatusPath, overwrite: true);
    }

    private static string? Truncate(string? s, int max)
        => string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s[..max] + "…");

    private static void UpdateContext(JsonObject status, string? transcriptPath)
    {
        try
        {
            if (string.IsNullOrEmpty(transcriptPath) || !File.Exists(transcriptPath)) return;
            var lines = File.ReadAllLines(transcriptPath);
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;
                JsonNode? node;
                try { node = JsonNode.Parse(lines[i]); } catch { continue; }
                var usage = node?["message"]?["usage"] ?? node?["usage"];
                if (usage == null) continue;

                long used = Get(usage, "input_tokens") + Get(usage, "cache_read_input_tokens")
                    + Get(usage, "cache_creation_input_tokens") + Get(usage, "output_tokens");
                if (used <= 0) continue;

                var session = status["session"] as JsonObject ?? new JsonObject();
                session["contextUsed"] = used;
                if (session["contextMax"] == null) session["contextMax"] = 200000;
                status["session"] = session;
                return;
            }
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

    private static void RecordProcess(JsonObject status)
    {
        var map = ProcessMap();
        uint start = (uint)Environment.ProcessId;

        uint claude = Ancestor(map, start, n => n.Contains("claude") || n == "node.exe");
        if (claude != 0) status["pid"] = (int)claude;

        uint term = Ancestor(map, start, IsTerminal);
        if (term != 0) status["consolePid"] = (int)term;
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

    private const uint CTRL_C_EVENT = 0;
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AttachConsole(uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool FreeConsole();
    [DllImport("kernel32.dll")] private static extern bool GenerateConsoleCtrlEvent(uint evt, uint pgid);
    [DllImport("kernel32.dll")] private static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);

    private static void Cancel(int pid)
    {
        FreeConsole();
        if (!AttachConsole((uint)pid)) return;
        SetConsoleCtrlHandler(IntPtr.Zero, true);
        GenerateConsoleCtrlEvent(CTRL_C_EVENT, 0);
        System.Threading.Thread.Sleep(150);
        FreeConsole();
        SetConsoleCtrlHandler(IntPtr.Zero, false);
    }
}
