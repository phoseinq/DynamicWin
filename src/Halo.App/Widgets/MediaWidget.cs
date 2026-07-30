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

internal sealed class MediaWidget : IWidget
{
    private static readonly Color White = Color.FromArgb(238, 255, 255, 255);
    private static readonly Color Dim = Color.FromArgb(150, 255, 255, 255);
    private static readonly Color Track = Color.FromArgb(46, 255, 255, 255);

    private readonly object _lock = new();
    private readonly MediaSessions _sessions;
    private readonly int _slot;
    private GlobalSystemMediaTransportControlsSession? _session;

    private string? _title, _artist, _trackKey, _appId;
    private bool _playing, _isVideo;
    private double _rate = 1.0;
    private bool _rateEnabled;
    private bool _seekEnabled;
    private bool _thumbWide;

    private GlobalSystemMediaTransportControlsSessionPlaybackStatus _status;
    private byte[]? _thumb;
    private TimeSpan _pos, _end;
    private TimeSpan _start, _minSeek, _maxSeek;
    private TimeSpan? _seekPending;
    private DateTime _seekSentAt;
    private DateTime _posAt;
    private int _version;

    private string? _artKey;
    private Bitmap? _art;
    private Bitmap[]? _frames;
    private int[]? _delays;
    private int _totalDelay;
    private Color _accent = White;

    public MediaWidget(MediaSessions sessions, int slot)
    {
        _sessions = sessions;
        _slot = slot;
        _sessions.Changed += Resync;
        Resync();
    }

    public string App => _sessions.SlotApp(_slot);

    public string Icon => "\uE768";

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

    public Bitmap? IconImage
    {
        get
        {
            string? id; lock (_lock) { id = _appId; }
            var app = AppIcon.ForSessionApp(id);
            if (app != null) return app;
            EnsureArt();
            return _art;
        }
    }

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
            _animatedArt = _frames is { Length: > 1 } && _totalDelay > 0;
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

    private Bitmap? CurArt()
    {
        if (_frames == null || _frames.Length == 0) return null;
        if (_frames.Length == 1 || _totalDelay <= 0) return _frames[0];
        int t = (int)(Environment.TickCount64 % _totalDelay);
        for (int i = 0; i < _frames.Length; i++) { t -= _delays![i]; if (t < 0) return _frames[i]; }
        return _frames[^1];
    }

    private void Resync() => Hook(_sessions.Session(_slot));

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
            string title = Fx.CleanText(props.Title);
            string artist = Fx.CleanText(props.Artist);
            string key = title + "" + artist;
            byte[]? thumb = props.Thumbnail != null ? await ReadStream(props.Thumbnail) : null;
            bool wide = ThumbIsWide(thumb);
            bool trackChanged;
            lock (_lock)
            {
                if (!ReferenceEquals(_session, s)) return;
                trackChanged = key != _trackKey;
                _title = title.Length > 0 ? title : (artist.Length > 0 ? artist : null);
                _artist = artist;
                _trackKey = key;
                if (thumb != null || trackChanged) { _thumb = thumb; _thumbWide = wide; }

                if (trackChanged) { _pos = TimeSpan.Zero; _end = TimeSpan.Zero; _posAt = DateTime.UtcNow; _trackEpoch++; }
                _version++;
            }
            if (trackChanged) DebugLog(title);
        }
        catch { }
    }

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
                _rateEnabled = info.Controls.IsPlaybackRateEnabled;
                _seekEnabled = info.Controls.IsPlaybackPositionEnabled;
                if (info.PlaybackRate is double pr && pr > 0) _rate = pr;
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

                _start = t.StartTime;
                _minSeek = t.MinSeekTime;
                _maxSeek = t.MaxSeekTime;
                _end = t.EndTime;

                if (_seekPending is { } target)
                {
                    var slack = TimeSpan.FromMilliseconds(1500);
                    bool arrived = (t.Position - target).Duration() <= TimeSpan.FromSeconds(2);
                    if (!arrived && DateTime.UtcNow - _seekSentAt < slack) { _version++; return; }
                    _seekPending = null;
                }
                _pos = t.Position;
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
            _pos = _end = _start = _minSeek = _maxSeek = TimeSpan.Zero;
            _seekPending = null;
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

    private void SeekBy(int secs)
    {
        var s = Cur();
        TimeSpan pos; bool playing; DateTime at;
        lock (_lock) { pos = _pos; playing = _playing; at = _posAt; }
        if (s == null) return;
        var cur = playing ? pos + (DateTime.UtcNow - at) : pos;
        SeekTo(s, cur + TimeSpan.FromSeconds(secs));
    }

    private void SeekTo(GlobalSystemMediaTransportControlsSession s, TimeSpan target)
    {
        TimeSpan start, end, lo, hi;
        lock (_lock) { start = _start; end = _end; lo = _minSeek; hi = _maxSeek; }
        var floor = lo > TimeSpan.Zero ? lo : start;
        var ceil = hi > TimeSpan.Zero ? hi : end;
        if (target < floor) target = floor;
        if (ceil > TimeSpan.Zero && target > ceil) target = ceil;
        lock (_lock) { _seekPending = target; _seekSentAt = DateTime.UtcNow; }
        try { _ = s.TryChangePlaybackPositionAsync(target.Ticks); } catch { }
    }
    private void SetVol(float f) { _meter.SetVolume(f); Bump(); }
    private void Mute() { _meter.ToggleMute(); Bump(); }
    private void Bump() { lock (_lock) { _version++; } }

    private void Seek(float f)
    {
        var s = Cur();
        TimeSpan start, end; lock (_lock) { start = _start; end = _end; }
        if (s == null || end <= start) return;
        SeekTo(s, start + TimeSpan.FromTicks((long)(Math.Clamp(f, 0f, 1f) * (end - start).Ticks)));
    }

    private static (RectangleF bar, RectangleF mute) VolLayout(int w) => (new RectangleF(62, 178, 96, 20), new RectangleF(24, 172, 32, 32));
    private static RectangleF SeekRect(int w) { float tx = 180; return new RectangleF(tx, 108, w - tx - 26, 18); }

    private enum Btn { Prev, Play, Next, Back10, Fwd10, Cc }

    private static readonly double[] Rates = { 1.0, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0 };
    private const float SpeedW = 44f, SpeedH = 22f, MenuW = 60f, ItemH = 21f, MenuPad = 5f;
    private bool _speedOpen;
    private float _speedT;

    private static RectangleF SpeedRect(int w) => new(w - 26f - SpeedW, 27f, SpeedW, SpeedH);
    private static RectangleF MenuRect(int w)
        => new(w - 26f - MenuW, 27f + SpeedH + 5f, MenuW, Rates.Length * ItemH + MenuPad * 2f);
    private static RectangleF ItemRect(int w, int i)
    {
        var m = MenuRect(w);
        return new RectangleF(m.X, m.Y + MenuPad + i * ItemH, m.Width, ItemH);
    }

    private void SetRate(double r)
    {
        var s = Cur();
        if (s == null) return;
        lock (_lock) { _rate = r; }
        try { _ = s.TryChangePlaybackRateAsync(r); } catch { }
        Bump();
    }

    private Btn[] Layout()
    {
        var app = App;
        if (!IsVideo()) return new[] { Btn.Prev, Btn.Play, Btn.Next };
        bool rateOk, seekOk; lock (_lock) { rateOk = _rateEnabled; seekOk = _seekEnabled; }
        var l = new List<Btn>();
        if (seekOk) l.Add(Btn.Back10);
        l.Add(Btn.Play);
        if (seekOk) l.Add(Btn.Fwd10);
        if (SubtitleKey(app) != 0) l.Add(Btn.Cc);
        return l.ToArray();
    }

    private static string RateText(double r) =>
        (r % 1 == 0 ? ((int)r).ToString() : r.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)) + "×";

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

    internal static string MetaLine(string? title, string? artist, string? size, string? resolution = null)
    {
        var parts = new List<string>(4);

        if (!string.IsNullOrWhiteSpace(artist)) parts.Add(artist!.Trim());
        else if (Group(title) is { } grp) parts.Add(grp);

        if ((HeightLabel(resolution) ?? resolution ?? Quality(title)) is { } q) parts.Add(q);
        if (Source(title) is { } src) parts.Add(src);
        if (!string.IsNullOrWhiteSpace(size)) parts.Add(size!);

        return parts.Count == 0 ? "·" : string.Join("  ·  ", parts);
    }

    private static readonly (string token, string label)[] Qualities =
    {
        ("2160p", "4K"), ("4320p", "8K"), ("1440p", "1440p"), ("1080p", "1080p"), ("720p", "720p"),
        ("576p", "576p"), ("480p", "480p"), ("360p", "360p"), ("uhd", "4K"),
    };
    internal static string? Quality(string? title)
    {
        if (string.IsNullOrEmpty(title)) return null;
        var t = title.ToLowerInvariant();
        foreach (var (token, label) in Qualities) if (t.Contains(token)) return label;
        return null;
    }

        internal static string? HeightLabel(string? resolution)
    {
        if (string.IsNullOrWhiteSpace(resolution)) return null;
        int x = resolution.IndexOf('x');
        if (x <= 0 || !int.TryParse(resolution.AsSpan(x + 1), out int hgt) || hgt <= 0) return null;
        return hgt >= 4000 ? "8K" : hgt >= 2000 ? "4K" : hgt + "p";
    }

    private static readonly (string token, string label)[] Sources =
    {
        ("remux", "Remux"), ("bluray", "BluRay"), ("blu-ray", "BluRay"), ("brrip", "BRRip"),
        ("bdrip", "BDRip"), ("web-dl", "WEB-DL"), ("webdl", "WEB-DL"), ("webrip", "WEBRip"),
        ("hdtv", "HDTV"), ("dvdrip", "DVDRip"), ("hdcam", "CAM"), ("camrip", "CAM"),
    };
    internal static string? Source(string? title)
    {
        if (string.IsNullOrEmpty(title)) return null;
        var t = title.ToLowerInvariant();
        foreach (var (token, label) in Sources) if (t.Contains(token)) return label;
        return null;
    }

    internal static string? Group(string? title)
    {
        if (string.IsNullOrEmpty(title)) return null;
        var name = title;
        int dot = name.LastIndexOf('.');
        if (dot > 0 && name.Length - dot <= 5) name = name.Substring(0, dot);
        var bits = name.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (bits.Length < 4) return null;
        var last = bits[^1].Trim();
        if (last.Length is < 3 or > 18) return null;
        foreach (var ch in last) if (!char.IsLetterOrDigit(ch) && ch != '-' && ch != '_') return null;

        var lower = last.ToLowerInvariant();
        if (Quality(lower) != null || Source(lower) != null) return null;
        foreach (var noise in new[] { "x264", "x265", "hevc", "av1", "aac", "ac3", "dts", "mp3", "10bit" })
            if (lower == noise) return null;
        return last;
    }

    private string? FileFacts()
    {
        string? title; lock (_lock) title = _title;
        var size = MediaFileInfo.Size(title, Bump);
        return size is { } b ? MediaFileInfo.Human(b) : null;
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

        if (_speedOpen && _speedT > 0.5f)
        {
            var items = new List<(RectangleF, Action<PointF>)>(Rates.Length);
            for (int i = 0; i < Rates.Length; i++)
            {
                double pick = Rates[i];
                items.Add((ItemRect(w, i), _ => SetRate(pick)));
            }
            return items;
        }
        var (vbar, mute) = VolLayout(w);
        var seek = SeekRect(w);
        var list = new List<(RectangleF, Action<PointF>)>
        {
            (vbar, pt => SetVol((pt.X - vbar.X) / vbar.Width)),
            (mute, _ => Mute()),
        };
        bool seekOk2; lock (_lock) { seekOk2 = _seekEnabled; }
        if (seekOk2) list.Insert(0, (seek, pt => Seek((pt.X - seek.X) / seek.Width)));
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

    private static byte SubtitleKey(string app) =>
        app.Contains("vlc") || app.Contains("mpv") ? (byte)'V' : IsBrowser(app) ? (byte)'C' : (byte)0;

    private void SendHotkey(byte vk)
    {
        if (vk == 0) return;
        string? title; lock (_lock) { title = _title; }
        KeyInject.Send(PlayerWindow(App, title), vk);
    }

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
                else return false;
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
        string? title, artist; bool playing; TimeSpan pos, end, start; DateTime posAt;
        lock (_lock)
        {
            title = _title; artist = _artist; playing = _playing;
            pos = _pos; end = _end; start = _start; posAt = _posAt;
        }
        if (title == null) return;

        EnsureArt();
        float dt = Dt();

        const float artX = 26, artY = 26, artSize = 132;

        Fx.Glow(g, w, h, fade, artX + artSize / 2f, artY + artSize / 2f, w * 1.35f, h * 1.9f, 38, _accent);
        DrawArt(g, artX, artY, artSize, fade);

        float tx = artX + artSize + 22, tw = w - tx - 26;
        bool rateOk0; lock (_lock) rateOk0 = _rateEnabled;
        bool showSpeed = rateOk0 && IsVideo();
        if (showSpeed) tw -= SpeedW + 12f;
        using var titleF = new Font("Segoe UI Semibold", 22f, GraphicsUnit.Pixel);
        using var bodyF = new Font("Segoe UI", 15f, GraphicsUnit.Pixel);
        using var timeF = new Font("Segoe UI", 12f, GraphicsUnit.Pixel);

        var titleRow = new RectangleF(tx, 34, tw, titleF.Height + 4);
        titleRow.Inflate(6f, 6f);
        bool onTitle = WidgetInput.Over && titleRow.Contains(WidgetInput.Mouse);
        using (var tb = new SolidBrush(Mul(White, fade)))
            DrawScrollingLine(g, title, titleF, tb, tx, 34, tw, onTitle, dt);

        using (var ab = new SolidBrush(Mul(Dim, fade)))
            DrawLine(g, MetaLine(title, artist, FileFacts()), bodyF, ab, tx, 66, tw);

        var now = playing ? pos + (DateTime.UtcNow - posAt) : pos;

        float frac = end > start ? (float)Math.Clamp((now - start) / (end - start), 0, 1) : 0f;
        int epoch; lock (_lock) epoch = _trackEpoch;
        if (epoch != _shownEpoch) { _shownEpoch = epoch; _fracShown = frac; }
        _fracShown = _fracShown < 0 ? frac : Ease(_fracShown, frac, dt, 0.10f);
        if (Math.Abs(frac - _fracShown) < 0.0004f) _fracShown = frac;

        var seek = SeekRect(w);
        var seekHit = seek; seekHit.Inflate(6f, 10f);
        bool onSeek = WidgetInput.Over && seekHit.Contains(WidgetInput.Mouse);
        bool seekable; lock (_lock) seekable = _seekEnabled;
        if (WidgetInput.Down && !_wasDown && onSeek && seekable) _scrubbing = true;
        if (_scrubbing)
        {
            _scrubFrac = Math.Clamp((WidgetInput.Mouse.X - seek.X) / Math.Max(1f, seek.Width), 0f, 1f);
            if (!WidgetInput.Down) { Seek(_scrubFrac); _scrubbing = false; _fracShown = _scrubFrac; }
        }
        _seekHover = Ease(_seekHover, _scrubbing ? 1f : 0f, dt, 0.07f);
        float st = _seekHover;
        if (_scrubbing) _fracShown = _scrubFrac;
        const float barCy = 118.5f, bhRest = 5f;
        float bh = bhRest * (1f + 2f * st);
        float by = barCy - bh / 2f;
        Fill(g, tx, by, tw, bh, Mul(Track, fade));
        if (_fracShown > 0) Fill(g, tx, by, tw * _fracShown, bh, Mul(White, fade));
        if (end > TimeSpan.Zero)
        {
            using var eb = new SolidBrush(Mul(Dim, fade));
            float ty = barCy + bh / 2f + 3f;

            var span = end - start;
            var shown = _scrubbing ? span * _scrubFrac : now - start;
            g.DrawString(Fmt(shown), timeF, eb, tx, ty);
            var ts = g.MeasureString(Fmt(span), timeF);
            g.DrawString(Fmt(span), timeF, eb, tx + tw - ts.Width, ty);
        }

        var (vbar, mute) = VolLayout(w);
        bool muted = _meter.Muted();
        float volNow = muted ? 0f : _meter.Volume();
        _volShown = _volShown < 0 ? volNow : Ease(_volShown, volNow, dt, 0.06f);
        if (Math.Abs(volNow - _volShown) < 0.002f) _volShown = volNow;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var volHit = vbar; volHit.Inflate(8f, 10f);
        bool onVol = WidgetInput.Over && volHit.Contains(WidgetInput.Mouse);
        if (WidgetInput.Down && !_wasDown && onVol) _volScrubbing = true;
        if (_volScrubbing)
        {
            float f = Math.Clamp((WidgetInput.Mouse.X - vbar.X) / Math.Max(1f, vbar.Width), 0f, 1f);
            _volShown = f;

            if (Math.Abs(f - _volSent) > 0.004f) { SetVol(f); _volSent = f; }
            if (!WidgetInput.Down) { SetVol(f); _volScrubbing = false; }
        }
        float vol = _volShown;
        _volHover = Ease(_volHover, _volScrubbing ? 1f : 0f, dt, 0.07f);
        float vt = _volHover;
        _wasDown = WidgetInput.Down;
        using (var fb = new SolidBrush(Mul(Color.FromArgb((int)(13 + 16 * vt), 255, 255, 255), fade)))
            g.FillEllipse(fb, mute);
        using (var pen = new Pen(Mul(Color.FromArgb((int)(28 + 26 * vt), 255, 255, 255), fade), 1f))
            g.DrawEllipse(pen, mute);
        DrawGlyphSoft(g, mute, muted ? "\uE74F" : "\uE767", 16f, muted ? fade * 0.55f : fade * (0.8f + 0.2f * vt));
        float vy = vbar.Y + vbar.Height / 2f, bh2 = 4f * (1f + 2f * vt);
        Fill(g, vbar.X, vy - bh2 / 2f, vbar.Width, bh2, Mul(Color.FromArgb(34, 255, 255, 255), fade));
        if (vol > 0)
            Fill(g, vbar.X, vy - bh2 / 2f, vbar.Width * vol, bh2,
                Mul(Color.FromArgb((int)(185 + 45 * vt), 255, 255, 255), fade));

        var layout = Layout();
        var rects = BtnRects(w, h, layout.Length);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        for (int i = 0; i < layout.Length; i++)
        {
            var r = rects[i];
            var hit = r; hit.Inflate(4f, 4f);
            bool hov = WidgetInput.Over && hit.Contains(WidgetInput.Mouse);
            _btnHover[i] += ((hov ? 1f : 0f) - _btnHover[i]) * 0.35f;
            if (Math.Abs((hov ? 1f : 0f) - _btnHover[i]) < 0.03f) _btnHover[i] = hov ? 1f : 0f;
            float t = _btnHover[i], sc = 1f + 0.09f * t, d = r.Width * sc;
            var rr = new RectangleF(r.X + (r.Width - d) / 2f, r.Y + (r.Height - d) / 2f, d, d);
            var kind = layout[i];
            bool bare = kind == Btn.Cc;
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
            bool isPlay = kind == Btn.Play;
            string glyph = isPlay ? Glyph(playing ? 0xE769 : 0xE768)
                : kind == Btn.Prev ? Glyph(0xE892) : Glyph(0xE893);
            DrawGlyphSoft(g, rr, glyph, (isPlay ? 22f : 17f) * sc, a, isPlay && !playing ? 1.5f : 0f);
        }

        DrawSpeed(g, w, fade, dt, showSpeed);
    }

    private void DrawSpeed(Graphics g, int w, float fade, float dt, bool show)
    {
        if (!show)
        {
            _speedOpen = false;
            _speedT = Ease(_speedT, 0f, dt, 0.05f);
            if (_speedT < 0.01f) { _speedT = 0f; return; }
        }
        var label = SpeedRect(w);
        var menu = MenuRect(w);
        if (show)
        {
            var hot = label; hot.Inflate(10f, 8f);
            bool over = WidgetInput.Over
                && (hot.Contains(WidgetInput.Mouse) || (_speedOpen && menu.Contains(WidgetInput.Mouse)));
            _speedOpen = over;
            _speedT = Ease(_speedT, over ? 1f : 0f, dt, 0.05f);
        }

        double rate; lock (_lock) rate = _rate;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        if (show)
        {

            using var lf = new Font("Segoe UI Semibold", 13f, GraphicsUnit.Pixel);
            using var lb = new SolidBrush(Mul(White, fade * (0.62f + 0.38f * _speedT)));
            using var sf = new StringFormat(StringFormat.GenericTypographic)
            { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center };
            var textBox = new RectangleF(label.X, label.Y, label.Width - 11f, label.Height);
            g.DrawString(RateText(rate), lf, lb, textBox, sf);
            float cx = label.Right - 5f, cy = label.Y + label.Height / 2f + 1f - 2f * _speedT;
            using var cp = new Pen(Mul(White, fade * (0.45f + 0.4f * _speedT)), 1.4f)
            { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLines(cp, new[] { new PointF(cx - 3.5f, cy - 1.5f), new PointF(cx, cy + 2f),
                                    new PointF(cx + 3.5f, cy - 1.5f) });
        }

        if (_speedT <= 0.01f) return;

        float a = fade * _speedT;
        var m = menu; m.Offset(0f, -6f * (1f - _speedT));
        using (var shadow = new SolidBrush(Color.FromArgb((int)(70 * a), 0, 0, 0)))
        using (var sp = Fx.Rounded(new RectangleF(m.X + 1f, m.Y + 2f, m.Width, m.Height), 11f))
            g.FillPath(shadow, sp);
        using (var back = new SolidBrush(Color.FromArgb((int)(232 * a), 28, 28, 32)))
        using (var mp = Fx.Rounded(m, 11f))
            g.FillPath(back, mp);
        using (var edge = new Pen(Color.FromArgb((int)(46 * a), 255, 255, 255), 1f))
        using (var mp2 = Fx.Rounded(m, 11f))
            g.DrawPath(edge, mp2);

        using var itemF = new Font("Segoe UI", 13f, GraphicsUnit.Pixel);
        using var isf = new StringFormat(StringFormat.GenericTypographic)
        { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        for (int i = 0; i < Rates.Length; i++)
        {
            var r = ItemRect(w, i); r.Offset(0f, -6f * (1f - _speedT));
            bool cur = Math.Abs(Rates[i] - rate) < 0.01;
            bool hov = WidgetInput.Over && r.Contains(WidgetInput.Mouse);
            if (hov)
                using (var hb = new SolidBrush(Color.FromArgb((int)(26 * a), 255, 255, 255)))
                using (var hp = Fx.Rounded(new RectangleF(r.X + 3f, r.Y, r.Width - 6f, r.Height), 7f))
                    g.FillPath(hb, hp);

            using (var tb2 = new SolidBrush(Mul(White, a * (cur || hov ? 0.98f : 0.66f))))
                g.DrawString(RateText(Rates[i]), itemF, tb2, r, isf);
            if (cur)
                using (var db = new SolidBrush(Mul(_accent == White ? White : _accent, a * 0.95f)))
                    g.FillEllipse(db, r.X + 7f, r.Y + r.Height / 2f - 2f, 4f, 4f);
        }
    }

    private static string Glyph(int codepoint) => ((char)codepoint).ToString();

    private readonly float[] _btnHover = new float[8];
    private float _volHover, _seekHover;
    private bool _wasDown, _scrubbing, _volScrubbing;
    private float _scrubFrac, _volSent = -1f;
    private float _volShown = -1f, _fracShown = -1f;
    private int _trackEpoch, _shownEpoch;

    private long _lastTick;
    private float Dt()
    {
        long now = Environment.TickCount64;
        float dt = _lastTick == 0 ? 1f / 60f : (now - _lastTick) / 1000f;
        _lastTick = now;
        return Math.Clamp(dt, 1f / 240f, 0.1f);
    }

    private static float Ease(float shown, float target, float dt, float tau)
        => shown + (target - shown) * (1f - MathF.Exp(-dt / tau));

    private static readonly FontFamily FluentFamily = new("Segoe Fluent Icons");

    private void DrawGlyphSoft(Graphics g, RectangleF r, string glyph, float px, float fade, float opticalDx = 0f)
    {
        using var path = new GraphicsPath();
        using var sf = new StringFormat(StringFormat.GenericTypographic);
        path.AddString(glyph, FluentFamily, (int)FontStyle.Regular, px, PointF.Empty, sf);
        path.Flatten();
        var b = path.GetBounds();
        if (b.Width <= 0 || b.Height <= 0) return;
        using var m = new Matrix();

        m.Translate(MathF.Round(r.X + (r.Width - b.Width) / 2f - b.X + opticalDx),
                    MathF.Round(r.Y + (r.Height - b.Height) / 2f - b.Y));
        path.Transform(m);
        using var br = new SolidBrush(Mul(White, fade * 0.92f));
        g.FillPath(br, path);
    }

    private void DrawArt(Graphics g, float x, float y, float size, float fade, float radius = 14f)
    {
        using var path = Rounded(new RectangleF(x, y, size, size), radius);

        Bitmap? img = CurArt();
        if (img == null) { string? id; lock (_lock) { id = _appId; } img = AppIcon.ForSessionApp(id); }
        if (img != null)
        {
            CoverFill(g, img, x, y, size, path, fade);
        }
        else
        {
            using var b = new SolidBrush(Mul(Color.FromArgb(40, 255, 255, 255), fade));
            g.FillPath(b, path);
            DrawGlyph(g, new RectangleF(x, y, size, size), "\uE8D6", size * 0.5f, fade * 0.7f);
        }
    }

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
            ia.SetWrapMode(WrapMode.TileFlipXY);
            ia.SetColorMatrix(new ColorMatrix { Matrix33 = fade });
            int side = Math.Min(img.Width, img.Height);
            sg.DrawImage(img, new Rectangle(0, 0, s, s),
                (img.Width - side) / 2, (img.Height - side) / 2, side, side, GraphicsUnit.Pixel, ia);
        }
        using var tb = new TextureBrush(scaled) { WrapMode = WrapMode.Clamp };
        tb.TranslateTransform(x, y);
        g.FillPath(tb, path);
    }

    private volatile bool _animatedArt;

    private volatile bool _marqueeScrolling;

    public bool Animating
    {
        get { lock (_lock) { return _title != null && (_playing || _animatedArt || _marqueeScrolling); } }
    }

    public Color? Ring
    {
        get { lock (_lock) return _title != null && _end > TimeSpan.Zero ? _accent : (Color?)null; }
    }
    public float RingProgress
    {
        get
        {
            TimeSpan pos, end, start; bool playing; DateTime at; string? t;
            lock (_lock) { pos = _pos; end = _end; start = _start; playing = _playing; at = _posAt; t = _title; }
            if (t == null || end <= start) return -1f;
            var now = playing ? pos + (DateTime.UtcNow - at) : pos;
            return (float)Math.Clamp((now - start) / (end - start), 0, 1);
        }
    }

    public void DrawCollapsed(Graphics g, int w, int h, float fade)
    {
        string? title; bool playing;
        lock (_lock) { title = _title; playing = _playing; }
        if (title == null) return;
        EnsureArt();
        float sz = h - 14f, x = 9, y = (h - sz) / 2f;
        float prog = RingProgress;

        if (prog >= 0f) Fx.PillBar(g, w, h, fade, prog, _accent, 0.34f);
        Fx.Glow(g, w, h, fade, x + sz / 2f, h / 2f, w * 0.7f, h * 2.2f, 34, _accent);
        DrawArt(g, x, y, sz, fade, sz * 0.28f);

        if (prog >= 0f)
        {
            var ringRect = new RectangleF(x - 2.5f, y - 2.5f, sz + 5f, sz + 5f);
            using var ringPath = Fx.Rounded(ringRect, sz * 0.28f + 2.5f);
            using (var track = new Pen(Mul(Track, fade * 0.9f), 1.7f))
                g.DrawPath(track, ringPath);
            using var pen = new Pen(Mul(_accent == White ? White : _accent, fade * 0.95f), 1.9f)
            { StartCap = LineCap.Round, EndCap = LineCap.Round };
            Fx.PathProgress(g, ringPath, prog, pen);
        }
        DrawEqualizer(g, w - 14f, h / 2f, fade, playing);
    }

    private const int EqBars = 9;
    private readonly AudioMeter _meter = new();
    private readonly float[] _eq = new float[EqBars];
    private float _amp;

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

                float env = 0.25f + 0.75f * (float)Math.Sin(Math.PI * (i + 0.5) / EqBars);
                float phase = 0.5f + 0.5f * (float)Math.Sin(t * (1.7 + i * 0.4) + i * 1.9);
                target = minH + (maxH - minH) * _amp * env * (0.35f + 0.65f * phase);
            }

            float rise = live ? 0.80f : 0.35f, fall = live ? 0.32f : 0.12f;
            _eq[i] += (target - _eq[i]) * (target > _eq[i] ? rise : fall);
            float bh = Math.Max(minH, _eq[i]);
            Color col = playing ? PaletteAt((float)i / (EqBars - 1)) : Color.FromArgb(120, 255, 255, 255);
            Fill(g, x0 + i * (barW + gap), cy - bh / 2f, barW, bh, Mul(col, fade));
        }
    }

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

        Fx.GlyphCentred(g, r, glyph, f, b);
    }

    private static (Bitmap[]? frames, int[]? delays) DecodeFrames(byte[]? bytes)
    {
        if (bytes == null || bytes.Length == 0) return (null, null);
        try
        {
            using var ms = new MemoryStream(bytes);
            using var img = Image.FromStream(ms);
            int n = 1;
            try { n = img.GetFrameCount(FrameDimension.Time); } catch { }
            if (n <= 1) return (new[] { new Bitmap(img) }, new[] { 0 });

            var frames = new Bitmap[n];
            var delays = new int[n];
            byte[]? pd = null;
            try { pd = img.GetPropertyItem(0x5100)?.Value; } catch { }
            for (int i = 0; i < n; i++)
            {
                img.SelectActiveFrame(FrameDimension.Time, i);
                frames[i] = new Bitmap(img);
                int cs = pd != null && pd.Length >= (i + 1) * 4 ? BitConverter.ToInt32(pd, i * 4) : 10;
                delays[i] = Math.Max(20, cs * 10);
            }
            return (frames, delays);
        }
        catch { return (null, null); }
    }

    private static void DrawLine(Graphics g, string text, Font f, Brush b, float x, float y, float w)
    {
        using var sf = new StringFormat(StringFormatFlags.NoWrap) { Trimming = StringTrimming.EllipsisCharacter };
        if (IsRtl(text)) sf.FormatFlags |= StringFormatFlags.DirectionRightToLeft;
        g.DrawString(text, f, b, new RectangleF(x, y, w, f.Height + 4), sf);
    }

    private float _marquee;
    private float _marqueeHold;

    internal const float MarqueeGap = 48f, MarqueeSpeed = 42f, MarqueeHold = 0.35f;

    internal static (float offset, float hold) MarqueeStep(float offset, float hold, float dt, float span)
    {
        if (span <= 0f) return (0f, 0f);
        if (hold < MarqueeHold) return (offset, hold + dt);
        offset += MarqueeSpeed * dt;
        return offset >= span ? (offset - span, 0f) : (offset, hold);
    }

    private void DrawScrollingLine(Graphics g, string text, Font f, Brush b, float x, float y, float w,
        bool hovered, float dt)
    {
        float textW = g.MeasureString(text, f, int.MaxValue, StringFormat.GenericTypographic).Width;
        if (textW <= w || !hovered)
        {

            if (!hovered) { _marquee = 0f; _marqueeHold = 0f; }
            _marqueeScrolling = false;
            DrawLine(g, text, f, b, x, y, w);
            return;
        }
        _marqueeScrolling = true;

        float span = textW + MarqueeGap;
        (_marquee, _marqueeHold) = MarqueeStep(_marquee, _marqueeHold, dt, span);

        var state = g.Save();
        g.SetClip(new RectangleF(x, y, w, f.Height + 4));
        bool rtl = IsRtl(text);
        using var sf = new StringFormat(StringFormatFlags.NoWrap);
        if (rtl) sf.FormatFlags |= StringFormatFlags.DirectionRightToLeft;
        float h2 = f.Height + 4;
        for (int pass = 0; pass < 2; pass++)
        {

            float ox = rtl ? x + w - textW + (_marquee - pass * span)
                           : x - (_marquee - pass * span);
            g.DrawString(text, f, b, new RectangleF(ox, y, textW + 2, h2), sf);
        }
        g.Restore(state);
    }

    private static bool IsRtl(string s)
    {
        foreach (var c in s)
            if (c >= 0x0590 && c <= 0x08FF) return true;
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
