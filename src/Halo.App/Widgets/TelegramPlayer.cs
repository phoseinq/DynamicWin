using System;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;
using Halo.Interop;

namespace Halo.Widgets;

// Telegram's media timeline, read out of Telegram's own UI - because SMTC gets nothing (end=pos=0 for
// the whole track, measured live) and tdesktop has no local api. The player strip's Qt slider answers
// UIA ValuePattern with "84%" and the strip carries one mm:ss text; between the two, position AND
// duration are real numbers from the app that owns them, not inventions.
//
// The strip shows either elapsed or total depending on telegram's mood, and nothing labels which. So
// Infer() watches two consecutive samples: a text that advances is elapsed (duration = elapsed/frac),
// a text that stands still while the slider moves is the total. Pure, pinned by TelegramPlayerTests.
//
// Polling runs on its own MTA thread at 1Hz, and only while someone keeps Poke()-ing (the media widget
// does, while a telegram session with an empty timeline is on screen). Cross-process UIA is not cheap;
// the search is scoped to the player strip's own subtree ("class Media::Player::Widget"), not the
// message list next to it, which is thousands of elements.
internal static class TelegramPlayer
{
    public static volatile bool Live;
    public static long LastLiveAt;         // TickCount64 of the last good sample
    public static int Version;
    public static volatile string? Debug;  // why the last lap produced nothing; --probe-tg prints it

    private static readonly object _lock = new();
    private static TimeSpan _pos;
    private static TimeSpan? _dur;
    private static long _wanted;
    private static Thread? _thread;

    public static (TimeSpan pos, TimeSpan? dur) Read() { lock (_lock) return (_pos, _dur); }

    public static void Poke()
    {
        Interlocked.Exchange(ref _wanted, Environment.TickCount64);
        if (_thread != null) return;
        lock (_lock)
        {
            if (_thread != null) return;
            _thread = new Thread(Loop) { IsBackground = true, Name = "tg-uia" };
            _thread.Start();
        }
    }

    // one step of "what do the strip's numbers mean". prevText/prevFrac are the previous sample,
    // prevDur whatever duration was already settled. see the header for the elapsed-vs-total logic.
    internal static (TimeSpan pos, TimeSpan? dur) Infer(double frac, TimeSpan text,
        double prevFrac, TimeSpan prevText, TimeSpan? prevDur)
    {
        frac = Math.Clamp(frac, 0.0, 1.0);
        // an empty slider under a nonzero time needs no motion to read: elapsed at <=1% cannot be
        // tens of seconds, so the text is the total. (a >16min file's opening seconds could fool
        // this - and the first advancing sample corrects it through the elapsed branch below.)
        if (frac <= 0.01 && text > TimeSpan.FromSeconds(10))
            return (TimeSpan.Zero, text);
        if (text > prevText && frac >= 0.02)
        {
            var est = TimeSpan.FromSeconds(Math.Round(text.TotalSeconds / frac));
            // keep a settled duration unless the fresh estimate really disagrees - the slider's "84%"
            // is rounded to a whole percent, so the estimate jitters a couple of seconds either way
            var dur = prevDur is { } known && Math.Abs((known - est).TotalSeconds) <= 3 ? known : est;
            return (text, dur);
        }
        if (text > prevText)
            return (text, prevDur);
        if (text == prevText && frac > prevFrac + 0.001 && text > TimeSpan.Zero)
            return (TimeSpan.FromSeconds(Math.Round(frac * text.TotalSeconds)), text);
        // nothing moved (paused, or a lone first sample)
        if (prevDur is { } d)
            return (text <= d ? text : TimeSpan.FromSeconds(Math.Round(frac * d.TotalSeconds)), d);
        return (text, null);
    }

    internal static double? ParsePercent(string? s)
        => s != null && s.EndsWith('%')
           && double.TryParse(s[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var p)
            ? Math.Clamp(p / 100.0, 0.0, 1.0) : null;

    private static readonly Regex TimeRx = new(@"^\d{1,2}:\d{2}(:\d{2})?$", RegexOptions.Compiled);

    internal static TimeSpan? ParseTime(string? s)
    {
        if (s == null || !TimeRx.IsMatch(s)) return null;
        var parts = s.Split(':');
        try
        {
            return parts.Length == 3
                ? new TimeSpan(int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]))
                : new TimeSpan(0, int.Parse(parts[0]), int.Parse(parts[1]));
        }
        catch { return null; }
    }

    private static void Loop()
    {
        IUIAutomation? auto = null;
        IUIAutomationElement? strip = null;
        double prevFrac = -1; TimeSpan prevText = TimeSpan.MinValue;
        while (true)
        {
            Thread.Sleep(1000);
            if (Environment.TickCount64 - Interlocked.Read(ref _wanted) > 15_000)
            {
                Live = false; strip = null;   // nobody is looking; drop the cross-proc refs too
                continue;
            }
            try
            {
                auto ??= Uia.Create();
                if (auto == null) { Debug = "CUIAutomation failed"; Live = false; continue; }
                strip ??= FindStrip(auto);
                if (strip == null) { Debug = "player strip not found"; Live = false; continue; }

                double? frac = null; TimeSpan? text = null;
                if (!Sample(auto, strip, ref frac, ref text))
                {
                    // window went away or the strip closed - re-find next lap
                    Debug = "strip went stale"; strip = null; Live = false;
                    continue;
                }
                if (frac is not { } f || text is not { } t)
                { Debug = $"frac={frac?.ToString() ?? "-"} text={text?.ToString() ?? "-"}"; Live = false; continue; }
                Debug = null;

                TimeSpan pos; TimeSpan? dur;
                lock (_lock)
                {
                    (pos, dur) = prevFrac < 0 ? Infer(f, t, f, t, _dur) : Infer(f, t, prevFrac, prevText, _dur);
                    bool changed = pos != _pos || dur != _dur;
                    _pos = pos; _dur = dur;
                    if (changed) Version++;
                }
                prevFrac = f; prevText = t;
                Live = dur is not null;
                if (Live) LastLiveAt = Environment.TickCount64;
            }
            catch (Exception e)
            {
                Debug = "com: " + e.Message;
                strip = null; Live = false;   // com failure = stale element; never let it escape the thread
            }
        }
    }

    // the player strip's own subtree: "class Media::Player::Widget" (read off the live tree)
    private static IUIAutomationElement? FindStrip(IUIAutomation auto)
    {
        IntPtr hwnd = FindTelegramWindow();
        if (hwnd == IntPtr.Zero) return null;
        if (auto.ElementFromHandle(hwnd, out var root) != 0 || root == null) return null;
        if (auto.CreatePropertyCondition(Uia.ClassNameProp, "class Media::Player::Widget", out var cond) != 0)
            return null;
        return root.FindFirst(Uia.TreeScopeDescendants, cond, out var found) == 0 ? found : null;
    }

    private static bool Sample(IUIAutomation auto, IUIAutomationElement strip,
        ref double? frac, ref TimeSpan? text)
    {
        if (auto.CreatePropertyCondition(Uia.ClassNameProp, "class Ui::FilledSlider", out var sliderCond) != 0)
            return false;
        if (strip.FindFirst(Uia.TreeScopeDescendants, sliderCond, out var slider) != 0 || slider == null)
            return false;
        frac = ParsePercent(Uia.PatternValue(slider));

        if (auto.CreatePropertyCondition(Uia.ControlTypeProp, Uia.TextType, out var textCond) != 0)
            return false;
        if (strip.FindAll(Uia.TreeScopeDescendants, textCond, out var texts) != 0 || texts == null)
            return false;
        if (texts.get_Length(out int n) != 0) return false;
        for (int i = 0; i < n; i++)
        {
            if (texts.GetElement(i, out var e) != 0 || e == null) continue;
            if (ParseTime(Uia.PropString(e, Uia.NameProp)) is { } t) { text = t; break; }
        }
        return true;
    }

    private static IntPtr FindTelegramWindow()
    {
        IntPtr found = IntPtr.Zero;
        try
        {
            Win32.EnumWindows((hwnd, _) =>
            {
                try
                {
                    if (!Win32.IsWindowVisible(hwnd)) return true;
                    Win32.GetWindowThreadProcessId(hwnd, out uint pid);
                    if (pid == 0) return true;
                    using var p = System.Diagnostics.Process.GetProcessById((int)pid);
                    if (!p.ProcessName.Equals("Telegram", StringComparison.OrdinalIgnoreCase)) return true;
                    found = hwnd;
                    return false;
                }
                catch { return true; }
            }, IntPtr.Zero);
        }
        catch { }
        return found;
    }
}
