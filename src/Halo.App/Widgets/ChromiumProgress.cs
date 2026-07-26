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
    internal readonly record struct Entry(string Name, long Received, long Total, string CurrentPath);

    // state is carried while scanning so the final revision decides; 0 = in progress, 1 = complete,
    // 2 = interrupted. Read off the live store, since there is no .proto in this repo.
    private readonly record struct Row(string Name, long Received, long Total, long State, string CurrentPath);

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
                found.Add(new Entry(r.Name, r.Received, r.Total, r.CurrentPath));
        var arr = found.ToArray();
        lock (_lock) { _cache = arr; _cacheAt = DateTime.UtcNow; }
        return arr;
    }

    // The in-progress entry that belongs to a particular partial file.
    //
    // The store records the partial's own path (current_path), so this is an exact identity and not a
    // guess. It used to be a guess: Edge's "Unconfirmed 12345.crdownload" carries no name, so the closest
    // received_bytes was the only link, and with two Edge downloads of similar size in flight that could
    // name the wrong one. The size match is still the fallback, because current_path is empty for the first
    // moment of a download, before Chromium has settled on a file.
    public static Entry? For(string? partialPath, long fileBytes)
    {
        var live = Live();
        if (live.Length == 0) return null;

        if (!string.IsNullOrEmpty(partialPath))
            foreach (var e in live)
                if (e.CurrentPath.Length > 0 &&
                    string.Equals(e.CurrentPath, partialPath, StringComparison.OrdinalIgnoreCase))
                    return e;

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

        Blocks(data, (key, b, off, len) => Parse(b, off, len, into, key), key => into.Remove(key));
    }

    // The block/batch walk, shared by the reader and the field dump so the two cannot drift.
    private static void Blocks(byte[] data, Action<string, byte[], int, int> onPut, Action<string> onDelete)
    {
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
                case 1: Batch(data, pos, len, onPut, onDelete); break;      // FULL
                case 2: frag.Clear(); Add(frag, data, pos, len); break;     // FIRST
                case 3: Add(frag, data, pos, len); break;                   // MIDDLE
                case 4:
                    Add(frag, data, pos, len);
                    var whole = frag.ToArray();
                    Batch(whole, 0, whole.Length, onPut, onDelete);
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
    private static void Batch(byte[] b, int off, int len,
        Action<string, byte[], int, int> onPut, Action<string> onDelete)
    {
        int i = off + 12, end = off + len;
        while (i < end)
        {
            byte tag = b[i++];
            if (!Len(b, ref i, end, out int kl)) return;
            int kOff = i; i += kl;
            if (i > end) return;
            if (tag != 1) { if (kl >= 12) onDelete(Encoding.ASCII.GetString(b, kOff, kl)); continue; }
            if (!Len(b, ref i, end, out int vl)) return;
            int vOff = i; i += vl;
            if (i > end) return;

            // keys look like "21_download,<guid>"; anything else in this shared store is someone else's
            if (kl < 12 || Encoding.ASCII.GetString(b, kOff, 11) != "21_download") continue;
            string key = Encoding.ASCII.GetString(b, kOff, kl);
            if (vl == 0) { onDelete(key); continue; }   // deleted, or cleared on completion
            onPut(key, b, vOff, vl);
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
    // FilePath current_path = 13; FilePath target_path = 14; int64 received = 15; int32 state = 21; } } }
    // — field numbers read off the live store with --probe-downloads, since there is no .proto here.
    // state 0 is in progress; 1 complete, 2 interrupted.
    private static void Parse(byte[] b, int off, int len, Dictionary<string, Row> into, string key)
    {
        if (!Sub(b, off, len, 1, out int iOff, out int iLen)) return;          // DownloadInfo
        if (!Sub(b, iOff, iLen, 4, out int pOff, out int pLen)) return;        // InProgressInfo

        string url = "", current = "", target = ""; long total = 0, recv = 0, state = -1;
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
                else if (field == 13) current = PickledPath(b, i, l);
                else if (field == 14) target = PickledPath(b, i, l);
                i += l;
            }
            else if (wire == 5) i += 4;
            else if (wire == 1) i += 8;
            else return;
        }

        // the saved name beats the URL's last segment: a Content-Disposition header renames the file, and
        // then the URL segment is not what the browser's own downloads row says either
        string name = "";
        try { if (target.Length > 0) name = Path.GetFileName(target); } catch { }
        if (name.Length == 0) name = NameFromUrl(url);

        // every revision is recorded, not just the running ones: a later "complete" or "interrupted"
        // revision has to be able to overwrite an earlier "in progress" one
        into[key] = new Row(name, recv, total, state, current);
    }

    // A base::FilePath is Pickle-serialised, not a plain string: uint32 payload size, uint32 character
    // count, then that many UTF-16 chars (native wide chars on Windows). Read as UTF-8 it looks like binary,
    // which is exactly how the path sat here unnoticed while the code guessed by file size instead.
    private static string PickledPath(byte[] b, int off, int len)
    {
        try
        {
            if (len < 8) return "";
            int chars = b[off + 4] | (b[off + 5] << 8) | (b[off + 6] << 16) | (b[off + 7] << 24);
            if (chars <= 0 || 8 + chars * 2 > len) return "";
            return Encoding.Unicode.GetString(b, off + 8, chars * 2);
        }
        catch { return ""; }
    }

    // dev-only: every field of every in-progress record, so a field number can be READ off the live store
    // instead of guessed. There is no .proto in this repo, and guessing produced a name that was right only
    // until a Content-Disposition header renamed the file.
    internal static string DumpFields()
    {
        var sb = new StringBuilder();
        foreach (var log in Logs())
        {
            byte[] data;
            try
            {
                using var src = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var ms = new MemoryStream();
                src.CopyTo(ms);
                data = ms.ToArray();
            }
            catch { continue; }

            var recs = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            Blocks(data,
                (key, b, off, len) =>
                {
                    var copy = new byte[len];
                    Array.Copy(b, off, copy, 0, len);
                    recs[key] = copy;
                },
                key => recs.Remove(key));
            foreach (var kv in recs)
            {
                sb.AppendLine($"-- {kv.Key}");
                if (!Sub(kv.Value, 0, kv.Value.Length, 1, out int iOff, out int iLen)) continue;
                Fields(sb, kv.Value, iOff, iLen, "f1");
                if (Sub(kv.Value, iOff, iLen, 4, out int pOff, out int pLen))
                    Fields(sb, kv.Value, pOff, pLen, "f1.f4");
            }
        }
        return sb.ToString();
    }

    private static void Fields(StringBuilder sb, byte[] b, int off, int len, string prefix)
    {
        int i = off, end = off + len;
        while (i < end)
        {
            if (!Varint(b, ref i, end, out ulong tag)) return;
            int field = (int)(tag >> 3), wire = (int)(tag & 7);
            if (wire == 0)
            {
                if (!Varint(b, ref i, end, out ulong v)) return;
                sb.AppendLine($"   {prefix}.{field} varint = {v}");
            }
            else if (wire == 2)
            {
                if (!Len(b, ref i, end, out int l)) return;
                string s = Encoding.UTF8.GetString(b, i, l);
                bool text = true;
                foreach (char c in s) if (char.IsControl(c) && c != '\t') { text = false; break; }
                // a pickled base::FilePath reads as binary under UTF-8, which is how the path fields sat here
                // unnoticed; show it decoded, then hex for anything still unrecognised
                if (!text)
                {
                    string p = PickledPath(b, i, l);
                    if (p.Length > 0) { s = "FilePath " + p; text = true; }
                }
                if (!text)
                {
                    var hex = new StringBuilder();
                    for (int k = 0; k < Math.Min(l, 48); k++) hex.Append(b[i + k].ToString("x2")).Append(' ');
                    s = "<binary> " + hex;
                }
                sb.AppendLine($"   {prefix}.{field} bytes[{l}] = {s}");
                i += l;
            }
            else if (wire == 5) i += 4;
            else if (wire == 1) i += 8;
            else return;
        }
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
