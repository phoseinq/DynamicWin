using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Threading.Tasks;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace Halo.Widgets;

// Now Playing via system media transport controls (Spotify, browsers, any player).
// All WinRT callbacks run off the UI thread: they only touch the locked snapshot + bump
// _version. GDI (art decode + draw) happens on the UI thread in DrawContent.
internal sealed class MediaWidget : IWidget
{
    private static readonly Color White = Color.FromArgb(238, 255, 255, 255);
    private static readonly Color Dim = Color.FromArgb(150, 255, 255, 255);
    private static readonly Color Track = Color.FromArgb(46, 255, 255, 255);

    private readonly object _lock = new();
    private readonly MediaSessions _sessions;
    private readonly int _slot;
    private GlobalSystemMediaTransportControlsSession? _session;

    // snapshot (guarded by _lock)
    private string? _title, _artist, _trackKey, _appId;
    private bool _playing, _isVideo; // video → transport shows seek ±10s instead of prev/next
    private double _rate = 1.0;      // current playback rate (read from SMTC, driven by the Speed chip)
    private bool _thumbWide;         // 16:9-ish thumbnail = a video frame (album art is square)
    // playback status: only Playing/Paused count as a live player. Browsers keep Stopped/Closed sessions
    // around with stale metadata after a video ends — those must NOT hold the pill open (blank black pill).
    private GlobalSystemMediaTransportControlsSessionPlaybackStatus _status;
    private byte[]? _thumb;
    private TimeSpan _pos, _end;
    private DateTime _posAt;
    private int _version;

    // UI-thread-only album-art cache
    private string? _artKey;
    private Bitmap? _art;            // the frame used for accent/icon (first frame of an animated cover)
    private Bitmap[]? _frames;       // >1 when the thumbnail is an animated GIF
    private int[]? _delays;          // per-frame duration (ms)
    private int _totalDelay;         // sum of _delays; 0 → static
    private Color _accent = White;

    public MediaWidget(MediaSessions sessions, int slot)
    {
        _sessions = sessions;
        _slot = slot;
        _sessions.Changed += Resync; // slots reassigned → re-point this widget at its slot's session
        Resync();
    }

    // the process name of the app this slot mirrors (e.g. "spotify", "chrome") — for the focus-hide rule
    public string App => _sessions.SlotApp(_slot);

    public string Icon => "\uE768"; // Segoe MDL2 Play — ponytail: glyph, not album art (menu draws glyphs only)

    public bool IsActive
    {
        get
        {
            lock (_lock)
            {
                return _title != null
                    && (_status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
                     || _status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused);
            }
        }
    }
    public int Version { get { lock (_lock) { return _version; } } }

    // circle / dropdown = the real app icon (Spotify, Chrome, VLC...), falling back to album art
    public Bitmap? IconImage
    {
        get
        {
            string? id; lock (_lock) { id = _appId; }
            var app = AppIcon.ForAumid(id);
            if (app != null) return app;
            EnsureArt();
            return _art;
        }
    }

    // decode _thumb -> _art once per track; UI-thread only (DrawContent + IconImage both call it).
    // also derive the accent colour (Apple-style tint from the art) for the equalizer.
    private void EnsureArt()
    {
        byte[]? thumb; string? key;
        lock (_lock) { thumb = _thumb; key = _trackKey; }
        if (key != _artKey)
        {
            DisposeFrames();
            (_frames, _delays) = DecodeFrames(thumb);
            _art = _frames is { Length: > 0 } ? _frames[0] : null;
            _totalDelay = 0;
            if (_delays != null) foreach (var d in _delays) _totalDelay += d;
            _artKey = key;
            _accent = _art != null ? Fx.Accent(_art) : White;
            _palette = Palette(_accent);
        }
    }

    private void DisposeFrames()
    {
        if (_frames != null) foreach (var f in _frames) f?.Dispose();
        _frames = null; _delays = null; _art = null;
    }

    // frame to paint right now: static cover → itself; animated GIF → the frame for the elapsed time (loops)
    private Bitmap? CurArt()
    {
        if (_frames == null || _frames.Length == 0) return null;
        if (_frames.Length == 1 || _totalDelay <= 0) return _frames[0];
        int t = (int)(Environment.TickCount64 % _totalDelay);
        for (int i = 0; i < _frames.Length; i++) { t -= _delays![i]; if (t < 0) return _frames[i]; }
        return _frames[^1];
    }

    // our slot's session changed (a player appeared/closed, or slots reassigned) → re-point at it
    private void Resync() => Hook(_sessions.Session(_slot));

    // ponytail: old sessions keep their handlers until GC drops them; switches are rare, so no unhook.
    // skips re-hooking when the slot's app is the one already showing (avoids churn on every Changed).
    private void Hook(GlobalSystemMediaTransportControlsSession? s)
    {
        string? newId = s?.SourceAppUserModelId;
        lock (_lock)
        {
            if (s != null && _session != null && newId == _appId) return;
            _session = s; _appId = newId;
        }
        if (s == null) { Clear(); return; }
        try
        {
            s.MediaPropertiesChanged += (_, __) => RefreshProps(s);
            s.PlaybackInfoChanged += (_, __) => RefreshPlayback(s);
            s.TimelinePropertiesChanged += (_, __) => RefreshTimeline(s);
            RefreshProps(s);
            RefreshPlayback(s);
            RefreshTimeline(s);
        }
        catch { }
    }

    private async void RefreshProps(GlobalSystemMediaTransportControlsSession s)
    {
        try
        {
            var props = await s.TryGetMediaPropertiesAsync();
            string title = Fx.CleanText(props.Title);   // fold 𝗳𝗮𝗻𝗰𝘆/decorative Unicode so it isn't tofu boxes
            string artist = Fx.CleanText(props.Artist);
            string key = title + "" + artist;
            byte[]? thumb = props.Thumbnail != null ? await ReadStream(props.Thumbnail) : null;
            bool wide = ThumbIsWide(thumb); // decode dims off-lock (small image, track-change only)
            bool trackChanged;
            lock (_lock)
            {
                if (!ReferenceEquals(_session, s)) return; // stale session
                trackChanged = key != _trackKey;
                _title = title.Length > 0 ? title : (artist.Length > 0 ? artist : null);
                _artist = artist;
                _trackKey = key;
                if (thumb != null || trackChanged) { _thumb = thumb; _thumbWide = wide; }
                // new track: the timeline still holds the OLD track's position (e.g. 1:53 against a
                // shorter new duration → bar pinned at the end until the real event lands). Zero it and
                // bump the epoch so the bar restarts from 0 instead of gliding back from the end.
                if (trackChanged) { _pos = TimeSpan.Zero; _end = TimeSpan.Zero; _posAt = DateTime.UtcNow; _trackEpoch++; }
                _version++;
            }
            if (trackChanged) DebugLog(title);
        }
        catch { }
    }

    // ponytail: one line per track change to learn what real players report (VLC's app id in particular,
    // so we can wire its subtitle/PiP hotkey). Trim once VLC support is confirmed.
    private void DebugLog(string title)
    {
        try
        {
            string app = App, id; bool video; lock (_lock) { id = _appId ?? ""; video = _isVideo; }
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Halo", "media-debug.txt"),
                $"{DateTime.Now:HH:mm:ss} app='{app}' aumid='{id}' video={video} title='{title}'\r\n");
        }
        catch { }
    }

    private void RefreshPlayback(GlobalSystemMediaTransportControlsSession s)
    {
        try
        {
            var info = s.GetPlaybackInfo();
            lock (_lock)
            {
                if (!ReferenceEquals(_session, s)) return;
                _status = info.PlaybackStatus;
                _playing = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                _isVideo = info.PlaybackType == Windows.Media.MediaPlaybackType.Video;
                if (info.PlaybackRate is double pr && pr > 0) _rate = pr; // reflect the app's real rate
                _version++;
            }
        }
        catch { }
    }

    private void RefreshTimeline(GlobalSystemMediaTransportControlsSession s)
    {
        try
        {
            var t = s.GetTimelineProperties();
            lock (_lock)
            {
                if (!ReferenceEquals(_session, s)) return;
                _pos = t.Position;
                _end = t.EndTime;
                _posAt = DateTime.UtcNow;
                _version++;
            }
        }
        catch { }
    }

    private void Clear()
    {
        lock (_lock)
        {
            _status = GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed;
            if (_title == null) return;
            _title = _artist = _trackKey = null;
            _thumb = null;
            _pos = _end = TimeSpan.Zero;
            _version++;
        }
    }

    private static async Task<byte[]?> ReadStream(IRandomAccessStreamReference r)
    {
        try
        {
            using var s = await r.OpenReadAsync();
            uint size = (uint)s.Size;
            if (size == 0) return null;
            using var reader = new DataReader(s);
            await reader.LoadAsync(size);
            var bytes = new byte[size];
            reader.ReadBytes(bytes);
            return bytes;
        }
        catch { return null; }
    }

    private GlobalSystemMediaTransportControlsSession? Cur() { lock (_lock) { return _session; } }
    private void Toggle() { var s = Cur(); if (s != null) _ = s.TryTogglePlayPauseAsync(); }
    private void Prev() { var s = Cur(); if (s != null) _ = s.TrySkipPreviousAsync(); }
    private void Next() { var s = Cur(); if (s != null) _ = s.TrySkipNextAsync(); }
    private void Stop() { var s = Cur(); if (s != null) _ = s.TryStopAsync(); }

    // video ±10s: seek relative to the (extrapolated) current position, clamped to [0, end]
    private void SeekBy(int secs)
    {
        var s = Cur();
        TimeSpan pos, end; bool playing; DateTime at;
        lock (_lock) { pos = _pos; end = _end; playing = _playing; at = _posAt; }
        if (s == null) return;
        var cur = playing ? pos + (DateTime.UtcNow - at) : pos;
        var target = cur + TimeSpan.FromSeconds(secs);
        if (target < TimeSpan.Zero) target = TimeSpan.Zero;
        if (end > TimeSpan.Zero && target > end) target = end;
        try { _ = s.TryChangePlaybackPositionAsync(target.Ticks); } catch { }
    }
    private void SetVol(float f) { _meter.SetVolume(f); Bump(); }
    private void Mute() { _meter.ToggleMute(); Bump(); }
    private void Bump() { lock (_lock) { _version++; } }

    private void Seek(float f)
    {
        var s = Cur();
        TimeSpan end; lock (_lock) { end = _end; }
        if (s == null || end <= TimeSpan.Zero) return;
        try { _ = s.TryChangePlaybackPositionAsync((long)(Math.Clamp(f, 0f, 1f) * end.Ticks)); } catch { }
    }

    private static (RectangleF bar, RectangleF mute) VolLayout(int w) => (new RectangleF(62, 178, 96, 20), new RectangleF(24, 172, 32, 32));
    private static RectangleF SeekRect(int w) { float tx = 180; return new RectangleF(tx, 108, w - tx - 26, 18); }

    private enum Btn { Prev, Play, Next, Back10, Fwd10, Cc, Speed }

    // playback-rate presets the Speed chip cycles through (wraps 2x → 1x).
    private static readonly double[] Rates = { 1.0, 1.25, 1.5, 1.75, 2.0 };

    // transport row: music = prev/play/next; video = ±10s seek / play-pause + a speed chip, plus CC
    // only when the app has a known hotkey (no dead buttons). No Stop, no PiP (user removed both).
    private Btn[] Layout()
    {
        var app = App;
        if (!IsVideo()) return new[] { Btn.Prev, Btn.Play, Btn.Next };
        var l = new List<Btn> { Btn.Back10, Btn.Play, Btn.Fwd10, Btn.Speed };
        if (SubtitleKey(app) != 0) l.Add(Btn.Cc);
        return l.ToArray();
    }

    // cycle to the next-higher preset (wrap to 1x past the top) and ask the session to change rate.
    // SMTC's TryChangePlaybackRateAsync is honoured by Films&TV / WMP / most Store players and modern
    // browsers; an app that ignores it just keeps 1x (honest no-op, not a crash).
    private void CycleSpeed()
    {
        var s = Cur();
        if (s == null) return;
        double cur; lock (_lock) { cur = _rate; }
        double next = Rates[0];
        foreach (var r in Rates) if (r > cur + 0.01) { next = r; break; }
        lock (_lock) { _rate = next; }
        try { _ = s.TryChangePlaybackRateAsync(next); } catch { }
        Bump();
    }

    private static string RateText(double r) =>
        (r % 1 == 0 ? ((int)r).ToString() : r.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)) + "×";

    // several signals, any one wins — players lie about PlaybackType constantly:
    //  • PlaybackType=Video (honest apps)
    //  • known video-player app (mpc/mpv/potplayer/…)
    //  • video filename in the title (local files in WMP/players)
    //  • a WIDE thumbnail — video frames are 16:9, album art is square (catches Media Player + browsers)
    //  • a browser session with no artist or no duration — live/sports streams, not music
    private bool IsVideo()
    {
        bool video, wide; string? title, artist; TimeSpan end;
        lock (_lock) { video = _isVideo; wide = _thumbWide; title = _title; artist = _artist; end = _end; }
        return video || wide || IsVideoApp(App) || HasVideoExt(title)
            || (IsBrowser(App) && (string.IsNullOrEmpty(artist) || end <= TimeSpan.Zero));
    }

    private static bool ThumbIsWide(byte[]? thumb)
    {
        if (thumb == null || thumb.Length == 0) return false;
        try
        {
            using var ms = new MemoryStream(thumb);
            using var img = Image.FromStream(ms, useEmbeddedColorManagement: false, validateImageData: false);
            return img.Height > 0 && img.Width >= img.Height * 1.4f;
        }
        catch { return false; }
    }

    private static bool IsVideoApp(string app) =>
        app.Contains("vlc") || app.Contains("mpc") || app.Contains("mpv") || app.Contains("potplayer")
        || app.Contains("wmplayer") || app.Contains("kmplayer") || app.Contains("gom")
        || app.Contains("smplayer") || app.Contains("video.ui") || app.Contains("media.player");

    private static readonly string[] VideoExt =
        { ".mkv", ".mp4", ".avi", ".mov", ".webm", ".m4v", ".flv", ".wmv", ".mpg", ".mpeg", ".ts", ".3gp", ".ogv" };
    private static bool HasVideoExt(string? title)
    {
        if (string.IsNullOrEmpty(title)) return false;
        var t = title.ToLowerInvariant();
        foreach (var e in VideoExt) if (t.Contains(e)) return true;
        return false;
    }

    public IReadOnlyList<(RectangleF rect, Action<PointF> onClick)> Buttons(int w, int h)
    {
        var (vbar, mute) = VolLayout(w);
        var seek = SeekRect(w);
        var list = new List<(RectangleF, Action<PointF>)>
        {
            (seek, pt => Seek((pt.X - seek.X) / seek.Width)),
            (vbar, pt => SetVol((pt.X - vbar.X) / vbar.Width)),
            (mute, _ => Mute()),
        };
        var layout = Layout();
        var r = BtnRects(w, h, layout.Length);
        for (int i = 0; i < layout.Length; i++)
        {
            Action act = layout[i] switch
            {
                Btn.Prev => Prev,
                Btn.Next => Next,
                Btn.Back10 => () => SeekBy(-10),
                Btn.Fwd10 => () => SeekBy(10),
                Btn.Speed => CycleSpeed,
                Btn.Cc => () => SendHotkey(SubtitleKey(App)),
                _ => Toggle,
            };
            list.Add((r[i], _ => act()));
        }
        return list;
    }

    private static bool IsBrowser(string app) =>
        app.Contains("chrome") || app.Contains("msedge") || app.Contains("edge") || app.Contains("firefox")
        || app.Contains("brave") || app.Contains("opera") || app.Contains("vivaldi");

    // best-effort per-app hotkey (needs the player focused). unknown app → 0 = no button.
    private static byte SubtitleKey(string app) =>
        app.Contains("vlc") || app.Contains("mpv") ? (byte)'V' : IsBrowser(app) ? (byte)'C' : (byte)0;

    // focus the player's window, then inject the key (KeyInject verifies focus before typing).
    // ponytail: fragile by nature (right app/tab must be up) — user accepted it.
    private void SendHotkey(byte vk)
    {
        if (vk == 0) return;
        string? title; lock (_lock) { title = _title; }
        KeyInject.Send(PlayerWindow(App, title), vk);
    }

    // window of the slot's app; PREFER the one whose title contains the playing media's title —
    // browsers have many windows, and the key must land in the one with the video ("کار نمیده" root)
    private static IntPtr PlayerWindow(string app, string? mediaTitle)
    {
        if (app.Length == 0) return IntPtr.Zero;
        string hint = (mediaTitle ?? "").Trim();
        if (hint.Length > 24) hint = hint[..24];
        IntPtr first = IntPtr.Zero, matched = IntPtr.Zero;
        var buf = new System.Text.StringBuilder(512);
        Halo.Interop.Win32.EnumWindows((h, _) =>
        {
            if (!Halo.Interop.Win32.IsWindowVisible(h) || Halo.Interop.Win32.GetWindowTextLengthW(h) == 0) return true;
            try
            {
                Halo.Interop.Win32.GetWindowThreadProcessId(h, out uint pid);
                using var p = System.Diagnostics.Process.GetProcessById((int)pid);
                string pn = p.ProcessName.ToLowerInvariant();
                if (pn != app && !pn.Contains(app) && !app.Contains(pn)) return true;
                if (first == IntPtr.Zero) first = h;
                if (hint.Length >= 4)
                {
                    buf.Clear();
                    Halo.Interop.Win32.GetWindowTextW(h, buf, buf.Capacity);
                    if (buf.ToString().Contains(hint, StringComparison.OrdinalIgnoreCase)) { matched = h; return false; }
                }
                else return false; // no hint → first window wins (old behaviour)
            }
            catch { }
            return true;
        }, IntPtr.Zero);
        return matched != IntPtr.Zero ? matched : first;
    }

    private static RectangleF[] BtnRects(int w, int h, int n)
    {
        const float artX = 26, artSize = 132, size = 40, gap = 18;
        float colL = artX + artSize + 22, colR = w - 26;
        float cx = (colL + colR) / 2f, total = n * size + (n - 1) * gap, x0 = cx - total / 2f, y = 158;
        var r = new RectangleF[n];
        for (int i = 0; i < n; i++) r[i] = new RectangleF(x0 + i * (size + gap), y, size, size);
        return r;
    }

    public void DrawContent(Graphics g, int w, int h, float fade)
    {
        if (fade <= 0.01f) return;
        string? title, artist; bool playing; TimeSpan pos, end; DateTime posAt;
        lock (_lock)
        {
            title = _title; artist = _artist; playing = _playing;
            pos = _pos; end = _end; posAt = _posAt;
        }
        if (title == null) return;

        EnsureArt();

        const float artX = 26, artY = 26, artSize = 132;
        Fx.Glow(g, w, h, fade, artX + artSize / 2f, artY + artSize / 2f, w * 0.85f, h * 1.2f, 38, _accent);
        DrawArt(g, artX, artY, artSize, fade);

        float tx = artX + artSize + 22, tw = w - tx - 26;
        using var titleF = new Font("Segoe UI Semibold", 22f, GraphicsUnit.Pixel);
        using var bodyF = new Font("Segoe UI", 15f, GraphicsUnit.Pixel);
        using var timeF = new Font("Segoe UI", 12f, GraphicsUnit.Pixel);
        using (var tb = new SolidBrush(Mul(White, fade)))
            DrawLine(g, title, titleF, tb, tx, 34, tw);
        if (!string.IsNullOrEmpty(artist))
            using (var ab = new SolidBrush(Mul(Dim, fade)))
                DrawLine(g, artist, bodyF, ab, tx, 66, tw);

        // seek bar (extrapolate while playing so it advances between events); the SHOWN fraction eases
        // toward the real one so seeks/track-changes glide instead of snapping ("نرم")
        var now = playing ? pos + (DateTime.UtcNow - posAt) : pos;
        float frac = end > TimeSpan.Zero ? (float)Math.Clamp(now / end, 0, 1) : 0f;
        int epoch; lock (_lock) epoch = _trackEpoch;
        if (epoch != _shownEpoch) { _shownEpoch = epoch; _fracShown = frac; } // new track: snap to 0:00
        _fracShown = _fracShown < 0 ? frac : _fracShown + (frac - _fracShown) * 0.18f;
        if (Math.Abs(frac - _fracShown) < 0.002f) _fracShown = frac;
        float by = 116, bh = 5;
        Fill(g, tx, by, tw, bh, Mul(Track, fade));
        if (_fracShown > 0) Fill(g, tx, by, tw * _fracShown, bh, Mul(White, fade));
        if (end > TimeSpan.Zero)
        {
            using var eb = new SolidBrush(Mul(Dim, fade));
            g.DrawString(Fmt(now), timeF, eb, tx, by + 8);
            var ts = g.MeasureString(Fmt(end), timeF);
            g.DrawString(Fmt(end), timeF, eb, tx + tw - ts.Width, by + 8);
        }

        // volume (left column, under the art): soft glass mute chip + a bar that breathes on hover;
        // shown level eases toward the real one so click-to-set glides
        var (vbar, mute) = VolLayout(w);
        bool muted = _meter.Muted();
        float volNow = muted ? 0f : _meter.Volume();
        _volShown = _volShown < 0 ? volNow : _volShown + (volNow - _volShown) * 0.30f;
        if (Math.Abs(volNow - _volShown) < 0.004f) _volShown = volNow;
        float vol = _volShown;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var volHit = RectangleF.Union(vbar, mute); volHit.Inflate(4f, 6f);
        bool vHov = WidgetInput.Over && volHit.Contains(WidgetInput.Mouse);
        _volHover += ((vHov ? 1f : 0f) - _volHover) * 0.35f;
        float vt = _volHover;
        using (var fb = new SolidBrush(Mul(Color.FromArgb((int)(13 + 16 * vt), 255, 255, 255), fade)))
            g.FillEllipse(fb, mute);
        using (var pen = new Pen(Mul(Color.FromArgb((int)(28 + 26 * vt), 255, 255, 255), fade), 1f))
            g.DrawEllipse(pen, mute);
        DrawGlyphSoft(g, mute, muted ? "\uE74F" : "\uE767", 16f, muted ? fade * 0.55f : fade * (0.8f + 0.2f * vt));
        float vy = vbar.Y + vbar.Height / 2f, bh2 = 4f + 2f * vt;
        Fill(g, vbar.X, vy - bh2 / 2f, vbar.Width, bh2, Mul(Color.FromArgb(34, 255, 255, 255), fade));
        if (vol > 0)
            Fill(g, vbar.X, vy - bh2 / 2f, vbar.Width * vol, bh2,
                Mul(Color.FromArgb((int)(185 + 45 * vt), 255, 255, 255), fade));

        // transport = glass chips; a small eased grow + brighten on hover (frames ride the
        // mouse-move redraws while the cursor is over the open panel)
        var layout = Layout();
        var rects = BtnRects(w, h, layout.Length);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        for (int i = 0; i < layout.Length; i++)
        {
            var r = rects[i];
            var hit = r; hit.Inflate(4f, 4f);
            bool hov = WidgetInput.Over && hit.Contains(WidgetInput.Mouse);
            _btnHover[i] += ((hov ? 1f : 0f) - _btnHover[i]) * 0.35f;
            if (Math.Abs((hov ? 1f : 0f) - _btnHover[i]) < 0.03f) _btnHover[i] = hov ? 1f : 0f; // settle
            float t = _btnHover[i], sc = 1f + 0.09f * t, d = r.Width * sc;
            var rr = new RectangleF(r.X + (r.Width - d) / 2f, r.Y + (r.Height - d) / 2f, d, d);
            var kind = layout[i];
            bool bare = kind == Btn.Cc; // toggle = bare mark, no glass chip around it
            if (!bare)
            {
                using (var fb = new SolidBrush(Mul(Color.FromArgb((int)(15 + 19 * t), 255, 255, 255), fade)))
                    g.FillEllipse(fb, rr);
                using (var pen = new Pen(Mul(Color.FromArgb((int)(34 + 30 * t), 255, 255, 255), fade), 1f))
                    g.DrawEllipse(pen, rr);
            }
            float a = fade * (0.8f + 0.2f * t);
            if (kind == Btn.Cc) { Fx.DrawCcMark(g, rr, a); continue; }
            if (kind == Btn.Back10) { Fx.DrawSeekArrow(g, rr, forward: false, a); continue; }
            if (kind == Btn.Fwd10) { Fx.DrawSeekArrow(g, rr, forward: true, a); continue; }
            if (kind == Btn.Speed) { double rate; lock (_lock) { rate = _rate; } DrawRateLabel(g, rr, rate, a); continue; }
            bool isPlay = kind == Btn.Play;
            string glyph = isPlay ? Glyph(playing ? 0xE769 : 0xE768)
                : kind == Btn.Prev ? Glyph(0xE892) : Glyph(0xE893);
            DrawGlyphSoft(g, rr, glyph, (isPlay ? 22f : 17f) * sc, a, isPlay && !playing ? 1.5f : 0f);
        }
    }

    private static string Glyph(int codepoint) => ((char)codepoint).ToString();

    // the Speed chip's label ("1×", "1.5×", "2×") centred in the chip — smaller type so "1.25×" fits.
    private void DrawRateLabel(Graphics g, RectangleF r, double rate, float fade)
    {
        string t = RateText(rate);
        float px = r.Height * (t.Length >= 5 ? 0.26f : 0.32f);
        using var f = new Font("Segoe UI Semibold", px, GraphicsUnit.Pixel);
        using var b = new SolidBrush(Mul(White, fade * 0.92f));
        using var sf = new StringFormat(StringFormat.GenericTypographic)
        { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(t, f, b, r, sf);
    }

    private readonly float[] _btnHover = new float[8];
    private float _volHover;
    private float _volShown = -1f, _fracShown = -1f; // eased displayed values (smooth bars)
    private int _trackEpoch, _shownEpoch;            // bumped per track change → seek bar snaps, no glide

    private static readonly FontFamily FluentFamily = new("Segoe Fluent Icons");

    // soft + truly centred: outline the glyph as a path and centre its ink bounds in the rect
    // (font-metric centring leaves Fluent glyphs visibly off inside the chips).
    // opticalDx: bbox-centring reads wrong for lopsided shapes (the play triangle's mass sits
    // left of its box centre) — callers nudge those toward their visual centre.
    private void DrawGlyphSoft(Graphics g, RectangleF r, string glyph, float px, float fade, float opticalDx = 0f)
    {
        using var path = new GraphicsPath();
        using var sf = new StringFormat(StringFormat.GenericTypographic);
        path.AddString(glyph, FluentFamily, (int)FontStyle.Regular, px, PointF.Empty, sf);
        path.Flatten(); // curve control points inflate GetBounds — flatten first for true ink bounds
        var b = path.GetBounds();
        if (b.Width <= 0 || b.Height <= 0) return;
        using var m = new Matrix();
        // snap to whole pixels so the AA edge is symmetric (half-pixel offsets read as "off centre")
        m.Translate(MathF.Round(r.X + (r.Width - b.Width) / 2f - b.X + opticalDx),
                    MathF.Round(r.Y + (r.Height - b.Height) / 2f - b.Y));
        path.Transform(m);
        using var br = new SolidBrush(Mul(White, fade * 0.92f));
        g.FillPath(br, path);
    }

    private void DrawArt(Graphics g, float x, float y, float size, float fade, float radius = 14f)
    {
        using var path = Rounded(new RectangleF(x, y, size, size), radius);
        // album art if the track has any; otherwise the source app's icon (podcasts, some videos and
        // radio streams ship no thumbnail \u2014 the app icon reads far better than a generic music glyph)
        Bitmap? img = CurArt();
        if (img == null) { string? id; lock (_lock) { id = _appId; } img = AppIcon.ForAumid(id); }
        if (img != null)
        {
            CoverFill(g, img, x, y, size, path, fade);
        }
        else
        {
            using var b = new SolidBrush(Mul(Color.FromArgb(40, 255, 255, 255), fade));
            g.FillPath(b, path);
            DrawGlyph(g, new RectangleF(x, y, size, size), "\uE8D6", size * 0.5f, fade * 0.7f); // MusicInfo
        }
    }

    // HQ-scale a (often small) image cover-fit to a square, then fill the rounded path with it as a
    // texture so the corners are anti-aliased (SetClip gives jagged, "dirty" edges).
    private static void CoverFill(Graphics g, Bitmap img, float x, float y, float size, GraphicsPath path, float fade)
    {
        int s = Math.Max(1, (int)Math.Ceiling(size));
        using var scaled = new Bitmap(s, s, PixelFormat.Format32bppPArgb);
        using (var sg = Graphics.FromImage(scaled))
        {
            sg.InterpolationMode = InterpolationMode.HighQualityBicubic;
            sg.PixelOffsetMode = PixelOffsetMode.HighQuality;
            sg.SmoothingMode = SmoothingMode.HighQuality;
            using var ia = new ImageAttributes();
            ia.SetWrapMode(WrapMode.TileFlipXY);            // no edge fringe
            ia.SetColorMatrix(new ColorMatrix { Matrix33 = fade });
            int side = Math.Min(img.Width, img.Height);     // cover-fit to a centered square
            sg.DrawImage(img, new Rectangle(0, 0, s, s),
                (img.Width - side) / 2, (img.Height - side) / 2, side, side, GraphicsUnit.Pixel, ia);
        }
        using var tb = new TextureBrush(scaled) { WrapMode = WrapMode.Clamp };
        tb.TranslateTransform(x, y);
        g.FillPath(tb, path);
    }

    public bool Animating { get { lock (_lock) { return _title != null && _playing; } } }

    // ring around the collapsed album-art circle = playback position (like the download %). Only when a
    // real duration exists (live streams have none → no ring). Extrapolated each frame so it glides.
    public Color? Ring
    {
        get { lock (_lock) return _title != null && _end > TimeSpan.Zero ? _accent : (Color?)null; }
    }
    public float RingProgress
    {
        get
        {
            TimeSpan pos, end; bool playing; DateTime at; string? t;
            lock (_lock) { pos = _pos; end = _end; playing = _playing; at = _posAt; t = _title; }
            if (t == null || end <= TimeSpan.Zero) return -1f;
            var now = playing ? pos + (DateTime.UtcNow - at) : pos;
            return (float)Math.Clamp(now / end, 0, 1);
        }
    }

    // Collapsed pill = album art on the left + an audio equalizer on the right (Dynamic-Island style).
    public void DrawCollapsed(Graphics g, int w, int h, float fade)
    {
        string? title; bool playing;
        lock (_lock) { title = _title; playing = _playing; }
        if (title == null) return;
        EnsureArt();
        float sz = h - 14f, x = 9, y = (h - sz) / 2f;
        Fx.Glow(g, w, h, fade, x + sz / 2f, h / 2f, w * 0.7f, h * 2.2f, 34, _accent);
        DrawArt(g, x, y, sz, fade, sz * 0.28f);
        DrawEqualizer(g, w - 14f, h / 2f, fade, playing);
    }

    private const int EqBars = 9;
    private readonly AudioMeter _meter = new();
    private readonly float[] _eq = new float[EqBars];
    private float _amp;

    // Same visual style, REAL tone: each bar is its own frequency band (bass → treble, left → right)
    // from a WASAPI-loopback FFT — the bars follow what the music actually does. If capture isn't
    // available (rare), falls back to the old peak-driven animation so the pill never looks dead.
    private void DrawEqualizer(Graphics g, float rightX, float cy, float fade, bool playing)
    {
        const float barW = 2.6f, gap = 2.6f, maxH = 22f, minH = 2.6f;
        float totalW = EqBars * barW + (EqBars - 1) * gap;
        float x0 = rightX - totalW;

        float[]? bands = playing ? AudioSpectrum.Bands() : null;
        bool live = bands != null && AudioSpectrum.Available;
        float peak = playing ? _meter.Peak() : 0f;
        _amp += (Math.Clamp((float)Math.Sqrt(peak) * 1.4f, 0f, 1f) - _amp) * 0.22f;
        double t = Environment.TickCount / 1000.0;

        for (int i = 0; i < EqBars; i++)
        {
            float target;
            if (live)
            {
                target = minH + (maxH - minH) * bands![i];
            }
            else
            {
                // fallback: old center-weighted peak animation
                float env = 0.25f + 0.75f * (float)Math.Sin(Math.PI * (i + 0.5) / EqBars);
                float phase = 0.5f + 0.5f * (float)Math.Sin(t * (1.7 + i * 0.4) + i * 1.9);
                target = minH + (maxH - minH) * _amp * env * (0.35f + 0.65f * phase);
            }
            // live bands are already smoothed in the analyzer — follow them fast or shouts get flattened
            float rise = live ? 0.80f : 0.35f, fall = live ? 0.32f : 0.12f;
            _eq[i] += (target - _eq[i]) * (target > _eq[i] ? rise : fall);
            float bh = Math.Max(minH, _eq[i]);
            Color col = playing ? PaletteAt((float)i / (EqBars - 1)) : Color.FromArgb(120, 255, 255, 255);
            Fill(g, x0 + i * (barW + gap), cy - bh / 2f, barW, bh, Mul(col, fade));
        }
    }

    // soft multi-hue gradient built from the art accent (a gentle halo, not a harsh rainbow)
    private Color[] _palette = { White, White, White };

    private static Color[] Palette(Color accent)
    {
        Fx.RgbToHsv(accent, out float h, out float s, out float v);
        return new[] { Fx.HsvToRgb((h - 22f + 360f) % 360f, s, v), accent, Fx.HsvToRgb((h + 22f) % 360f, s, v) };
    }

    private Color PaletteAt(float f)
    {
        f = Math.Clamp(f, 0f, 1f);
        return f <= 0.5f ? LerpColor(_palette[0], _palette[1], f * 2f) : LerpColor(_palette[1], _palette[2], (f - 0.5f) * 2f);
    }

    private static Color LerpColor(Color a, Color b, float t)
        => Color.FromArgb(255, (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    private void DrawGlyph(Graphics g, RectangleF r, string glyph, float px, float fade)
    {
        using var f = new Font("Segoe Fluent Icons", px, GraphicsUnit.Pixel);
        using var b = new SolidBrush(Mul(White, fade));
        // centre via StringFormat (MeasureString padding sat glyphs off-centre in the glass chips)
        using var sf = new StringFormat(StringFormat.GenericTypographic)
        { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(glyph, f, b, r, sf);
    }

    // decode the thumbnail into one or more frames. A single image → one frame; an animated GIF cover →
    // all its frames + per-frame delays so DrawArt can play it inside the pill (collapsed and expanded).
    private static (Bitmap[]? frames, int[]? delays) DecodeFrames(byte[]? bytes)
    {
        if (bytes == null || bytes.Length == 0) return (null, null);
        try
        {
            using var ms = new MemoryStream(bytes);
            using var img = Image.FromStream(ms);
            int n = 1;
            try { n = img.GetFrameCount(FrameDimension.Time); } catch { }
            if (n <= 1) return (new[] { new Bitmap(img) }, new[] { 0 }); // detach from the stream

            var frames = new Bitmap[n];
            var delays = new int[n];
            byte[]? pd = null;
            try { pd = img.GetPropertyItem(0x5100)?.Value; } catch { } // PropertyTagFrameDelay: n×int32, centiseconds
            for (int i = 0; i < n; i++)
            {
                img.SelectActiveFrame(FrameDimension.Time, i);
                frames[i] = new Bitmap(img); // clone — SelectActiveFrame mutates img in place
                int cs = pd != null && pd.Length >= (i + 1) * 4 ? BitConverter.ToInt32(pd, i * 4) : 10;
                delays[i] = Math.Max(20, cs * 10); // centiseconds→ms, floored so a 0-delay GIF isn't a strobe
            }
            return (frames, delays);
        }
        catch { return (null, null); }
    }

    // one line, single-line ellipsis, right-aligned + RTL for Persian/Arabic titles
    private static void DrawLine(Graphics g, string text, Font f, Brush b, float x, float y, float w)
    {
        using var sf = new StringFormat(StringFormatFlags.NoWrap) { Trimming = StringTrimming.EllipsisCharacter };
        if (IsRtl(text)) sf.FormatFlags |= StringFormatFlags.DirectionRightToLeft; // Near => right edge, ellipsis on the left
        g.DrawString(text, f, b, new RectangleF(x, y, w, f.Height + 4), sf);
    }

    private static bool IsRtl(string s)
    {
        foreach (var c in s)
            if (c >= 0x0590 && c <= 0x08FF) return true; // Hebrew..Arabic Extended
        return false;
    }

    private static string Fmt(TimeSpan t)
        => t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";

    private static void Fill(Graphics g, float x, float y, float w, float h, Color c)
    {
        if (w <= 0) return;
        using var path = Rounded(new RectangleF(x, y, w, h), h / 2f);
        using var b = new SolidBrush(c);
        g.FillPath(b, path);
    }

    private static GraphicsPath Rounded(RectangleF r, float radius)
    {
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        var p = new GraphicsPath();
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    private static Color Mul(Color c, float a)
        => Color.FromArgb((int)Math.Clamp(c.A * a, 0, 255), c.R, c.G, c.B);
}
