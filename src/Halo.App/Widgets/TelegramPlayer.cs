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
    // the strip's own title text ("Katy Perry - Legendary Lovers"). the strip describes ONE item, and
    // it is not always the one smtc is talking about: a video played over a paused song leaves the
    // song's strip standing, and injecting its 3:10 under the video's title is a lie the widget must
    // be able to refuse. null until read; consumers treat null as "no match".
    public static volatile string? Title;

    // A video is a DIFFERENT surface from the music strip: its own top-level window, `Ui::MediaSlider`
    // rather than `Ui::FilledSlider`, and two labels - elapsed "00:00" and REMAINING "-00:19". That
    // second label is worth the whole feature: elapsed + remaining is the exact duration, so video needs
    // none of the elapsed-vs-total guessing Infer does for music. Playing a video pauses the music, so
    // when this window exists it is what the pill is looking at, and it wins.
    public static volatile bool VideoSource;

    // why the video surface was not claimed on the last lap; --probe-tg prints it. Video is read from a
    // window that only exists while one is playing, so a failure here cannot be reproduced on demand -
    // the breadcrumb has to be left behind at the moment it happens.
    public static volatile string? VideoDebug;

    private static readonly object _lock = new();
    private static TimeSpan _pos;
    private static TimeSpan? _dur;
    private static long _wanted;
    private static Thread? _thread;

    public static (TimeSpan pos, TimeSpan? dur) Read() { lock (_lock) return (_pos, _dur); }


    // Whether a posted click actually seeks telegram is not knowable in advance - qt is free to ignore a
    // click it did not get through the hardware queue. So it is not assumed: every seek records where it
    // aimed, and the next strip samples say whether the slider went there. If it did not, this latches
    // false and the media widget takes the scrub handle away, because a control the app cannot honour
    // must not stay on screen doing nothing.
    public static volatile bool Seekable = true;
    private static double _aimed = -1;
    private static long _aimedAt;

    private static void JudgeSeek(double f)
    {
        if (_aimed < 0) return;
        long age = Environment.TickCount64 - _aimedAt;
        if (age < 1200) return;   // the click needs a strip lap to show up
        // playback keeps moving while we wait, so this is a "did it jump roughly there", not equality
        if (Math.Abs(f - _aimed) <= 0.08) { Seekable = true; _aimed = -1; }
        else if (age > 6000) { Debug = $"seek ignored (aimed {_aimed:F2}, strip at {f:F2})"; Seekable = false; _aimed = -1; }
    }

    // track changed - the settled duration belongs to the OLD track and must not gate the new one's
    public static void Reset()
    {
        lock (_lock) { _pos = TimeSpan.Zero; _dur = null; Version++; }
    }

    // Seek by clicking telegram's own slider, since there is no api to ask. UIA ValuePattern.SetValue
    // is accepted and then ignored by qt's slider (measured live: value unchanged across the call), and
    // the strip has no InvokePattern that seeks - a posted click on the slider is what is left. It is
    // posted, not synthesized: no real cursor moves and telegram takes no focus, the same trick
    // KeyInject uses for vlc.
    //
    // Two transforms, because the important case is the minimized one - the pill exists so telegram can
    // stay out of the way, and seeking only while its window is up would be close to useless. Qt keeps
    // its layout while minimized and still honours a posted click: verified live, a click at the
    // uia-rect-relative point moved a MINIMIZED telegram from 11% to 61%. ScreenToClient is the exact
    // transform whenever the window is really on screen (borders, caption and dpi all shift the client
    // origin), but for a minimized window it answers from a ~-32000 origin and is unusable, so there the
    // point is taken relative to the window element's own uia rect, which stays in restored space.
    public static bool SeekTo(double frac)
    {
        try
        {
            frac = Math.Clamp(frac, 0.0, 1.0);
            var auto = Uia.Create();
            if (auto == null) { Debug = "seek: no uia"; return false; }

            // whichever surface is actually playing owns the click - the video window if one is up, the
            // music strip otherwise
            IntPtr hwnd; double[] sr;
            if (SampleVideo(auto) is { } vid)
            {
                hwnd = vid.hwnd; sr = Uia.PropRect(vid.slider);
            }
            else
            {
                if (FindStrip(auto) is not { } found) { Debug = "seek: no strip"; return false; }
                if (auto.CreatePropertyCondition(Uia.ClassNameProp, "class Ui::FilledSlider", out var cond) != 0)
                    return false;
                if (found.strip.FindFirst(Uia.TreeScopeDescendants, cond, out var slider) != 0 || slider == null)
                { Debug = "seek: no slider"; return false; }
                hwnd = found.hwnd; sr = Uia.PropRect(slider);
            }
            if (!PostClick(auto, hwnd, sr, frac, hover: false)) return false;
            Debug = null;
            _aimed = frac; _aimedAt = Environment.TickCount64;   // the strip decides whether it landed
            return true;
        }
        catch (Exception e) { Debug = "seek: " + e.Message; return false; }
    }

    // Does the strip describe the same item smtc is reporting? Telegram leaves the music strip standing
    // while a video plays, so its numbers can belong to a completely different item - that is how a
    // paused song's 3:45 ended up printed under a video. Compared loosely because the strip writes one
    // "performer - title" line while smtc splits the two, and an untagged file shows as its filename on
    // both sides.
    internal static bool TitleMatches(string? strip, string? title)
    {
        string s = Norm(strip), t = Norm(title);
        if (s.Length < 3 || t.Length < 3) return false;   // nothing to compare = no permission to adopt
        return s.Contains(t, StringComparison.Ordinal) || t.Contains(s, StringComparison.Ordinal);
    }

    private static string Norm(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var sb = new System.Text.StringBuilder(s.Length);
        bool space = false;
        foreach (char c in s.Trim().ToLowerInvariant())
        {
            // 2013/2014/2012 = en, em and figure dash. written as code points because this file is
            // ascii-only and an editor that resolves \u escapes puts the raw character back.
            int u = c;
            char n = u is 0x2013 or 0x2014 or 0x2012 or '_' ? '-' : c;
            if (char.IsWhiteSpace(n)) { space = sb.Length > 0; continue; }
            if (space) { sb.Append(' '); space = false; }
            sb.Append(n);
        }
        return sb.ToString();
    }

    // The video window exists only while one is playing, so a failure cannot be reproduced after the fact,
    // and it is the PILL's reader that matters rather than a probe's - a probe pokes itself, while the
    // pill's reader only runs while the media widget is asking for it. Same loose-file convention as
    // notif-debug.txt. Deduped, because at 1Hz an unchanged state would otherwise fill the file.
    private static string? _lastLogged;
    internal static void Log(string line)
    {
        try
        {
            if (line == _lastLogged) return;
            _lastLogged = line;
            string dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Halo");
            string path = System.IO.Path.Combine(dir, "tg-debug.txt");
            System.IO.Directory.CreateDirectory(dir);
            var f = new System.IO.FileInfo(path);
            if (f.Exists && f.Length > 200_000) f.Delete();
            System.IO.File.AppendAllText(path,
                DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + line + Environment.NewLine);
        }
        catch { }
    }

    // Telegram's own speed control, `Media::Player::SpeedButton`, whose NAME carries the current value
    // ("Playback speed: 1x"). That is the whole reason speed is offerable at all: SMTC's playback rate was
    // rejected because nothing honours it, but this can be both READ and verified after a change, so the
    // pill never shows a speed it has not confirmed. Null when telegram is showing no speed control.
    public static volatile string? Speed;

    private const string SpeedClass = "class Media::Player::SpeedButton";

    // "Playback speed: 1x" -> "1x". The label is localised around the value, so the value is taken from
    // after the last colon rather than by matching the sentence.
    internal static string? ParseSpeed(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        int i = name.LastIndexOf(':');
        if (i < 0 || i + 1 >= name.Length) return null;
        string s = name[(i + 1)..].Trim();
        return s.Length > 1 && (s.EndsWith('x') || s.EndsWith('X')) ? s.ToLowerInvariant() : null;
    }

    // Posts a click at fracX across an element's rect. `hover` first for BUTTONS: qt only accepts a press
    // on a widget it already considers hovered, and measured live a bare down/up on the speed button did
    // nothing three times running, while the same sequence with two mousemoves and ~200ms in front of it
    // toggled it on the first try. The slider does not need it, which is why it is a parameter.
    private static bool PostClick(IUIAutomation auto, IntPtr hwnd, double[] r, double fracX, bool hover)
    {
        if (r.Length < 4 || r[2] <= 1) return false;
        int sx = (int)(r[0] + r[2] * fracX), sy = (int)(r[1] + r[3] / 2);
        int cx, cy, limX, limY;
        if (!Win32.IsIconic(hwnd))
        {
            var pt = new Win32.POINT { X = sx, Y = sy };
            if (!Win32.ScreenToClient(hwnd, ref pt) || !Win32.GetClientRect(hwnd, out var cr))
            { Debug = "click: transform failed"; return false; }
            (cx, cy, limX, limY) = (pt.X, pt.Y, cr.right, cr.bottom);
        }
        else
        {
            if (auto.ElementFromHandle(hwnd, out var root) != 0 || root == null) return false;
            var wr = Uia.PropRect(root);
            if (wr.Length < 4 || wr[2] <= 1) { Debug = "click: window rect"; return false; }
            (cx, cy, limX, limY) = (sx - (int)wr[0], sy - (int)wr[1], (int)wr[2], (int)wr[3]);
        }
        if (cx < 0 || cy < 0 || cx >= limX || cy >= limY)
        { Debug = $"click: pt {cx},{cy} outside {limX}x{limY}"; return false; }

        IntPtr lp = (IntPtr)((cy << 16) | (cx & 0xFFFF));
        if (hover)
        {
            Win32.PostMessage(hwnd, 0x0200, IntPtr.Zero, lp);   // WM_MOUSEMOVE
            Thread.Sleep(200);
            Win32.PostMessage(hwnd, 0x0200, IntPtr.Zero, lp);
            Thread.Sleep(200);
        }
        Win32.PostMessage(hwnd, 0x0201, (IntPtr)1, lp);         // WM_LBUTTONDOWN, MK_LBUTTON
        Thread.Sleep(hover ? 90 : 40);
        Win32.PostMessage(hwnd, 0x0202, IntPtr.Zero, lp);       // WM_LBUTTONUP
        return true;
    }

    // Toggle telegram's playback speed. A click is a TOGGLE between 1x and whatever alternate its own menu
    // last selected (measured: 1x -> 0.5x -> 1x), so this offers exactly what telegram offers on a click
    // rather than inventing a speed ladder the app would not follow.
    public static bool ToggleSpeed()
    {
        try
        {
            var auto = Uia.Create();
            if (auto == null) return false;
            if (FindStrip(auto) is not { } found) { Debug = "speed: no strip"; return false; }
            if (auto.CreatePropertyCondition(Uia.ClassNameProp, SpeedClass, out var cond) != 0) return false;
            if (found.strip.FindFirst(Uia.TreeScopeDescendants, cond, out var btn) != 0 || btn == null)
            { Debug = "speed: no button"; return false; }
            string? before = ParseSpeed(Uia.PropString(btn, Uia.NameProp));
            if (!PostClick(auto, found.hwnd, Uia.PropRect(btn), 0.5, hover: true)) return false;
            // Exactly ONE click, then wait for telegram to agree. Clicking again on no answer would be a
            // second TOGGLE, and two toggles land back where they started - a retry loop here can leave the
            // speed somewhere nobody asked for. So the click is fired once and only the confirmation is
            // retried, by re-reading the button's own label.
            for (int i = 0; i < 10; i++)
            {
                Thread.Sleep(200);
                string? now = ParseSpeed(Uia.PropString(btn, Uia.NameProp));
                if (now != null && now != before) { Speed = now; return true; }
            }
            Speed = before;
            Debug = $"speed: telegram kept {before ?? "-"} after the click";
            return false;
        }
        catch (Exception e) { Debug = "speed: " + e.Message; return false; }
    }

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

    // How a fresh duration candidate meets the settled one.
    //
    // Hysteresis, because the estimate wobbles a couple of seconds - the slider rounds to whole percents -
    // and re-settling every lap made the bar's END twitch once a second. Infer suppresses that inside its
    // elapsed branch only, so it has to be done again here to cover its other two branches, which hand
    // back the strip's raw text.
    //
    // And a settled duration is only ever a heuristic: "constant text under a moving slider means the text
    // is the total" misfires when two samples land inside the same wall-clock second, which is what
    // resuming music after a video does. Treating that guess as final left the bar pinned at 100% for the
    // rest of the track. So if the strip walks PAST the duration, the guess was wrong - drop it and let
    // the next samples settle a new one.
    internal static TimeSpan? Settle(TimeSpan? settled, TimeSpan? candidate, TimeSpan pos)
    {
        var dur = settled;
        if (candidate is { } cand && (dur is not { } s || Math.Abs((cand - s).TotalSeconds) > 3))
            dur = cand;
        return dur is { } known && pos > known + TimeSpan.FromSeconds(1) ? null : dur;
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
                Log("reader parked - nothing has poked it for 15s");
                continue;
            }
            try
            {
                auto ??= Uia.Create();
                if (auto == null) { Debug = "CUIAutomation failed"; Live = false; continue; }
                // Video first: playing one pauses the music, but telegram leaves the music strip standing,
                // so asking the strip first would answer about the paused song. Its numbers are exact, so
                // none of the Infer/Settle machinery below applies to them.
                if (SampleVideo(auto) is { } vid)
                {
                    lock (_lock)
                    {
                        if (_pos != vid.pos || _dur != vid.dur) Version++;
                        _pos = vid.pos; _dur = vid.dur;
                    }
                    VideoSource = true; Title = null; Debug = null;
                    Live = true; LastLiveAt = Environment.TickCount64;
                    prevFrac = -1; prevText = TimeSpan.MinValue;   // music inference restarts clean after
                    Log($"VIDEO claimed dur={vid.dur}");
                    continue;
                }
                if (VideoSource) Log("video released");
                VideoSource = false;
                if (VideoDebug is { } vd) Log("no video: " + vd);

                strip ??= FindStrip(auto)?.strip;
                if (strip == null) { Debug = "player strip not found"; Live = false; Title = null; continue; }

                double? frac = null; TimeSpan? text = null; string? label = null;
                if (!Sample(auto, strip, ref frac, ref text, ref label))
                {
                    // window went away or the strip closed - re-find next lap
                    Debug = "strip went stale"; strip = null; Live = false; Title = null;
                    continue;
                }
                Title = label;
                Speed = SampleSpeed(auto, strip);
                if (frac is not { } f || text is not { } t)
                { Debug = $"frac={frac?.ToString() ?? "-"} text={text?.ToString() ?? "-"}"; Live = false; continue; }
                Debug = null;
                JudgeSeek(f);

                bool live;
                lock (_lock)
                {
                    var (pos, durCand) = prevFrac < 0 ? Infer(f, t, f, t, _dur) : Infer(f, t, prevFrac, prevText, _dur);
                    bool changed = pos != _pos;
                    _pos = pos;
                    var next = Settle(_dur, durCand, pos);
                    if (next != _dur) { _dur = next; changed = true; }
                    if (changed) Version++;
                    live = _dur is not null;
                }
                prevFrac = f; prevText = t;
                Live = live;
                if (live) LastLiveAt = Environment.TickCount64;
                lock (_lock) Log($"music live={live} dur={(_dur?.ToString() ?? "-")} title={Title ?? "-"}");
            }
            catch (Exception e)
            {
                Debug = "com: " + e.Message;
                strip = null; Live = false;   // com failure = stale element; never let it escape the thread
            }
        }
    }

    // The video player's own window. Returns its seek slider plus an EXACT position and duration, because
    // the video controls label both ends: "00:00" elapsed and "-00:19" remaining, and the two add up to
    // the total. The narrow MediaSlider sitting beside the seek bar is the volume control - the seek bar
    // is the wide one (measured live: 363px vs 75px).
    // Telegram writes the video's remaining time with a REAL minus sign, not an ascii hyphen. Measured
    // live: the label came back as "-00:13" on screen but `StartsWith('-')` was false, so it fell through
    // to the elapsed branch, `left` stayed null and the video surface was never claimed - the whole
    // "video is not supported" report. 2212 is the minus sign; the dashes are here because a label that
    // close to one is not worth a second bug. Code points, because this file stays ascii.
    private static bool IsMinus(char c)
    {
        int u = c;
        return u is '-' or 0x2212 or 0x2013 or 0x2014 or 0x2012;
    }

    // The video controls' two labels -> an exact position and duration, because the second one is what is
    // LEFT: elapsed + remaining is the total, so video needs none of the elapsed-vs-total guessing Infer
    // does for music. Pure so the minus-sign trap above stays pinned by a test.
    // A remaining of zero means the video has finished - and its window STAYS behind afterwards, so that
    // is not a source to claim; the music telegram has already resumed takes back over.
    internal static (TimeSpan pos, TimeSpan dur)? VideoClock(System.Collections.Generic.IEnumerable<string> labels)
    {
        TimeSpan? elapsed = null, left = null;
        foreach (string name in labels)
        {
            if (name.Length > 1 && IsMinus(name[0])) left ??= ParseTime(name[1..]);
            else elapsed ??= ParseTime(name);
        }
        if (elapsed is not { } pos || left is not { } rem || rem <= TimeSpan.Zero) return null;
        var dur = pos + rem;
        return dur > TimeSpan.Zero ? (pos, dur) : null;
    }

    // windows already known to carry no media slider, re-checked only every 10s (see SampleVideo)
    private static readonly System.Collections.Generic.HashSet<IntPtr> _barren = new();
    private static long _barrenAt;

    private static (IntPtr hwnd, IUIAutomationElement slider, TimeSpan pos, TimeSpan dur)? SampleVideo(
        IUIAutomation auto)
    {
        // A video always arrives as a NEW window handle, and a new handle is never in the barren set, so
        // detection stays instant without re-walking anything. The periodic clear is only a hedge for a
        // window that grows controls in place; it is deliberately rare, since each rescan pays the main
        // window's full tree walk again.
        long now = Environment.TickCount64;
        if (now - _barrenAt > 60_000) { _barren.Clear(); _barrenAt = now; }
        if (auto.CreatePropertyCondition(Uia.ClassNameProp, "class Ui::MediaSlider", out var sliderCond) != 0)
            return null;
        if (auto.CreatePropertyCondition(Uia.ControlTypeProp, Uia.TextType, out var textCond) != 0)
            return null;

        string? why = null;
        foreach (IntPtr hwnd in TelegramWindows())
        {
            // A descendants search is a cross-process walk of the WHOLE window tree, and telegram's main
            // window is the thousands-of-element message list - the reason the strip is read through its
            // own scoped subtree and cached. Doing it here once a second starved the reader badly enough
            // that music stopped being detected at all. So a window that has already answered "no media
            // sliders" is not asked again for 10s; a window that has never been seen - which is exactly
            // what a video window is when one starts - is always asked immediately.
            if (_barren.Contains(hwnd)) continue;
            if (auto.ElementFromHandle(hwnd, out var root) != 0 || root == null) continue;
            if (root.FindAll(Uia.TreeScopeDescendants, sliderCond, out var arr) != 0 || arr == null) continue;
            if (arr.get_Length(out int n) != 0 || n == 0) { _barren.Add(hwnd); continue; }

            IUIAutomationElement? seek = null; double widest = 0;
            for (int i = 0; i < n; i++)
            {
                if (arr.GetElement(i, out var e) != 0 || e == null) continue;
                var r = Uia.PropRect(e);
                if (r.Length < 4 || r[2] <= widest) continue;
                widest = r[2]; seek = e;
            }
            if (seek == null) { why = $"{n} mediasliders, none with a rect"; continue; }

            if (root.FindAll(Uia.TreeScopeDescendants, textCond, out var texts) != 0 || texts == null) continue;
            if (texts.get_Length(out int tn) != 0) continue;
            var labels = new System.Collections.Generic.List<string>(tn);
            var seen = new System.Text.StringBuilder();
            for (int i = 0; i < tn; i++)
            {
                if (texts.GetElement(i, out var e) != 0 || e == null) continue;
                string name = Uia.PropString(e, Uia.NameProp);
                labels.Add(name);
                if (seen.Length < 120) seen.Append('[').Append(name).Append(']');
            }
            if (VideoClock(labels) is not { } clock)
            { why = $"{n} sliders, {tn} texts {seen} did not read as a video clock"; continue; }
            VideoDebug = null;
            return (hwnd, seek, clock.pos, clock.dur);
        }
        VideoDebug = why;
        return null;
    }

    // the player strip's own subtree: "class Media::Player::Widget" (read off the live tree).
    //
    // Every visible telegram window is tried, not just the first: telegram keeps other visible top-level
    // windows around (a 10x1460 sliver turned up mid-session), and picking the first one found made the
    // strip - and with it the whole timeline - vanish depending on what else telegram happened to have
    // open. The window that actually contains the strip is the one to talk to, and the seek needs its
    // handle too, so both come back together.
    private static (IntPtr hwnd, IUIAutomationElement strip)? FindStrip(IUIAutomation auto)
    {
        if (auto.CreatePropertyCondition(Uia.ClassNameProp, "class Media::Player::Widget", out var cond) != 0)
            return null;
        foreach (IntPtr hwnd in TelegramWindows())
        {
            if (auto.ElementFromHandle(hwnd, out var root) != 0 || root == null) continue;
            if (root.FindFirst(Uia.TreeScopeDescendants, cond, out var found) == 0 && found != null)
                return (hwnd, found);
        }
        return null;
    }

    // scoped to the strip's own subtree, so this costs one small search rather than a window walk
    private static string? SampleSpeed(IUIAutomation auto, IUIAutomationElement strip)
    {
        try
        {
            if (auto.CreatePropertyCondition(Uia.ClassNameProp, SpeedClass, out var cond) != 0) return null;
            if (strip.FindFirst(Uia.TreeScopeDescendants, cond, out var btn) != 0 || btn == null) return null;
            return ParseSpeed(Uia.PropString(btn, Uia.NameProp));
        }
        catch { return null; }
    }

    private static bool Sample(IUIAutomation auto, IUIAutomationElement strip,
        ref double? frac, ref TimeSpan? text, ref string? title)
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
        // the strip carries both a mm:ss and its label; whichever parses as a time is the clock, and the
        // first that does not is what the strip is playing
        for (int i = 0; i < n; i++)
        {
            if (texts.GetElement(i, out var e) != 0 || e == null) continue;
            string? name = Uia.PropString(e, Uia.NameProp);
            if (ParseTime(name) is { } t) { text ??= t; }
            else if (!string.IsNullOrWhiteSpace(name)) title ??= name;
        }
        return true;
    }

    private static IntPtr FindTelegramWindow()
    {
        var all = TelegramWindows();
        return all.Count > 0 ? all[0] : IntPtr.Zero;
    }

    private static System.Collections.Generic.List<IntPtr> TelegramWindows()
    {
        var found = new System.Collections.Generic.List<IntPtr>();
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
                    if (p.ProcessName.Equals("Telegram", StringComparison.OrdinalIgnoreCase))
                        found.Add(hwnd);
                    return true;
                }
                catch { return true; }
            }, IntPtr.Zero);
        }
        catch { }
        return found;
    }

    // dev probe (--probe-tg-tree). Everything in every visible telegram window that could plausibly BE a
    // timeline: any slider, and any text reading mm:ss. The point is to answer whether a playing video
    // exposes its position anywhere - the music strip is the only surface known to, and if a video has
    // none then the pill's only honest option for video is to draw no bar at all.
    internal static void DumpTree(System.IO.TextWriter w)
    {
        var auto = Uia.Create();
        if (auto == null) { w.WriteLine("CUIAutomation failed"); return; }
        var wins = TelegramWindows();
        w.WriteLine($"visible telegram windows: {wins.Count}");
        foreach (IntPtr hwnd in wins)
        {
            var buf = new char[256];
            int len = Win32.GetClassName(hwnd, buf, buf.Length);
            w.WriteLine($"== hwnd 0x{hwnd.ToInt64():X} class='{new string(buf, 0, Math.Max(0, len))}' iconic={Win32.IsIconic(hwnd)}");
            if (auto.ElementFromHandle(hwnd, out var root) != 0 || root == null) { w.WriteLine("   no uia root"); continue; }

            foreach (int type in new[] { Uia.SliderType, Uia.TextType })
            {
                if (auto.CreatePropertyCondition(Uia.ControlTypeProp, type, out var cond) != 0) continue;
                if (root.FindAll(Uia.TreeScopeDescendants, cond, out var arr) != 0 || arr == null) continue;
                if (arr.get_Length(out int n) != 0) continue;
                w.WriteLine($"   {(type == Uia.SliderType ? "sliders" : "texts")}: {n}");
                // the message list is thousands of texts; this is a probe, not a scan
                for (int i = 0; i < Math.Min(n, 600); i++)
                {
                    if (arr.GetElement(i, out var e) != 0 || e == null) continue;
                    string name = Uia.PropString(e, Uia.NameProp);
                    // a video's remaining time reads "-00:07", which ParseTime rejects - so the filter here
                    // is "looks like a clock", deliberately looser than what the reader accepts
                    if (type == Uia.TextType && !(name.Contains(':') && name.Length <= 12) && name.Length is 0 or > 60)
                        continue;
                    var r = Uia.PropRect(e);
                    string rect = r.Length >= 4 ? $"{(int)r[0]},{(int)r[1]} {(int)r[2]}x{(int)r[3]}" : "-";
                    w.WriteLine($"     [{i}] cls='{Uia.PropString(e, Uia.ClassNameProp)}' name='{name}' " +
                                $"value='{Uia.PatternValue(e) ?? "-"}' rect={rect}");
                }
            }
        }
    }
}
