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
    private bool _rateEnabled;       // SMTC says the app supports rate change; Telegram etc. report false -> hide the Speed chip
    private bool _seekEnabled;       // SMTC says seek/position change is supported (Telegram video reports false)
    private bool _thumbWide;         // 16:9-ish thumbnail = a video frame (album art is square)
    // playback status: only Playing/Paused count as a live player. Browsers keep Stopped/Closed sessions
    // around with stale metadata after a video ends — those must NOT hold the pill open (blank black pill).
    private GlobalSystemMediaTransportControlsSessionPlaybackStatus _status;
    private byte[]? _thumb;
    private TimeSpan _pos, _end;
    private TimeSpan _start, _minSeek, _maxSeek;   // the seekable window; not always [0, end]
    // For sessions that report no timeline at all (telegram: end=pos=0 for the whole track), the one
    // number that is a MEASUREMENT rather than an invention is how long the track has been playing by
    // our own clock: telegram starts every track at zero, and play/pause transitions arrive via SMTC.
    // It drifts if the user seeks inside the app itself - accepted; elapsed-only is shown, never a bar.
    private TimeSpan _wallBase;                    // playing time accumulated up to the last transition
    private DateTime _wallAt;                      // start of the current playing stretch; default = unknown
    private TimeSpan? _seekPending;                // where we have asked to be, until the player confirms it
    private DateTimeOffset _seekSentAt, _seekAskedAt;
    private TimeSpan _reported;                    // the last position the PLAYER actually claimed
    private TimeSpan _prevEnd;                     // outgoing track's duration, to spot its leftover timeline
    private long _trackAt;                         // when the track changed, so that wait can't last forever
    private int _seekTries;
    private bool _seekBusy;                        // one outstanding request at a time
    private DateTime _posAt;
    private int _version;

    // UI-thread-only album-art cache
    private string? _artKey;
    private Bitmap? _art;            // the frame used for accent/icon (first frame of an animated cover)
    private volatile bool _artStale; // set when art arrives late for a track already decoded without it
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

    // dev-hook seam (--render-morph): the filmstrip has to be the same picture every time it is rendered,
    // and the only other way to get a track in here is to be playing one. Nothing in a shipping path calls
    // this; with no live session PollTimeline finds nothing to poll and the seeded state stands.
    internal void Seed(string title, string artist, byte[]? thumb, double through)
    {
        lock (_lock)
        {
            _title = title;
            _artist = artist;
            _trackKey = title + "|" + artist;
            _thumb = thumb;
            _thumbWide = false;
            _playing = true;
            _start = TimeSpan.Zero;
            _end = TimeSpan.FromMinutes(4);
            _pos = TimeSpan.FromMinutes(4 * through);
            _posAt = DateTime.UtcNow;
            _version++;
        }
    }

    // the process name of the app this slot mirrors (e.g. "spotify", "chrome") — for the focus-hide rule
    public string App => _sessions.SlotApp(_slot);

    // Read by the local API, which needs to describe what is playing without going through the drawing
    // code that normally reads these under the same lock.
    public int Slot => _slot;
    public string? TitleText { get { lock (_lock) return _title; } }
    public string? ArtistText { get { lock (_lock) return _artist; } }
    public bool Playing { get { lock (_lock) return _playing; } }

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
            var app = AppIcon.ForSessionApp(id);
            if (app != null) return app;
            EnsureArt();
            return _art;
        }
    }

    // decode _thumb -> _art once per track; UI-thread only (DrawContent + IconImage both call it).
    // also derive the accent colour (Apple-style tint from the art) for the equalizer.
    private void EnsureArt()
    {
        byte[]? thumb; string? key; bool stale;
        // _artStale is how late-arriving art gets in: the key has not changed - it is the same track - so
        // without it the decode below would be skipped and the cover that finally turned up ignored.
        // the flag is read and cleared under the same lock as the thumb snapshot: checked outside it, a
        // chase committing between the snapshot and the check had its flag consumed by a decode of the
        // OLD thumb, and with the flag gone the cover that had just landed was never decoded at all.
        lock (_lock)
        {
            thumb = _thumb; key = _trackKey;
            stale = _artStale;
            if (key != _artKey || stale) _artStale = false;
        }
        if (key != _artKey || stale)
        {
            // Same bytes as the cover already decoded -> nothing to do but adopt the key. Without this,
            // every chase retry that re-committed the art already on screen (the chase commits whatever
            // it reads, it cannot see the decoded state) played a full flip with IDENTICAL faces -
            // "the flip animation comes but the art doesn't change", at whatever odd moment the retry
            // schedule landed on. Also covers the next track of the same album: a card that turns to
            // reveal the same face reads as a glitch, not a transition.
            long hash = ThumbHash(thumb);
            if (hash == _artHash)
            {
                _artKey = key;
                return;
            }
            // card flip on a cover change (Task: the old art turns away and the next one comes in from
            // behind it). The outgoing face has to be snapshotted BEFORE DisposeFrames kills it; with no
            // outgoing face (first track, or a chased cover replacing the app icon) the clock starts at
            // the halfway point, so all that plays is the incoming half - an entrance, not a flip of
            // nothing. Clock is a timestamp, not an eased field: DrawCollapsed and DrawContent run in the
            // same frame mid-morph on two different dt clocks, and a shared field stepped by both would
            // flip twice as fast exactly then.
            _prevArt?.Dispose();
            _prevArt = _art != null ? (Bitmap)_art.Clone() : null;
            _flipAt = Environment.TickCount64 - (_prevArt == null ? FlipMs / 2 : 0);
            _artHash = hash;
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
            bool trackChanged, chase;
            int epoch;
            lock (_lock)
            {
                if (!ReferenceEquals(_session, s)) return; // stale session
                trackChanged = key != _trackKey;
                bool firstTrack = _trackKey == null;   // attaching to a session is not "the track changed"
                _title = title.Length > 0 ? title : (artist.Length > 0 ? artist : null);
                _artist = artist;
                _trackKey = key;
                if (thumb != null || trackChanged) { _thumb = thumb; _thumbWide = wide; }
                // new track: the timeline still holds the OLD track's position (e.g. 1:53 against a
                // shorter new duration → bar pinned at the end until the real event lands). Zero it and
                // bump the epoch so the bar restarts from 0 instead of gliding back from the end.
                // ...but only when a track actually gave way to another one. Attaching to a session also lands
                // here, with the title arriving AFTER Hook() has already read a perfectly good timeline, and
                // throwing that away left the pill with no bar at all until the next poll refilled it - the
                // guard below made it worse by then refusing the refill as "the old track's", measured as a
                // solid 2s of end=0 / ring=-1 at startup. There is no predecessor to protect against on the
                // first track: keep everything Hook() learned.
                if (trackChanged && !firstTrack)
                {
                    _prevEnd = _end;                       // how RefreshTimeline recognises the outgoing track
                    _trackAt = Environment.TickCount64;
                    _pos = _end = _start = TimeSpan.Zero;
                    _minSeek = _maxSeek = TimeSpan.Zero;   // a leftover seek window clamps the NEW track's
                    _posAt = DateTime.UtcNow;              // seeks into the old one's range
                }
                if (trackChanged)
                {
                    _trackEpoch++;
                    _wallBase = TimeSpan.Zero;
                    _wallAt = _playing ? DateTime.UtcNow : default;
                    if (_tgTimeline) TelegramPlayer.Reset(); // the settled duration was the OLD track's
                    _tgTimeline = false;   // next track re-earns its numbers from the strip
                }
                _version++;
                // No cover yet is normal, not final: Spotify hands SMTC the track's text as soon as it
                // starts and fetches the artwork afterwards, so the first properties read after a track
                // change routinely comes back with an empty thumbnail. Nothing here ever asked a second
                // time, which is why a track could play to the end wearing the app's logo.
                chase = _thumb is not { Length: > 0 };
                epoch = _trackEpoch;
            }
            if (trackChanged) DebugLog(title);
            if (chase) ChaseArt(s, epoch);
        }
        catch { }
    }

    // Ask again for a cover that was not ready the first time, on a widening delay. Stops the instant art
    // lands or the session goes - so a track that genuinely has no artwork costs six cheap property reads
    // spread over fifteen seconds and then nothing. A track CHANGE restarts the schedule instead of
    // stopping it (see ChaseArt).
    //
    // The epoch still guards the commit: a fetch started for the song before last cannot come back and
    // staple its cover onto whatever is playing now.
    private static readonly int[] ArtRetries = [350, 700, 1400, 2600, 4500, 6000];

    private async void ChaseArt(GlobalSystemMediaTransportControlsSession s, int epoch)
    {
        // one chain per widget - but the chain REBINDS on a track change instead of dying. The old code
        // returned on an epoch mismatch, and that was the hole: the new track's ChaseArt call had already
        // bounced off _chasing while this chain slept in its delay, so a track started during another
        // track's chase (skipping, mostly - spotify leaves the thumbnail empty at the start of nearly
        // every track) got no retries at all and wore the app logo to the end.
        if (_chasing) return;
        _chasing = true;
        try
        {
            for (int i = 0; i < ArtRetries.Length; i++)
            {
                await System.Threading.Tasks.Task.Delay(ArtRetries[i]);
                switch (ChaseStep(s, epoch))
                {
                    case ArtChase.Done: return;
                    case ArtChase.Restart:
                        lock (_lock) { s = _session!; epoch = _trackEpoch; }
                        i = -1;   // new track, fresh schedule
                        continue;
                }
                try
                {
                    var props = await s.TryGetMediaPropertiesAsync();
                    byte[]? thumb = props.Thumbnail != null ? await ReadStream(props.Thumbnail) : null;
                    if (thumb is not { Length: > 0 }) continue;
                    bool wide = ThumbIsWide(thumb);
                    lock (_lock)
                    {
                        if (!ReferenceEquals(_session, s) || _trackEpoch != epoch) continue;
                        _thumb = thumb;
                        _thumbWide = wide;
                        _artStale = true;   // the decode is the UI thread's job, in EnsureArt; set before
                        _version++;         // the version bump so no redraw can consume the bump stale-less
                    }
                    return;
                }
                catch { }
            }
        }
        catch { }
        finally { _chasing = false; }
    }

    internal enum ArtChase { Done, Restart, Fetch }

    // pure step decision so the rebind behaviour is a test: the one that matters is track-moved-on with
    // no art yet -> Restart, never give up (giving up there is exactly the dropped-retry bug above).
    internal static ArtChase Decide(bool sessionAlive, bool trackMoved, bool hasArt) =>
        !sessionAlive ? ArtChase.Done
        : hasArt ? ArtChase.Done
        : trackMoved ? ArtChase.Restart
        : ArtChase.Fetch;

    private ArtChase ChaseStep(GlobalSystemMediaTransportControlsSession s, int epoch)
    {
        lock (_lock)
        {
            return Decide(_session != null,
                          !ReferenceEquals(_session, s) || _trackEpoch != epoch,
                          _thumb is { Length: > 0 });
        }
    }

    private volatile bool _chasing;

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
                bool moved = _status != info.PlaybackStatus
                    || _rateEnabled != info.Controls.IsPlaybackRateEnabled
                    || _seekEnabled != info.Controls.IsPlaybackPositionEnabled;
                _status = info.PlaybackStatus;
                bool nowPlaying = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                if (nowPlaying && !_playing) _wallAt = DateTime.UtcNow;
                else if (!nowPlaying && _playing && _wallAt != default) _wallBase += DateTime.UtcNow - _wallAt;
                _playing = nowPlaying;
                _isVideo = info.PlaybackType == Windows.Media.MediaPlaybackType.Video;
                _rateEnabled = info.Controls.IsPlaybackRateEnabled;
                _seekEnabled = info.Controls.IsPlaybackPositionEnabled;
                if (info.PlaybackRate is double pr && pr > 0 && Math.Abs(pr - _rate) > 0.001)
                { _rate = pr; moved = true; }
                if (moved) _version++;   // a poll that found nothing new must not force a repaint
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
                // A track change and its timeline do not land together: for up to a second or so the player
                // keeps handing out the OUTGOING track's span and position. Believing it is what makes a new
                // song's bar leap forward to wherever the last one ended and then fall back to the start.
                // The leftover is recognisable by still carrying the old duration; wait it out - but only
                // briefly, so a playlist of equal-length tracks can't stall the bar forever. Until it clears,
                // _end is zero and the pill simply shows no bar, which is the honest state: not known yet.
                if (_prevEnd > TimeSpan.Zero)
                {
                    if (MediaTiming.IsLeftover(t.EndTime, _prevEnd, Environment.TickCount64 - _trackAt)) return;
                    _prevEnd = TimeSpan.Zero;
                }
                // StartTime is not always zero, and MinSeekTime/MaxSeekTime is not always the whole track.
                // Windows' Media Player reports a window here, and treating position as "ticks from 0"
                // against it is what made seeking backwards do nothing: the target landed before MinSeekTime
                // and was rejected outright, while a forward target still fell inside the range and worked.
                // ...but a zeroed span is not a report, it is the app declining to answer, and a browser does it
                // constantly: Chrome pushes one real timeline when a video starts and then, once that tab is in
                // the background, comes back with everything at zero. Taken literally that erased a duration we
                // already knew (RingProgress goes to -1, the bar vanishes) and dragged the position back to the
                // start, which is "the bar shows up once and never again". A blank answer about a track we
                // already have a span for is no news at all. Only a track change forgets the span, and
                // RefreshProps zeroes it there; a genuinely durationless live stream reports zero from the very
                // first update, never reaches this state, and still correctly gets no bar.
                if (MediaTiming.IsBlank(t.StartTime, t.EndTime, _start, _end)) return;
                _start = t.StartTime;
                _minSeek = t.MinSeekTime;
                _maxSeek = t.MaxSeekTime;
                _end = t.EndTime;

                // The same players go quiet in a subtler way: the poll keeps returning the SAME position, frame
                // after frame, while the video plays on. Re-stamping _posAt against an unchanged _pos restarts
                // the extrapolation from the same instant every 200ms, so RingProgress can never grow past one
                // poll's worth and the bar sits frozen. An identical reading is the player repeating itself,
                // not time standing still: leave the clock alone and let the extrapolation carry the bar.
                bool repeated = t.Position == _reported;
                _reported = t.Position;
                // While a seek is outstanding, the only report worth believing is one that AGREES with where
                // we asked to be. A timestamp is not enough on its own: a player will happily stamp an update
                // after our request and still carry its pre-seek position in it - measured, and that is how a
                // rapid third tap ended up computing from a position the player had already left. Until it
                // agrees, the pill keeps showing the target, which is also what the next relative tap counts
                // from. NudgeSeek is what makes that agreement happen, or gives up and lets reality back in.
                bool stale = _seekPending is { } want
                    && (t.Position - want).Duration() > TimeSpan.FromSeconds(1.5);
                bool confirming = _seekPending is not null;
                if (!stale)
                {
                    _seekPending = null;
                    // Skip the re-stamp on a repeat, but never on the report that confirms a seek - that one
                    // has to restart the clock at the position we landed on. If a repeat is really the video
                    // buffering, the bar runs slightly ahead until the next differing report pulls it back,
                    // and DrawCollapsed eases that correction rather than snapping it.
                    if (MediaTiming.ShouldRestamp(repeated, _playing, confirming))
                    {
                        bool moved = (t.Position - _pos).Duration() > TimeSpan.FromMilliseconds(250);
                        _pos = t.Position;
                        _posAt = DateTime.UtcNow;
                        if (moved) _version++;
                    }
                }
            }
        }
        catch { }
    }

    // SMTC's TimelinePropertiesChanged is not a stream, it is an occasional nudge: Windows' Media Player
    // fires it on a seek and then says nothing for minutes. If the session was hooked before its file had a
    // duration, EndTime stays ZERO for the rest of the session - measured on a live 2h10m film, where the
    // session itself reported 2:10:23 while the widget still held 0. Everything hangs off that number, so
    // everything broke at once and quietly: the bar never filled, the timestamps never drew, RingProgress
    // was -1, and Seek() returned early on `end <= start` - which is precisely "the bar does nothing".
    //
    // So the timeline is polled as well, twice a second, from the draw path. The event stays: it makes the
    // update prompt. The poll makes it certain.
    private long _pollAt;
    private void PollTimeline()
    {
        long now = Environment.TickCount64;
        if (now - _pollAt < 200) return;   // the seek pump lives on this cadence too
        _pollAt = now;
        if (Cur() is { } s) { RefreshTimeline(s); RefreshPlayback(s); }
        TelegramTimeline();
        NudgeSeek();
    }

    // the timeline in _pos/_end came from telegram's own ui (TelegramPlayer), not smtc
    private bool _tgTimeline;

    // Telegram publishes an EMPTY smtc timeline for the whole track (measured: end=pos=0, canSeek
    // false), but its own player strip carries the numbers - so they are read from there and fed into
    // the same fields smtc would have filled. The bar, the ring, the timestamps and the extrapolation
    // all light up through the ordinary pipeline; no drawing code knows the difference. Scrubbing is
    // re-enabled on top of this by posting a click onto that same strip (TelegramPlayer.SeekTo), even
    // though smtc still reports the session as unseekable.
    private void TelegramTimeline()
    {
        bool want; string? smtcTitle;
        lock (_lock)
        {
            want = _title != null && (_end <= _start || _tgTimeline)
                && _appId?.Contains("telegram", StringComparison.OrdinalIgnoreCase) == true;
            smtcTitle = _title;
        }
        if (!want) return;
        TelegramPlayer.Poke();
        var (pos, dur) = TelegramPlayer.Read();
        // The music strip describes ONE item, and telegram leaves it standing while a video plays over
        // it - so its numbers are only ours when its label is the track smtc is naming. Without this a
        // paused song's 3:45 was printed under a playing video. The video player is its own surface and
        // needs no such check: if it is up, it IS what is playing.
        bool mine = TelegramPlayer.VideoSource
            || TelegramPlayer.TitleMatches(TelegramPlayer.Title, smtcTitle);
        if (mine && TelegramPlayer.Live && dur is { } d && d > TimeSpan.Zero)
        {
            lock (_lock)
            {
                if (!_tgTimeline && _end > _start) return;   // real smtc numbers arrived - they win
                _tgTimeline = true;
                _start = TimeSpan.Zero;
                bool moved = _end != d;
                _end = d;
                // the strip's text is whole seconds; re-stamping every lap yanked the extrapolated bar
                // back a fraction of a second, once a second - the reported jitter. only correct DRIFT:
                // the extrapolation is already tracking real time between strip reads.
                var cur = _playing ? _pos + (DateTime.UtcNow - _posAt) : _pos;
                if ((pos - cur).Duration() > TimeSpan.FromSeconds(1.2))
                {
                    _pos = pos; _posAt = DateTime.UtcNow;
                    moved = true;
                }
                if (moved) _version++;
            }
        }
        else if (!mine || Environment.TickCount64 - TelegramPlayer.LastLiveAt > 5000)
        {
            // strip gone (player closed, window shut to tray), or it moved on to describing something
            // else - stop extrapolating into a pinned bar; the honest no-timeline states (sweep +
            // stopwatch) take back over
            lock (_lock)
            {
                if (!_tgTimeline) return;
                _tgTimeline = false;
                _pos = _end = _start = TimeSpan.Zero;
                _posAt = DateTime.UtcNow;
                _version++;
            }
        }
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
            _wallBase = TimeSpan.Zero; _wallAt = default;
            _tgTimeline = false;
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

    // video ±10s: seek relative to the (extrapolated) current position
    private void SeekBy(int secs)
    {
        var s = Cur();
        TimeSpan pos; bool playing; DateTime at;
        lock (_lock) { pos = _pos; playing = _playing; at = _posAt; }
        if (s == null) return;
        var cur = playing ? pos + (DateTime.UtcNow - at) : pos;
        SeekTo(s, cur + TimeSpan.FromSeconds(secs));
    }

    // One place decides where a seek may land, because getting it wrong is invisible in one direction only.
    // The seekable range is [MinSeekTime or StartTime, MaxSeekTime or EndTime] - clamping to [0, EndTime]
    // instead is what made backward seeks silently do nothing on Windows' Media Player.
    private void SeekTo(GlobalSystemMediaTransportControlsSession s, TimeSpan target)
    {
        TimeSpan start, end, lo, hi;
        lock (_lock) { start = _start; end = _end; lo = _minSeek; hi = _maxSeek; }
        var floor = lo > TimeSpan.Zero ? lo : start;
        var ceil = hi > TimeSpan.Zero ? hi : end;
        if (target < floor) target = floor;
        if (ceil > TimeSpan.Zero && target > ceil) target = ceil;
        lock (_lock)
        {
            _seekPending = target;
            _seekAskedAt = DateTimeOffset.UtcNow;
            _seekTries = 0;      // the burst may still be going: NudgeSeek decides when to actually speak
            _pos = target;       // optimistic — the pill moves on the ask, not on the answer
            _posAt = DateTime.UtcNow;
            _version++;
        }
    }

    /// <summary>
    /// Getting a seek to actually happen, which took three measurements to understand.
    ///
    /// The player accepts an isolated seek immediately, and <b>silently drops</b> anything that arrives while
    /// it is still working on one — six taps 120ms apart produced one seek and five no-ops, with
    /// <c>TryChangePlaybackPositionAsync</c> returning true for every single one. So sending on every tap
    /// throws away precisely the position the user actually wanted: the last one.
    ///
    /// Hence: a tap moves a <i>target</i>, and this sends it immediately when nothing is in flight, or waits
    /// for the tapping to stop when it lands mid-burst. One retry covers a request the player swallowed while
    /// busy; after that it stops asking, because the second seek of a video that is simply not reporting its
    /// position is not a correction, it is another yank of the picture.
    /// </summary>
    private void NudgeSeek()
    {
        TimeSpan target, reported;
        DateTimeOffset asked, sent;
        int tries;
        lock (_lock)
        {
            if (_seekPending is not { } want || _seekBusy) return;
            target = want; reported = _reported; asked = _seekAskedAt; sent = _seekSentAt; tries = _seekTries;
        }

        if ((reported - target).Duration() <= TimeSpan.FromSeconds(1.5))
        {
            lock (_lock) _seekPending = null;      // arrived
            return;
        }

        var now = DateTimeOffset.UtcNow;
        switch (MediaTiming.NextSeekStep(tries, (now - asked).TotalMilliseconds, (now - sent).TotalMilliseconds))
        {
            case MediaTiming.SeekStep.Wait: return;
            case MediaTiming.SeekStep.GiveUp: lock (_lock) _seekPending = null; return;
        }

        lock (_lock) { _seekSentAt = now; _seekTries = tries + 1; _seekBusy = true; }
        var s = Cur();
        if (s is null) { lock (_lock) _seekBusy = false; return; }
        // awaited rather than fired and forgotten, so a second request is never issued on top of one that is
        // still outstanding
        _ = Task.Run(async () =>
        {
            try { await s.TryChangePlaybackPositionAsync(target.Ticks); } catch { }
            lock (_lock) _seekBusy = false;
        });
    }
    // dev hooks (--probe-seek): the rapid-tap case can only be measured through the path the buttons use
    internal void SeekByForProbe(int secs) => SeekBy(secs);
    internal TimeSpan PositionForProbe { get { lock (_lock) return _pos; } }

    // dev hook (--probe-media): the bar is a pure function of these numbers, and on screen a missing bar looks
    // identical whether the player never reported a duration, the widget refused the span it was handed, or a
    // track change is still being waited out. null = empty slot. Drives the same poll the draw path drives.
    internal string? ProbeLine()
    {
        PollTimeline();
        lock (_lock)
        {
            if (_title == null) return null;
            static string F(TimeSpan t) => t == TimeSpan.Zero ? "0" : t.ToString(@"h\:mm\:ss");
            var t = _title.Length > 22 ? _title[..22] : _title;
            // accent too: PillBar draws NOTHING for a white accent, so "no bar" and "no colour extracted from
            // the icon yet" are the same picture on screen
            return $"{t,-22} play={(_playing ? 1 : 0)} end={F(_end),-7} pos={F(_pos),-7} " +
                   $"rep={F(_reported),-7} prevEnd={F(_prevEnd),-7} seek={(_seekPending is { } p ? F(p) : "-"),-7} " +
                   $"ring={RingProgress:0.000} accent={(_accent == Fx.White ? "WHITE (no bar!)" : _accent.ToString())} " +
                   $"art={(_thumb == null ? "none" : "yes")}";
        }
    }

    private void SetVol(float f) { _meter.SetVolume(f); Bump(); }
    private void Mute() { _meter.ToggleMute(); Bump(); }
    private void Bump() { lock (_lock) { _version++; } }

    // the bar's fraction is of the SEEKABLE span, which is where it starts, not necessarily zero
    private void Seek(float f)
    {
        f = Math.Clamp(f, 0f, 1f);
        bool tg; TimeSpan start, end;
        lock (_lock) { tg = _tgTimeline; start = _start; end = _end; }
        if (end <= start) return;
        if (tg)
        {
            // optimistic: the bar lands where the finger left it, and telegram is asked through its own
            // slider (TelegramPlayer.SeekTo posts the click). the next 1s strip read corrects a miss.
            lock (_lock)
            {
                _pos = start + TimeSpan.FromTicks((long)(f * (end - start).Ticks));
                _posAt = DateTime.UtcNow;
                _version++;
            }
            _ = Task.Run(() => TelegramPlayer.SeekTo(f));
            return;
        }
        var s = Cur();
        if (s == null) return;
        SeekTo(s, start + TimeSpan.FromTicks((long)(f * (end - start).Ticks)));
    }

    private static (RectangleF bar, RectangleF mute) VolLayout(int w) => (new RectangleF(62, 178, 96, 20), new RectangleF(24, 172, 32, 32));
    private static RectangleF SeekRect(int w) { float tx = 180; return new RectangleF(tx, 108, w - tx - 26, 18); }

    private enum Btn { Prev, Play, Next, Back10, Fwd10, Cc }

    // ── speed ────────────────────────────────────────────────────────────────────────────────────────
    // This was a glass chip in the transport row that CYCLED on every click: four clicks to get from 1x to
    // 2x, no way to see what the choices were, and no way back except all the way round. Now it is a bare
    // label at the top right - no chip, no circle - that drops the whole list when you point at it.
    private static readonly double[] Rates = { 1.0, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0 };
    // seven rows have to fit between the label and the bottom of a 220-tall panel: 27 + 22 + 5 for the
    // handle and the gap leaves 158, and 7 x 21 + 2 x 5 is 157. The first attempt used 23px rows and the
    // last one hung out of the panel.
    private const float SpeedW = 44f, SpeedH = 22f, MenuW = 64f, ItemH = 21f, MenuPad = 5f;
    private bool _speedOpen;   // last frame's hover verdict; Buttons() reads it to know if the list is live
    private float _speedT;     // eased open amount

    private static RectangleF SpeedRect(int w) => new(w - 26f - SpeedW, 27f, SpeedW, SpeedH);
    private static RectangleF MenuRect(int w)
        => new(w - 26f - MenuW, 27f + SpeedH + 5f, MenuW, Rates.Length * ItemH + MenuPad * 2f);
    private static RectangleF ItemRect(int w, int i)
    {
        var m = MenuRect(w);
        return new RectangleF(m.X, m.Y + MenuPad + i * ItemH, m.Width, ItemH);
    }

    // ask the session for a rate outright. SMTC's TryChangePlaybackRateAsync is honoured by Films&TV, Media
    // Player and modern browsers; an app that ignores it just stays where it was (honest no-op, not a crash).
    private void SetRate(double r)
    {
        var s = Cur();
        if (s == null) return;
        lock (_lock) { _rate = r; }
        try { _ = s.TryChangePlaybackRateAsync(r); } catch { }
        Bump();
    }

    // transport row: music = prev/play/next; video = ±10s seek / play-pause + a speed chip, plus CC
    // only when the app has a known hotkey (no dead buttons). No Stop, no PiP (user removed both).
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
        return l.ToArray();   // speed lives at the top right now, not in this row
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

    // ── the second line ──────────────────────────────────────────────────────────────────────────────
    // A release filename is a sentence about the file: "Spy.2015.1080p.BluRay.Farsi.Dubbed.Film2Media" says
    // the year, the resolution, the source and who put it out. All of it was being thrown away, so the panel
    // showed a name and nothing else. These pull out the parts worth reading, and nothing that is not there:
    // no resolution is claimed unless the name says one, and no size unless the file was actually found.
    internal static string MetaLine(string? title, string? artist, string? size, string? resolution = null)
    {
        var parts = new List<string>(4);
        // the app's own artist field first: when a player fills it in, it beats anything guessed from a name
        if (!string.IsNullOrWhiteSpace(artist)) parts.Add(artist!.Trim());
        else if (Group(title) is { } grp) parts.Add(grp);
        // A resolution read from the stream beats one read off a filename, when the player will say. Given
        // raw ("1920x1080") it labels it here rather than trusting each caller to remember to: one of them
        // did not, and the panel would have shown the pixel dimensions where a "1080p" belongs.
        if ((HeightLabel(resolution) ?? resolution ?? Quality(title)) is { } q) parts.Add(q);
        if (Source(title) is { } src) parts.Add(src);
        if (!string.IsNullOrWhiteSpace(size)) parts.Add(size!);
        // a dot rather than an empty row: the row keeps its height, so nothing below it moves when a name
        // happens to carry no information
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

    /// <summary>"1920x1080" → "1080p", "3840x2160" → "4K". What a player reports, in the units people use.</summary>
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

    // whoever put it out, which in a release name is the last dotted word before the extension. Only when the
    // name really is dotted-release-shaped: a plain "Spy.mkv" or a sentence with a full stop in it must not
    // produce a word and present it as a publisher.
    internal static string? Group(string? title)
    {
        if (string.IsNullOrEmpty(title)) return null;
        var name = title;
        int dot = name.LastIndexOf('.');
        if (dot > 0 && name.Length - dot <= 5) name = name.Substring(0, dot);   // drop the extension
        var bits = name.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (bits.Length < 4) return null;                                       // not a release name
        var last = bits[^1].Trim();
        if (last.Length is < 3 or > 18) return null;
        foreach (var ch in last) if (!char.IsLetterOrDigit(ch) && ch != '-' && ch != '_') return null;
        // a trailing quality/codec token is not a publisher
        var lower = last.ToLowerInvariant();
        if (Quality(lower) != null || Source(lower) != null) return null;
        foreach (var noise in new[] { "x264", "x265", "hevc", "av1", "aac", "ac3", "dts", "mp3", "10bit" })
            if (lower == noise) return null;
        return last;
    }

    // the file size, once it is known. The lookup runs off the render path and bumps Version when it lands,
    // so the line fills itself in a frame or two after the track starts rather than blocking on a disk walk.
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
        // an open speed list is modal: it covers the seek bar and the transport row, and a click landing on
        // what is UNDER a menu is the oldest bug in menus
        if (_speedOpen && _speedT > 0.5f)
        {
            var items = new List<(RectangleF, Action<PointF>)>(Rates.Length);
            for (int i = 0; i < Rates.Length; i++)
            {
                double pick = Rates[i];   // captured per item, or every row would set the last rate
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
        if (seekOk2) list.Insert(0, (seek, pt => Seek((pt.X - seek.X) / seek.Width))); // no dead scrub when the app cannot seek (Telegram)
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

    // Best-effort per-app hotkey (needs the player focused). Unknown app -> 0 = no button.
    //
    // Browsers are back to 0, which takes the button away there. C toggles captions on YouTube and nowhere
    // else: on any other site, in any other tab, in a video that simply has no track, the key lands in the
    // page and does nothing at all - and the pill was still offering a subtitle button for it. A control
    // the underlying app cannot honour does not ship here; VLC and mpv genuinely toggle on V, so they keep
    // it and everything else loses it until there is a real way to ask.
    private static byte SubtitleKey(string app) =>
        app.Contains("vlc") || app.Contains("mpv") ? (byte)'V' : (byte)0;

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
        PollTimeline();
        string? title, artist; bool playing; TimeSpan pos, end, start, wall; DateTime posAt;
        lock (_lock)
        {
            title = _title; artist = _artist; playing = _playing;
            pos = _pos; end = _end; start = _start; posAt = _posAt;
            wall = _wallAt == default ? TimeSpan.MinValue
                 : _wallBase + (_playing ? DateTime.UtcNow - _wallAt : TimeSpan.Zero);
        }
        if (title == null) return;

        EnsureArt();
        float dt = Dt();   // once per frame: every ease below is a real time constant, not a per-frame step

        // not the fixed panel rect: mid-morph this is wherever the collapsed art has grown to, so the two
        // draws land on top of each other and the crossfade reads as one image
        var art = ArtRect(h);
        float artX = art.X, artY = art.Y, artSize = art.Width;
        // Radii deliberately larger than the panel: the glow is clipped to the pill anyway, and sizing it to
        // fit meant its falloff ended INSIDE the panel, leaving the far side unlit and a visible boundary
        // where it stopped. Overshooting puts the vanishing point outside the glass entirely.
        Fx.Glow(g, w, h, fade, artX + artSize / 2f, artY + artSize / 2f, w * 1.35f, h * 1.9f, 38, _accent);
        DrawArt(g, artX, artY, artSize, fade, ArtRadius(h));

        float tx = artX + artSize + 22, tw = w - tx - 26;
        bool rateOk0; lock (_lock) rateOk0 = _rateEnabled;
        bool showSpeed = rateOk0 && IsVideo();
        if (showSpeed) tw -= SpeedW + 12f;   // the title stops before the speed label rather than under it
        using var titleF = new Font("Segoe UI Semibold", 22f, GraphicsUnit.Pixel);
        using var bodyF = new Font("Segoe UI", 15f, GraphicsUnit.Pixel);
        using var timeF = new Font("Segoe UI", 12f, GraphicsUnit.Pixel);
        // The scroll follows the TITLE ROW, not the whole panel: hovering anywhere on the panel kept it
        // travelling long after the pointer had left the name, which reads as the title having a mind of its
        // own. Bound to its own row, moving off it returns the title to the start immediately.
        var titleRow = new RectangleF(tx, 34, tw, titleF.Height + 4);
        titleRow.Inflate(6f, 6f);
        bool onTitle = WidgetInput.Over && titleRow.Contains(WidgetInput.Mouse);
        using (var tb = new SolidBrush(Mul(White, fade)))
            _marquee.Draw(g, title, titleF, tb, tx, 34, tw, onTitle, dt);
        // Second line: whatever else is actually known about this thing - the artist or uploader if the app
        // gave one, otherwise the release group off the end of the filename, plus the quality tokens the name
        // carries and the file size when the player will say where the file is. A single "·" when none of
        // that exists, because a row that disappears makes the panel jump.
        using (var ab = new SolidBrush(Mul(Dim, fade)))
            DrawLine(g, MetaLine(title, artist, FileFacts()), bodyF, ab, tx, 66, tw);

        // seek bar (extrapolate while playing so it advances between events); the SHOWN fraction eases
        // toward the real one so seeks/track-changes glide instead of snapping ("نرم")
        var now = playing ? pos + (DateTime.UtcNow - posAt) : pos;
        // of the seekable span, so a track whose timeline does not start at zero still reads honestly
        float frac = end > start ? (float)Math.Clamp((now - start) / (end - start), 0, 1) : 0f;
        int epoch; lock (_lock) epoch = _trackEpoch;
        if (epoch != _shownEpoch) { _shownEpoch = epoch; _fracShown = frac; } // new track: snap to 0:00
        _fracShown = _fracShown < 0 ? frac : Ease(_fracShown, frac, dt, 0.10f);
        if (Math.Abs(frac - _fracShown) < 0.0004f) _fracShown = frac;

        // A 5px bar is a hard thing to drag, so it swells to three times that WHILE HELD — not on hover,
        // which would make it twitch every time the pointer crossed it — growing about its own centre so
        // the fill does not appear to jump, and the timestamps step down out of its way.
        //
        // While held, the fill follows the CURSOR instead of the player. That is what makes scrubbing
        // smooth: asking the player to seek on every frame of a drag means waiting for it to report a new
        // position each time, and the bar arrives in a series of jumps. The seek is committed once, on
        // release, and until then what you see is your own hand.
        var seek = SeekRect(w);
        var seekHit = seek; seekHit.Inflate(6f, 10f);
        bool onSeek = WidgetInput.Over && seekHit.Contains(WidgetInput.Mouse);
        // telegram seeks through its own strip rather than smtc, but only while that has been observed
        // to work - see TelegramPlayer.Seekable
        bool seekable; lock (_lock) seekable = _seekEnabled || (_tgTimeline && TelegramPlayer.Seekable);
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
        if (end > start)
        {
            if (_fracShown > 0) Fill(g, tx, by, tw * _fracShown, bh, Mul(White, fade));
        }
        else if (playing)
        {
            // Telegram (and some radio streams) report NO timeline at all - measured live: 12s of
            // playback with end=pos=0 and canSeek false, and unlike vlc there is no local api to ask
            // instead. The track used to sit empty at the 0:00 it would never leave, which reads as a
            // broken bar. Indeterminate is the honest shape: a soft sweep that says "moving" and
            // refuses to say "where". Non-interactive by construction - scrubbing is already gated on
            // _seekEnabled, and there are no timestamps to lie with.
            float ts2 = (Environment.TickCount64 % SweepMs) / (float)SweepMs;
            float sw = tw * 0.24f;
            float sx = tx - sw + (tw + sw) * ts2;
            var sst = g.Save();
            g.SetClip(new RectangleF(tx, by, tw, bh));
            var sweep = new RectangleF(sx, by - 1f, sw, bh + 2f);
            using (var lg = new LinearGradientBrush(sweep, Color.Transparent, Color.Transparent, LinearGradientMode.Horizontal))
            {
                var mid = Mul(Color.FromArgb(96, 255, 255, 255), fade);
                lg.InterpolationColors = new ColorBlend
                {
                    Colors = new[] { Color.FromArgb(0, mid), mid, Color.FromArgb(0, mid) },
                    Positions = new[] { 0f, 0.5f, 1f },
                };
                g.FillRectangle(lg, sweep);
            }
            g.Restore(sst);
        }
        if (end > TimeSpan.Zero)
        {
            using var eb = new SolidBrush(Mul(Dim, fade));
            float ty = barCy + bh / 2f + 3f;
            // while scrubbing the label previews where you are about to land, not where the player still is:
            // a bar dragged to 85% next to a stale "0:40" reads as a bug
            var span = end - start;
            var shown = _scrubbing ? span * _scrubFrac : now - start;
            g.DrawString(Fmt(shown), timeF, eb, tx, ty);
            var ts = g.MeasureString(Fmt(span), timeF);
            g.DrawString(Fmt(span), timeF, eb, tx + tw - ts.Width, ty);
        }
        else if (wall >= TimeSpan.Zero)
        {
            // no timeline to place this on (telegram) - but how long it has been playing IS our own
            // measurement (the stopwatch above). elapsed only, on the left where elapsed always sits;
            // no total and no fill, because the denominator genuinely does not exist here.
            using var eb = new SolidBrush(Mul(Dim, fade));
            g.DrawString(Fmt(wall), timeF, eb, tx, barCy + bh / 2f + 3f);
        }

        // volume (left column, under the art): soft glass mute chip + a bar that breathes on hover;
        // shown level eases toward the real one so click-to-set glides
        var (vbar, mute) = VolLayout(w);
        bool muted = _meter.Muted();
        float volNow = muted ? 0f : _meter.Volume();
        _volShown = _volShown < 0 ? volNow : Ease(_volShown, volNow, dt, 0.06f);
        if (Math.Abs(volNow - _volShown) < 0.002f) _volShown = volNow;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        // same press-and-drag as the seek bar: held, it grows and tracks the cursor
        var volHit = vbar; volHit.Inflate(8f, 10f);
        bool onVol = WidgetInput.Over && volHit.Contains(WidgetInput.Mouse);
        if (WidgetInput.Down && !_wasDown && onVol) _volScrubbing = true;
        if (_volScrubbing)
        {
            float f = Math.Clamp((WidgetInput.Mouse.X - vbar.X) / Math.Max(1f, vbar.Width), 0f, 1f);
            _volShown = f;
            // the bar tracks the cursor every frame, but the mixer only hears about real changes —
            // pushing an identical level 60 times a second is pure noise
            if (Math.Abs(f - _volSent) > 0.004f) { SetVol(f); _volSent = f; }
            if (!WidgetInput.Down) { SetVol(f); _volScrubbing = false; }
        }
        float vol = _volShown;
        _volHover = Ease(_volHover, _volScrubbing ? 1f : 0f, dt, 0.07f);
        float vt = _volHover;
        _wasDown = WidgetInput.Down;   // one edge per frame, shared by both bars
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
            bool isPlay = kind == Btn.Play;
            string glyph = isPlay ? Glyph(playing ? 0xE769 : 0xE768)
                : kind == Btn.Prev ? Glyph(0xE892) : Glyph(0xE893);
            DrawGlyphSoft(g, rr, glyph, (isPlay ? 22f : 17f) * sc, a, isPlay && !playing ? 1.5f : 0f);
        }

        DrawSpeed(g, w, fade, dt, showSpeed);   // last: the open list sits over the bar and the transport row
    }

    // The label is bare - no chip, no ring - because it is a menu handle, not a button: the thing you press
    // is the row you pick out of the list. Hovering either the label or the list keeps it open, so the
    // pointer can travel from one to the other without the list closing underneath it.
    private void DrawSpeed(Graphics g, int w, float fade, float dt, bool show)
    {
        if (!show)
        {
            _speedOpen = false;
            _speedT = Ease(_speedT, 0f, dt, 0.13f);
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
            _speedT = Ease(_speedT, over ? 1f : 0f, dt, over ? 0.075f : 0.13f);
        }

        double rate; lock (_lock) rate = _rate;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        if (show)
        {
            // the handle brightens as the list opens, and carries a chevron so it reads as "there is more
            // under here" rather than as a number someone forgot to make interactive
            using var lf = new Font("Segoe UI Semibold", 13f, GraphicsUnit.Pixel);
            using var lb = new SolidBrush(Mul(White, fade * (0.62f + 0.38f * _speedT)));
            using var sf = new StringFormat(StringFormat.GenericTypographic)
            { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center };
            var textBox = new RectangleF(label.X, label.Y, label.Width - 11f, label.Height);
            g.DrawString(RateText(rate), lf, lb, textBox, sf);
            // the chevron turns over as the list opens rather than swapping to a different glyph: the arms
            // lerp through flat, which is the same "one shape, eased" idea as everything else here
            float cx = label.Right - 5f, cy = label.Y + label.Height / 2f + 1f;
            float armY = -1.6f + 3.2f * _speedT, tipY = 1.9f - 3.8f * _speedT;
            using var cp = new Pen(Mul(White, fade * (0.45f + 0.4f * _speedT)), 1.4f)
            { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLines(cp, new[] { new PointF(cx - 3.5f, cy + armY), new PointF(cx, cy + tipY),
                                    new PointF(cx + 3.5f, cy + armY) });
        }

        if (_speedT <= 0.01f) return;

        // ── the list ─────────────────────────────────────────────────────────────────────────────────────
        // First attempt was an opaque dark box with a 1px white border: a Win32 context menu sitting on top of
        // a frosted panel, which is what "doesn't match the theme" meant. This one is built out of the same
        // three things the rest of the panel is: a translucent white wash over the glass rather than a solid
        // fill, an accent glow underneath so it belongs to the artwork's colour, and an edge that is brightest
        // at the top and fades away down the sides - which is what a lit glass edge actually does, and why a
        // uniform hairline read as hard.
        float a = fade * _speedT;
        var m = menu;
        m.Offset(0f, -9f * (1f - _speedT));   // rises into place instead of appearing at full size

        Fx.Glow(g, (int)(m.Right + 30f), (int)(m.Bottom + 30f), a * 0.5f,
            m.X + m.Width / 2f, m.Y + m.Height * 0.35f, m.Width * 2.6f, m.Height * 1.5f, 26,
            _accent == White ? Color.FromArgb(120, 150, 255) : _accent);

        using (var shade = new SolidBrush(Color.FromArgb((int)(120 * a), 10, 10, 13)))
        using (var sp = Fx.Rounded(m, 15f))
            g.FillPath(shade, sp);
        using (var wash = new SolidBrush(Color.FromArgb((int)(26 * a), 255, 255, 255)))
        using (var wp = Fx.Rounded(m, 15f))
            g.FillPath(wash, wp);
        // the lit edge: a vertical gradient down the stroke, bright where the light is and gone by the bottom
        using (var edge = new LinearGradientBrush(
                   new RectangleF(m.X, m.Y - 1f, m.Width, m.Height + 2f),
                   Color.FromArgb((int)(74 * a), 255, 255, 255),
                   Color.FromArgb((int)(10 * a), 255, 255, 255), 90f))
        using (var pen = new Pen(edge, 1f))
        using (var ep = Fx.Rounded(m, 15f))
            g.DrawPath(pen, ep);

        using var itemF = new Font("Segoe UI", 13f, GraphicsUnit.Pixel);
        using var isf = new StringFormat(StringFormat.GenericTypographic)
        { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        for (int i = 0; i < Rates.Length; i++)
        {
            // each row arrives a beat after the one above it: a list that unrolls reads as soft, and the same
            // seven rows appearing together read as a box being switched on
            float ti = Math.Clamp((_speedT - i * 0.05f) / 0.55f, 0f, 1f);
            ti = 1f - MathF.Pow(1f - ti, 3);
            if (ti <= 0.01f) continue;
            var r = ItemRect(w, i);
            r.Offset(0f, -9f * (1f - _speedT) + 5f * (1f - ti));

            bool cur = Math.Abs(Rates[i] - rate) < 0.01;
            bool hov = WidgetInput.Over && r.Contains(WidgetInput.Mouse);
            // eased, not binary: the first version snapped the highlight on and off under the pointer
            _itemHover[i] = Ease(_itemHover[i], hov ? 1f : 0f, dt, 0.055f);
            float ih = _itemHover[i];
            float ia = a * ti;

            var pill = new RectangleF(r.X + 4f, r.Y + 1f, r.Width - 8f, r.Height - 2f);
            if (cur)
                using (var cb = new SolidBrush(Fx.Alpha(_accent == White ? White : _accent, ia * 0.20f)))
                using (var cp = Fx.Rounded(pill, pill.Height / 2f))
                    g.FillPath(cb, cp);
            if (ih > 0.01f)
                using (var hb = new SolidBrush(Color.FromArgb((int)(30 * ia * ih), 255, 255, 255)))
                using (var hp = Fx.Rounded(pill, pill.Height / 2f))
                    g.FillPath(hb, hp);

            using (var tb2 = new SolidBrush(Mul(White, ia * (0.58f + 0.40f * MathF.Max(cur ? 1f : 0f, ih)))))
                g.DrawString(RateText(Rates[i]), itemF, tb2, r, isf);
        }
    }

    private readonly float[] _itemHover = new float[8];

    private static string Glyph(int codepoint) => ((char)codepoint).ToString();

    private readonly float[] _btnHover = new float[8];
    private float _volHover, _seekHover;
    private bool _wasDown, _scrubbing, _volScrubbing;
    private float _scrubFrac, _volSent = -1f;
    private float _volShown = -1f, _fracShown = -1f; // eased displayed values (smooth bars)
    private int _trackEpoch, _shownEpoch;            // bumped per track change → seek bar snaps, no glide

    // Every ease here used to be a fixed fraction per FRAME, which makes the speed depend on the frame rate:
    // the pill drops to a lower fps tier when little is happening, and at that tier a 0.18-per-frame lerp
    // advances the seek bar in visible steps. Measured wall-clock dt turns them into real time constants.
    private long _lastTick;
    private float Dt()
    {
        long now = Environment.TickCount64;
        float dt = _lastTick == 0 ? 1f / 60f : (now - _lastTick) / 1000f;
        _lastTick = now;
        return Math.Clamp(dt, 1f / 240f, 0.1f);
    }

    // frame-rate independent approach: converges with time constant tau regardless of fps
    private static float Ease(float shown, float target, float dt, float tau)
        => shown + (target - shown) * (1f - MathF.Exp(-dt / tau));

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

    // The album art is ONE image growing, not two crossfading. DrawCollapsed sized itself off h alone, so
    // while the pill grew its art raced past the panel's 132 all the way to 170 and sat 17px to the left
    // of it, while DrawContent drew a second copy at the fixed panel rect - two squares of different size
    // and place in the same frame, which is what read as a sudden switch rather than a scale. Both ends of
    // the morph are known here, so both draws take the same lerped rect and the crossfade then happens
    // between rectangles that agree, which is the whole trick.
    private const float MorphFromH = 40f, MorphToH = 220f;
    private const float MiniArtX = 9f, MiniArtY = 7f, MiniArtSize = MorphFromH - 14f;
    private const float PanelArtX = 26f, PanelArtY = 26f, PanelArtSize = 132f;

    internal static float MorphT(float h) => Math.Clamp((h - MorphFromH) / (MorphToH - MorphFromH), 0f, 1f);

    // backlight behind the cover. This was rx = w*0.7 drawn straight onto the pill AFTER PillBar - an
    // accent wash reaching ~80% of the way across, which on a playing track stuck out past the bar's
    // wavefront as a paler copy of it: the reported "two bars, a strong one behind and a faint one
    // ahead". Same disease the Intersect fix cured inside PillBar, but this glow is drawn on top of the
    // bar so no clip in PillBar could touch it. It hugs the art now; a halo around the cover reads as
    // backlight, not as a second bar. Shared with --render-bar so the filmstrip shows the same pill the
    // user sees (the hook missing this glow is exactly how "two bars" shipped unseen).
    internal static void ArtGlow(Graphics g, int w, int h, float fade, Color accent)
    {
        var a = ArtRect(h);
        Fx.Glow(g, w, h, fade, a.X + a.Width / 2f, h / 2f, a.Width * 2.1f, h * 1.7f, 34, accent);
    }

    internal static RectangleF ArtRect(float h)
    {
        float t = MorphT(h);
        float size = MiniArtSize + (PanelArtSize - MiniArtSize) * t;
        return new RectangleF(
            MiniArtX + (PanelArtX - MiniArtX) * t,
            MiniArtY + (PanelArtY - MiniArtY) * t,
            size, size);
    }

    // The pill's art is nearly a squircle, the panel's is a soft-cornered square. Lerped on the same t, so
    // the corners open out as it grows instead of snapping at the handover.
    internal static float ArtRadius(float h)
    {
        const float miniR = MiniArtSize * 0.28f;
        return miniR + (14f - miniR) * MorphT(h);
    }

    internal const int FlipMs = 560;
    internal const int SweepMs = 2800;   // one lap of the no-timeline indeterminate sweep
    private Bitmap? _prevArt;      // the outgoing cover, alive only for the flip
    private long _artHash;         // ThumbHash of the bytes behind _art; 0 = no art decoded

    // FNV-1a over the thumb bytes, 0 reserved for "no art" - identity of the cover as bytes, which is
    // the only identity the commit side has too. never returns 0 for real content, so a real cover can
    // always be told from the empty state.
    internal static long ThumbHash(byte[]? b)
    {
        if (b is not { Length: > 0 }) return 0;
        unchecked
        {
            long h = -3750763034362895579L; // FNV offset basis
            foreach (var x in b) { h ^= x; h *= 1099511628211L; }
            return h == 0 ? 1 : h;
        }
    }

    private long _flipAt = long.MinValue;
    // el must be checked from BOTH sides: an unset clock (long.MinValue) makes the subtraction
    // overflow negative, and a negative el walked straight into FlipPose as t << 0, whose smoothstep
    // fed cos() a huge angle -> NaN -> ScaleTransform threw. found by --render-widget the first time
    // a session with no art hit the hash early-out, which leaves the clock unset by design.
    private bool Flipping => (Environment.TickCount64 - _flipAt) is >= 0 and < FlipMs;

    // the card's pose at clock t in 0..1: horizontal squeeze |cos(angle)| (1 -> 0 -> 1) and which face is
    // showing. The angle rides a smoothstep rather than t itself - at constant angular speed the card
    // slams into the turn and out of it, which is what "it doesn't turn smoothly" looked like; eased, it
    // winds up, turns, and settles. smoothstep(0.5) = 0.5, so the face still swaps exactly at the
    // narrowest instant. The floor keeps GDI+ from a degenerate zero-width transform at the crossing.
    internal static (float sx, bool front) FlipPose(float t)
    {
        t = Math.Clamp(t, 0f, 1f);   // t outside the clock is a caller bug, but NaN out of cos() is worse
        float e = t * t * (3f - 2f * t);
        return (Math.Max(MathF.Abs(MathF.Cos(e * MathF.PI)), 0.001f), t < 0.5f);
    }

    private void DrawArt(Graphics g, float x, float y, float size, float fade, float radius = 14f)
    {
        long el = Environment.TickCount64 - _flipAt;
        if (el is >= 0 and < FlipMs)
        {
            // a card turning on its vertical axis, faked with a horizontal squeeze: |cos| runs 1 -> 0 -> 1
            // and the face swaps at the zero crossing, so the new cover really does come in "from behind"
            // the old one. The transform scales path AND texture together (TextureBrush lives in world
            // coordinates), which is what keeps the squeezed cover looking turned rather than cropped.
            var (sx, front) = FlipPose(el / (float)FlipMs);
            var st = g.Save();
            g.TranslateTransform(x + size / 2f, 0f);
            g.ScaleTransform(sx, 1f);
            g.TranslateTransform(-(x + size / 2f), 0f);
            if (front)
            {
                if (_prevArt != null)
                {
                    using var path = Rounded(new RectangleF(x, y, size, size), radius);
                    CoverFill(g, _prevArt, x, y, size, path, fade);
                }
            }
            else DrawArtFace(g, x, y, size, fade, radius);
            g.Restore(st);
            return;
        }
        DrawArtFace(g, x, y, size, fade, radius);
    }

    private void DrawArtFace(Graphics g, float x, float y, float size, float fade, float radius)
    {
        using var path = Rounded(new RectangleF(x, y, size, size), radius);
        // album art if the track has any; otherwise the source app's icon (podcasts, some videos and
        // radio streams ship no thumbnail \u2014 the app icon reads far better than a generic music glyph)
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

    // An animated cover has to keep asking for frames even while the track is paused, or the GIF freezes and
    // then jumps whenever something else happens to trigger a repaint. _animatedArt is set on the UI thread
    // by EnsureArt and only read here, so a volatile bool is the whole synchronisation it needs.
    private volatile bool _animatedArt;

    // the title marquee, shared with the vlc panel (see Marquee.cs for the scroll contract)
    private readonly Marquee _marquee = new();

    public bool Animating
    {
        // Flipping too: a track change on a PAUSED session (skip while paused, or a chased cover landing
        // after a pause) still has to play its half-second of card flip, and without this it froze on
        // whatever squeezed frame the last incidental redraw happened to catch.
        get { lock (_lock) { return _title != null && (_playing || _animatedArt || _marquee.Scrolling || Flipping); } }
    }

    // the flip is the one moment on this widget the eye tracks a moving edge, same as the shell's morph -
    // it gets morph treatment from the controller (full cadence, no collapsed frame-skip) for its 560ms
    public bool Sprinting => Flipping;

    // ring around the collapsed album-art circle = playback position (like the download %). Only when a
    // real duration exists (live streams have none → no ring). Extrapolated each frame so it glides.
    public Color? Ring
    {
        get
        {
            PokeTelegram();
            lock (_lock) return _title != null && _end > TimeSpan.Zero ? _accent : (Color?)null;
        }
    }

    // The ring is often the ONLY place telegram's timeline is drawn - the pill collapsed, or the media
    // widget sitting in the strip while something else is primary. In that state neither DrawContent nor
    // DrawCollapsed runs, so nothing called PollTimeline, so nothing poked the uia reader: it parked
    // itself 15s after the last poke and the widget withdrew the timeline 5s later. That is why the bar
    // only ever filled in while the panel was open and froze as soon as it was not.
    //
    // Not an unconditional poll, because EaseRings reads Ring for EVERY widget on EVERY frame. Gated on
    // "this is the telegram case" so the cross-process UIA cost only exists while it is being asked for;
    // PollTimeline's own 200ms throttle bounds the rest.
    private void PokeTelegram()
    {
        bool want;
        lock (_lock)
            want = _title != null && (_end <= _start || _tgTimeline)
                && _appId?.Contains("telegram", StringComparison.OrdinalIgnoreCase) == true;
        if (want) PollTimeline();
    }

    public float RingProgress
    {
        get
        {
            PokeTelegram();
            TimeSpan pos, end, start; bool playing; DateTime at; string? t;
            lock (_lock) { pos = _pos; end = _end; start = _start; playing = _playing; at = _posAt; t = _title; }
            if (t == null || end <= start) return -1f;
            var now = playing ? pos + (DateTime.UtcNow - at) : pos;
            return (float)Math.Clamp((now - start) / (end - start), 0, 1);
        }
    }

    // The collapsed bar's own frame clock, separate because Dt() is consumed by the panel and a frame that
    // draws both would hand the second caller a dt of zero.
    private long _pillTick;
    private float PillDt()
    {
        long now = Environment.TickCount64;
        float dt = _pillTick == 0 ? 1f / 60f : Math.Clamp((now - _pillTick) / 1000f, 1f / 240f, 0.1f);
        _pillTick = now;
        return dt;
    }

    private static readonly Color NeutralBar = Color.FromArgb(255, 222, 226, 232);

    // The bar's colour comes from the album art, so every track change repainted the whole pill background
    // between two frames. The target still comes straight from the art; only the pixels lag. The first cover
    // snaps - washing in from the White sentinel would be a flash of grey, which is not a transition, it is a
    // different glitch.
    private Color _accentShown = Fx.White;
    private bool _accentInit;
    // ...and the bar itself fades rather than appearing the instant a duration lands and vanishing the instant
    // a track change clears one. _lastProg holds the outgoing fill in place while it fades, so a track change
    // cross-fades instead of blinking off and on.
    private float _barIn;
    private float _lastProg = -1f;

    private static Color LerpColor(Color from, Color to, float dt, float tau)
    {
        float k = 1f - MathF.Exp(-dt / tau);
        return Color.FromArgb(
            (int)MathF.Round(from.A + (to.A - from.A) * k),
            (int)MathF.Round(from.R + (to.R - from.R) * k),
            (int)MathF.Round(from.G + (to.G - from.G) * k),
            (int)MathF.Round(from.B + (to.B - from.B) * k));
    }

    // Extrapolation already carries the fill between reports; this is for the moment a report lands and
    // disagrees - without it every correction is a visible step sideways. A track change or a real jump is not
    // eased: a new song's bar belongs at the new song's position, not gliding back from where the last ended.
    private float _pillFrac = -1f;
    private int _pillEpoch = -1;
    private float PillFrac(float frac, float dt)
    {
        if (frac < 0f) { _pillFrac = -1f; return frac; }
        int epoch; lock (_lock) epoch = _trackEpoch;
        // 0.02 of a three-minute song is under four seconds, so ordinary corrections - the ones that land every
        // time the player finally reports - were being SNAPPED, and a snap is exactly what "not smooth" looks
        // like. Only a track change or a real seek jumps now; everything else glides.
        if (epoch != _pillEpoch || _pillFrac < 0f || Math.Abs(frac - _pillFrac) > 0.08f)
        {
            _pillEpoch = epoch;
            return _pillFrac = frac;
        }
        _pillFrac = Ease(_pillFrac, frac, dt, 0.14f);
        if (Math.Abs(frac - _pillFrac) < 0.0004f) _pillFrac = frac;
        return _pillFrac;
    }

    // Collapsed pill = album art on the left + an audio equalizer on the right (Dynamic-Island style).
    public void DrawCollapsed(Graphics g, int w, int h, float fade)
    {
        PollTimeline();
        string? title; bool playing;
        lock (_lock) { title = _title; playing = _playing; }
        if (title == null) return;
        // the marquee lives on the open panel's title row; a panel closed mid-scroll must not leave the
        // flag latched, or a paused long-titled track keeps requesting frames with nothing moving
        _marquee.Park();
        EnsureArt();
        var art = ArtRect(h);
        float sz = art.Width, x = art.X, y = art.Y;
        float dt = PillDt();
        // -1 is also what a switched-off timeline looks like from here, which is the point: every
        // downstream branch already knows how to draw a pill with no bar, because a live stream has
        // none either.
        float prog = Halo.Settings.SettingsStore.On("media.progress")
            ? PillFrac(RingProgress, dt) : -1f;

        // Backmost: how far through the video you are, as the pill's own background — the same "the pill IS
        // the bar" language the agent pills use for a spent usage window, and a better use of it here, since
        // a video's progress is the number you actually keep glancing at.
        // alive: the wavefront breathes while it is playing and stands still when it is not
        // 0.34 was a whisper meant for an agent pill's spent-usage window; this one is the thing you glance
        // at the pill FOR, and at 0.34 it was barely there. 0.5 also brings in the sheen and the lip, which
        // is what makes it read as one lit body rather than a flat block with a bright edge.
        // A greyscale or monochrome cover makes AccentOf give up and answer Fx.White, and PillBar draws
        // NOTHING for White - its other callers use that to mean "no colour worth painting with". Measured
        // live: a playing track with real art, end and position all correct, ring at 0.219, and no bar on the
        // pill at all. For this widget the bar IS the content, so White falls back to a neutral light grey -
        // deliberately not Fx.White itself, which is the sentinel that means "skip".
        var accentTarget = _accent == Fx.White ? NeutralBar : _accent;
        if (!_accentInit) { _accentShown = accentTarget; _accentInit = true; }
        else _accentShown = LerpColor(_accentShown, accentTarget, dt, 0.30f);

        if (prog >= 0f) _lastProg = prog;
        _barIn = Ease(_barIn, prog >= 0f ? 1f : 0f, dt, 0.20f);
        if (_barIn > 0.01f && _lastProg >= 0f)
            // No breath on playback. The pulse means "something is working and you cannot see how far along it
        // is" - which is a download's problem, not a track's. A track already shows its position by the
        // bar moving, so a pulse on top of it is decoration competing with the one honest signal there is.
        Fx.PillBar(g, w, h, fade * _barIn, _lastProg, _accentShown, 0.5f);
        else if (playing && RingProgress < 0f && Halo.Settings.SettingsStore.On("media.progress"))
        {
            // telegram-style sessions report no position at all (measured: end=pos=0 for the whole
            // track - see the panel's sweep). the pill bar's language for that is a wash that travels
            // without a wavefront: "playing, position unknown", never a bar stuck at zero.
            float ts3 = (Environment.TickCount64 % SweepMs) / (float)SweepMs;
            float sw3 = w * 0.30f;
            var sweep = new RectangleF(-sw3 + (w + sw3) * ts3, 0, sw3, h);
            var sst = g.Save();
            using (var pp = Fx.PillPath(w, h, h / 2f))
                g.SetClip(pp);
            using (var lg = new LinearGradientBrush(sweep, Color.Transparent, Color.Transparent, LinearGradientMode.Horizontal))
            {
                var mid = Mul(Color.FromArgb(64, _accentShown), fade);
                lg.InterpolationColors = new ColorBlend
                {
                    Colors = new[] { Color.FromArgb(0, mid), mid, Color.FromArgb(0, mid) },
                    Positions = new[] { 0f, 0.5f, 1f },
                };
                g.FillRectangle(lg, sweep);
            }
            g.Restore(sst);
        }
        ArtGlow(g, w, h, fade, _accent);
        DrawArt(g, x, y, sz, fade, ArtRadius(h));

        // No ring around the art. The pill's own background already carries this exact number, and a second
        // reading of it two pixels away is decoration, not information.
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
        // Centred on the glyph's own INK, not by StringFormat. StringFormat centres the line box and the
        // advance width, and for an icon font neither describes where that particular glyph's ink is - the
        // fallback art glyph sat visibly high and left of the tile it is supposed to fill. Same conclusion
        // LocalBadge and the copy pill each reached; it lives in Fx now so it is reached once.
        Fx.GlyphCentred(g, r, glyph, f, b);
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

// The four rules that decide when a seek is spoken and which timeline reports are believed. Pure and out here
// for the same reason NotchVisibility is: from the outside every one of them fails as a *rendering* bug - a bar
// that leaps forward at the start of a track and falls back, a seek that keeps re-landing for seconds after the
// tap, a fill frozen mid-video, a pill with no bar at all - so the logic has to be testable without a player.
internal static class MediaTiming
{
    internal const int BurstMs = 320;      // quiet required before a burst's final target is spoken
    internal const int FreshMs = 1200;     // nothing sent this recently → not a burst, speak at once
    internal const int RetryMs = 700;
    internal const int GiveUpMs = 2500;
    internal const int MaxTries = 2;
    internal const int LeftoverMs = 2000;  // how long a track change tolerates the old track's timeline

    internal enum SeekStep { Wait, Send, GiveUp }

    internal static SeekStep NextSeekStep(int tries, double msSinceAsked, double msSinceSent)
    {
        // an isolated ask goes out immediately; only one landing on the heels of a send waits for the tapping
        // to stop, which is the case the debounce exists for
        if (tries == 0)
            return msSinceSent < FreshMs && msSinceAsked < BurstMs ? SeekStep.Wait : SeekStep.Send;
        // one retry covers a request the player swallowed while busy; past that, asking again is not a
        // correction, it is another yank of the picture
        if (tries >= MaxTries || msSinceAsked > GiveUpMs) return SeekStep.GiveUp;
        return msSinceSent < RetryMs ? SeekStep.Wait : SeekStep.Send;
    }

    // for a moment after a track change the player still serves the OUTGOING track's timeline, recognisable by
    // still carrying its duration. Time-boxed, so a playlist of equal-length tracks can't stall the bar forever.
    internal static bool IsLeftover(TimeSpan incomingEnd, TimeSpan prevEnd, double msSinceTrack)
        => prevEnd > TimeSpan.Zero && incomingEnd == prevEnd && msSinceTrack < LeftoverMs;

    // a zeroed span is the app declining to answer, not news about a track already measured. A live stream
    // reports zero from the very first update, never has a span to keep, and correctly gets no bar.
    internal static bool IsBlank(TimeSpan inStart, TimeSpan inEnd, TimeSpan knownStart, TimeSpan knownEnd)
        => inEnd <= inStart && knownEnd > knownStart;

    // an identical reading while playing is the player repeating itself, not time standing still: re-stamping
    // the clock against it restarts extrapolation every poll and freezes the bar. The report that confirms a
    // seek is always stamped - that one has to restart the clock at the position actually landed on.
    internal static bool ShouldRestamp(bool repeated, bool playing, bool confirming)
        => !repeated || !playing || confirming;
}
