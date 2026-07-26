using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Halo.Widgets;

// Live download progress straight out of Chromium's in-progress store.
//
// This exists because Edge gives nothing anywhere else. Its History row is written only when a download
// ENDS (measured: max(id) did not move through 22s and 85MB of downloading), and it never renames its
// partial away from "Unconfirmed 12345.crdownload" — so there is no name, no total, and the file on disk
// cannot even be attributed to a particular download. One such blob was seen growing 72MB to 92MB across
// three separate test downloads.
//
// What Chromium does keep current is the InProgressDownloadManager's own store, a LevelDB under
// <profile>\shared_proto_db. Measured live: state went 0 while running, received_bytes moved from 0 to
// 37,304,924 across twelve seconds, and total_bytes read 104,857,600 the whole time. So everything the
// pill wants is in there — it just has to be read without a LevelDB library or a protobuf compiler, since
// this project adds no packages.
//
// Only the write-ahead .log files are read, never the compacted .ldb tables. That is deliberate and it is
// also sufficient: an in-progress download is precisely the record that has just been written and not yet
// compacted, which is the same reason the History reader has to snapshot the -wal.
internal static class ChromiumProgress
{
    internal readonly record struct Entry(string Name, long Received, long Total);

    // state is carried while scanning so the final revision decides; 0 = in progress, 1 = complete,
    // 2 = interrupted. Read off the live store, since there is no .proto in this repo.
    private readonly record struct Row(string Name, long Received, long Total, long State);

    private const double CacheSeconds = 2.0;
    private static readonly object _lock = new();
    private static Entry[] _cache = Array.Empty<Entry>();
    private static DateTime _cacheAt = DateTime.MinValue;

    public static Entry[] Live()
    {
        lock (_lock)
            if ((DateTime.UtcNow - _cacheAt).TotalSeconds < CacheSeconds) return _cache;

        // A write-ahead log keeps EVERY revision, so one download appears once per progress write — the
        // first run of this returned twenty rows for a single transfer, received climbing through all of
        // them. Keyed by guid with last-write-wins, and a delete or an empty value drops it.
        var live = new Dictionary<string, Row>(StringComparer.Ordinal);
        foreach (var log in Logs())
        {
            try { ReadLog(log, live); }
            catch { }   // a probe failing is normal; degrade silently
        }
        var found = new List<Entry>();
        foreach (var r in live.Values)
            if (r.State == 0 && r.Total > 0 && r.Name.Length > 0)
                found.Add(new Entry(r.Name, r.Received, r.Total));
        var arr = found.ToArray();
        lock (_lock) { _cache = arr; _cacheAt = DateTime.UtcNow; }
        return arr;
    }

    // The in-progress entry that best explains a partial file of this size. Edge's own file name says
    // nothing, so the size is the only honest link between the two — and it is a good one, because
    // received_bytes is what produced those bytes. A generous window absorbs the store lagging the disk.
    public static Entry? For(long fileBytes)
    {
        var live = Live();
        // One download running means no ambiguity to resolve, and this is the common case. Insisting on a
        // size match here made the pill flicker back to a bare "Downloading" whenever the store lagged the
        // disk — seen at 88MB on disk against 55MB recorded, a 33MB gap that a tight window rejected.
        if (live.Length == 1) return live[0].Total > 0 ? live[0] : null;

        Entry? best = null;
        long bestGap = long.MaxValue;
        foreach (var e in live)
        {
            if (e.Total <= 0) continue;
            long gap = Math.Abs(e.Received - fileBytes);
            if (gap < bestGap) { bestGap = gap; best = e; }
        }
        return best;
    }

    private static IEnumerable<string> Logs()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var roots = new[]
        {
            Path.Combine(local, @"Microsoft\Edge\User Data"),
            Path.Combine(local, @"Google\Chrome\User Data"),
            Path.Combine(local, @"BraveSoftware\Brave-Browser\User Data"),
            Path.Combine(local, @"Vivaldi\User Data"),
            Path.Combine(roaming, @"Opera Software\Opera Stable"),
        };
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            string[] subs;
            try { subs = Directory.GetDirectories(root); } catch { continue; }
            foreach (var sub in subs)
            {
                string name = Path.GetFileName(sub);
                if (!name.Equals("Default", StringComparison.OrdinalIgnoreCase) &&
                    !name.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase)) continue;
                string db = Path.Combine(sub, "shared_proto_db");
                if (!Directory.Exists(db)) continue;
                string[] logs;
                try { logs = Directory.GetFiles(db, "*.log"); } catch { continue; }
                foreach (var l in logs) yield return l;
            }
        }
    }

    // ── LevelDB write-ahead log ───────────────────────────────────────────────────────────────────────
    // 32KB blocks; each record is crc(4) length(2) type(1) payload, and a record too big for the rest of
    // its block is split FIRST/MIDDLE/LAST and has to be stitched back together. The CRC is not checked:
    // a corrupt tail would fail to parse as protobuf anyway and is discarded there.
    private const int Block = 32768;

    private static void ReadLog(string path, Dictionary<string, Row> into)
    {
        byte[] data;
        try
        {
            // Copy first: the browser holds the log open, and reading it while it is being appended to
            // gives a torn tail. FileShare.ReadWrite is what makes the copy possible at all.
            using var src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var ms = new MemoryStream();
            src.CopyTo(ms);
            data = ms.ToArray();
        }
        catch { return; }

        var frag = new List<byte>();
        int pos = 0;
        while (pos + 7 <= data.Length)
        {
            int inBlock = pos % Block;
            if (Block - inBlock < 7) { pos += Block - inBlock; continue; }
            int len = data[pos + 4] | (data[pos + 5] << 8);
            byte type = data[pos + 6];
            pos += 7;
            if (len < 0 || pos + len > data.Length) break;
            if (type == 0 && len == 0) { pos += Block - (pos % Block); continue; }

            switch (type)
            {
                case 1: Batch(data, pos, len, into); break;                 // FULL
                case 2: frag.Clear(); Add(frag, data, pos, len); break;     // FIRST
                case 3: Add(frag, data, pos, len); break;                   // MIDDLE
                case 4:
                    Add(frag, data, pos, len);
                    var whole = frag.ToArray();
                    Batch(whole, 0, whole.Length, into);
                    frag.Clear();
                    break;
            }
            pos += len;
        }
    }

    private static void Add(List<byte> to, byte[] src, int off, int len)
    {
        for (int i = 0; i < len; i++) to.Add(src[off + i]);
    }

    // WriteBatch: sequence(8) count(4), then per entry a tag byte (1 = put, 0 = delete) and
    // varint-length-prefixed key, plus the same for the value on a put.
    private static void Batch(byte[] b, int off, int len, Dictionary<string, Row> into)
    {
        int i = off + 12, end = off + len;
        while (i < end)
        {
            byte tag = b[i++];
            if (!Len(b, ref i, end, out int kl)) return;
            int kOff = i; i += kl;
            if (i > end) return;
            if (tag != 1) { if (kl >= 12) into.Remove(Encoding.ASCII.GetString(b, kOff, kl)); continue; }
            if (!Len(b, ref i, end, out int vl)) return;
            int vOff = i; i += vl;
            if (i > end) return;

            // keys look like "21_download,<guid>"; anything else in this shared store is someone else's
            if (kl < 12 || Encoding.ASCII.GetString(b, kOff, 11) != "21_download") continue;
            string key = Encoding.ASCII.GetString(b, kOff, kl);
            if (vl == 0) { into.Remove(key); continue; }   // deleted, or cleared on completion
            Parse(b, vOff, vl, into, key);
        }
    }

    private static bool Len(byte[] b, ref int i, int end, out int len)
    {
        len = 0;
        if (!Varint(b, ref i, end, out ulong v) || v > (ulong)(end - i)) return false;
        len = (int)v;
        return true;
    }

    // ── just enough protobuf ──────────────────────────────────────────────────────────────────────────
    // DownloadDBEntry { DownloadInfo f1 { ... InProgressInfo f4 { string url = 1; int64 total = 10;
    // int64 received = 15; int32 state = 21; } } } — field numbers read off the live store, since there is
    // no .proto here. state 0 is in progress; 1 complete, 2 interrupted.
    private static void Parse(byte[] b, int off, int len, Dictionary<string, Row> into, string key)
    {
        if (!Sub(b, off, len, 1, out int iOff, out int iLen)) return;          // DownloadInfo
        if (!Sub(b, iOff, iLen, 4, out int pOff, out int pLen)) return;        // InProgressInfo

        string url = ""; long total = 0, recv = 0, state = -1;
        int i = pOff, end = pOff + pLen;
        while (i < end)
        {
            if (!Varint(b, ref i, end, out ulong tag)) return;
            int field = (int)(tag >> 3), wire = (int)(tag & 7);
            if (wire == 0)
            {
                if (!Varint(b, ref i, end, out ulong v)) return;
                if (field == 10) total = (long)v;
                else if (field == 15) recv = (long)v;
                else if (field == 21) state = (long)v;
            }
            else if (wire == 2)
            {
                if (!Len(b, ref i, end, out int l)) return;
                if (field == 1) url = Encoding.UTF8.GetString(b, i, l);
                i += l;
            }
            else if (wire == 5) i += 4;
            else if (wire == 1) i += 8;
            else return;
        }

        // every revision is recorded, not just the running ones: a later "complete" or "interrupted"
        // revision has to be able to overwrite an earlier "in progress" one
        into[key] = new Row(NameFromUrl(url), recv, total, state);
    }

    private static bool Sub(byte[] b, int off, int len, int field, out int subOff, out int subLen)
    {
        subOff = subLen = 0;
        int i = off, end = off + len;
        while (i < end)
        {
            if (!Varint(b, ref i, end, out ulong key)) return false;
            int f = (int)(key >> 3), wire = (int)(key & 7);
            if (wire == 2)
            {
                if (!Len(b, ref i, end, out int l)) return false;
                if (f == field) { subOff = i; subLen = l; return true; }
                i += l;
            }
            else if (wire == 0) { if (!Varint(b, ref i, end, out _)) return false; }
            else if (wire == 5) i += 4;
            else if (wire == 1) i += 8;
            else return false;
        }
        return false;
    }

    private static bool Varint(byte[] b, ref int i, int end, out ulong value)
    {
        value = 0;
        int shift = 0;
        while (i < end && shift <= 63)
        {
            byte x = b[i++];
            value |= (ulong)(x & 0x7f) << shift;
            if ((x & 0x80) == 0) return true;
            shift += 7;
        }
        return false;
    }

    // the last path segment of the URL, percent-decoded — the store carries no file name of its own
    private static string NameFromUrl(string url)
    {
        try
        {
            if (url.Length == 0 || url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return "";
            int q = url.IndexOfAny(new[] { '?', '#' });
            string path = q >= 0 ? url.Substring(0, q) : url;
            int slash = path.LastIndexOf('/');
            string name = slash >= 0 ? path.Substring(slash + 1) : path;
            name = Uri.UnescapeDataString(name);
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name.Length > 80 ? name.Substring(0, 80) : name;
        }
        catch { return ""; }
    }
}
