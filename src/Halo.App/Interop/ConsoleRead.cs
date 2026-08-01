using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Halo.Interop;

// Reading the text a terminal is currently showing.
//
// Claude Code's compaction progress exists nowhere else. Verified against the shipped binary: the only
// compact_progress events it raises are compact_start / compact_end / hooks_start, nothing is appended to
// the transcript between them, and the counter the spinner renders ("N tokens") lives in a response_length
// accumulator in memory. So the number is real, it is live, and the terminal is the only place it is
// published.
//
// A GUI process has no console of its own, which is what makes this possible at all: AttachConsole binds
// us to the agent's, CONOUT$ then opens ITS active screen buffer, and FreeConsole lets go. Everything is
// best-effort - a terminal that is gone, a console we may not attach to, a buffer that will not open, all
// answer null rather than throwing, and the caller simply has no reading this time.
internal static class ConsoleRead
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sec,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleScreenBufferInfo(IntPtr h, out CONSOLE_SCREEN_BUFFER_INFO info);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ReadConsoleOutputCharacterW(IntPtr h, [Out] char[] buf, uint len,
        COORD at, out uint read);

    [StructLayout(LayoutKind.Sequential)]
    private struct COORD { public short X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SMALL_RECT { public short Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CONSOLE_SCREEN_BUFFER_INFO
    {
        public COORD Size, CursorPosition;
        public ushort Attributes;
        public SMALL_RECT Window;
        public COORD MaximumWindowSize;
    }

    private const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
    private static readonly IntPtr Invalid = new(-1);

    // The last `lines` rows of the process's screen buffer, oldest first, trailing blanks trimmed.
    // Null when the console cannot be reached at all, which is the normal answer for a session that has
    // just exited.
    internal static string[]? Tail(int pid, int lines = 6, int below = 0)
    {
        if (pid <= 0) return null;
        bool attached = false;
        IntPtr h = Invalid;
        try
        {
            // we must own no console while attaching; ours (if the dev hooks gave us one) comes back below
            FreeConsole();
            if (!AttachConsole((uint)pid)) return null;
            attached = true;
            // While attached we are in that console's control group, so the Ctrl+C the user presses to
            // interrupt the agent would be delivered here too - and the default handler ends the process.
            // Set once and left set: a layered-window tool has no use for Ctrl+C in any case.
            SetConsoleCtrlHandler(IntPtr.Zero, true);
            h = CreateFileW("CONOUT$", GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h == Invalid) return null;
            if (!GetConsoleScreenBufferInfo(h, out var info)) return null;

            // The cursor, not the buffer's height: a ConPTY buffer is allocated tall and mostly empty, so
            // the bottom rows are blank and the text being rendered ends where the cursor is.
            int width = info.Size.X;
            // `below` exists because a TUI puts the cursor in whatever field it wants typed into, which
            // for Claude's question box is halfway up it - reading only as far as the cursor showed the
            // question and hid every option under it.
            int bottom = Math.Min(info.CursorPosition.Y + 1 + below, info.Size.Y);
            int top = Math.Max(0, bottom - below - lines);
            if (width <= 0 || bottom <= top) return null;

            var rows = new string[bottom - top];
            var buf = new char[width];
            for (int y = top; y < bottom; y++)
            {
                if (!ReadConsoleOutputCharacterW(h, buf, (uint)width, new COORD { X = 0, Y = (short)y },
                        out uint read))
                    return null;
                rows[y - top] = new string(buf, 0, (int)Math.Min(read, (uint)width)).TrimEnd();
            }
            return rows;
        }
        catch { return null; }
        finally
        {
            try { if (h != Invalid) CloseHandle(h); } catch { }
            try { if (attached) FreeConsole(); } catch { }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT_RECORD
    {
        public ushort EventType;
        public int KeyDown;
        public ushort RepeatCount;
        public ushort VirtualKeyCode;
        public ushort VirtualScanCode;
        public ushort UnicodeChar;
        public uint ControlKeyState;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool WriteConsoleInputW(IntPtr h, INPUT_RECORD[] buf, uint len, out uint written);

    private const ushort KEY_EVENT = 1;

    // Put keystrokes into a terminal WITHOUT taking the foreground.
    //
    // This is the half that makes answering Claude's own question box from the pill possible. Focus was
    // the first attempt and it is a dead end from a background process - SetForegroundWindow returns true
    // and does nothing, and AttachThreadInput did not rescue it either (both tried, both measured, in the
    // typing work that came before this). The console's input buffer takes the keys directly: no focus,
    // no stolen click, and the terminal need not even be visible.
    // A named key rather than a character. Arrows carry no UnicodeChar at all, and conhost turns the
    // virtual key into the escape sequence a TUI is actually reading - which is why typing letters into
    // Claude's question box did nothing until the list had been walked down to its text field.
    internal const ushort VkDown = 0x28, VkUp = 0x26, VkEnter = 0x0D, VkTab = 0x09, VkEscape = 0x1B;

    internal static bool Press(int pid, ushort vk, int times = 1)
        => Send(pid, null, vk, times);

    internal static bool Type(int pid, string text)
        => Send(pid, text, 0, 1);

    private static bool Send(int pid, string? text, ushort vk, int times)
    {
        if (pid <= 0 || (string.IsNullOrEmpty(text) && vk == 0)) return false;
        bool attached = false;
        IntPtr h = Invalid;
        try
        {
            FreeConsole();
            if (!AttachConsole((uint)pid)) return false;
            attached = true;
            SetConsoleCtrlHandler(IntPtr.Zero, true);
            h = CreateFileW("CONIN$", GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h == Invalid) return false;

            // down and up per key. A TUI reading raw input sees the down; sending only the down leaves a
            // key stuck as far as anything tracking state is concerned.
            int count = text is null ? times : text.Length;
            var recs = new INPUT_RECORD[count * 2];
            for (int i = 0; i < count; i++)
            {
                recs[i * 2] = new INPUT_RECORD
                {
                    EventType = KEY_EVENT, KeyDown = 1, RepeatCount = 1,
                    UnicodeChar = text is null ? (ushort)0 : text[i],
                    VirtualKeyCode = vk,
                };
                recs[i * 2 + 1] = recs[i * 2];
                recs[i * 2 + 1].KeyDown = 0;
            }
            return WriteConsoleInputW(h, recs, (uint)recs.Length, out uint wrote) && wrote > 0;
        }
        catch { return false; }
        finally
        {
            try { if (h != Invalid) CloseHandle(h); } catch { }
            try { if (attached) FreeConsole(); } catch { }
        }
    }

    internal static string Dump(int pid, int lines = 12, int below = 0)
    {
        var rows = Tail(pid, lines, below);
        return rows is null ? "(no console)" : string.Join("\n", rows);
    }

    internal static string Describe(int pid, int lines = 14, int below = 0)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"pid {pid}");
        sb.AppendLine(Dump(pid, lines, below));
        return sb.ToString();
    }
}
