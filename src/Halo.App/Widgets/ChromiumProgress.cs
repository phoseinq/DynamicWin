using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Halo.Widgets;

internal static class ChromiumProgress
{
    internal readonly record struct Entry(string Name, long Received, long Total, string CurrentPath);

    private readonly record struct Row(string Name, long Received, long Total, long State, string CurrentPath);

    private const double CacheSeconds = 2.0;
    private static readonly object _lock = new();
    private static Entry[] _cache = Array.Empty<Entry>();
    private static DateTime _cacheAt = DateTime.MinValue;

    public static Entry[] Live()
    {
        lock (_lock)
            if ((DateTime.UtcNow - _cacheAt).TotalSeconds < CacheSeconds) return _cache;

        var live = new Dictionary<string, Row>(StringComparer.Ordinal);
        foreach (var log in Logs())
        {
            try { ReadLog(log, live); }
            catch { }
        }
        var found = new List<Entry>();
        foreach (var r in live.Values)
            if (r.State == 0 && r.Total > 0 && r.Name.Length > 0)
                found.Add(new Entry(r.Name, r.Received, r.Total, r.CurrentPath));
        var arr = found.ToArray();
        lock (_lock) { _cache = arr; _cacheAt = DateTime.UtcNow; }
        return arr;
    }

    public static Entry? For(string? partialPath, long fileBytes)
    {
        var live = Live();
        if (live.Length == 0) return null;

        if (!string.IsNullOrEmpty(partialPath))
            foreach (var e in live)
                if (e.CurrentPath.Length > 0 &&
                    string.Equals(e.CurrentPath, partialPath, StringComparison.OrdinalIgnoreCase))
                    return e;

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

    private const int Block = 32768;

    private static void ReadLog(string path, Dictionary<string, Row> into)
    {
        byte[] data;
        try
        {

            using var src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var ms = new MemoryStream();
            src.CopyTo(ms);
            data = ms.ToArray();
        }
        catch { return; }

        Blocks(data, (key, b, off, len) => Parse(b, off, len, into, key), key => into.Remove(key));
    }

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
                case 1: Batch(data, pos, len, onPut, onDelete); break;
                case 2: frag.Clear(); Add(frag, data, pos, len); break;
                case 3: Add(frag, data, pos, len); break;
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

            if (kl < 12 || Encoding.ASCII.GetString(b, kOff, 11) != "21_download") continue;
            string key = Encoding.ASCII.GetString(b, kOff, kl);
            if (vl == 0) { onDelete(key); continue; }
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

    private static void Parse(byte[] b, int off, int len, Dictionary<string, Row> into, string key)
    {
        if (!Sub(b, off, len, 1, out int iOff, out int iLen)) return;
        if (!Sub(b, iOff, iLen, 4, out int pOff, out int pLen)) return;

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

        string name = "";
        try { if (target.Length > 0) name = Path.GetFileName(target); } catch { }
        if (name.Length == 0) name = NameFromUrl(url);

        into[key] = new Row(name, recv, total, state, current);
    }

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
