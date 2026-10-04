using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Stellar.RaidManager;

// ── Punctuate Mark Presets — compact share code (pure codec, no Unity / no plugin services) ─────────────────────
//
// A preset travels between machines as a single copy-pasteable line:  "RMP1." + Base64Url(payload), no padding.
// Everything is tuned for SHORTNESS — players paste these into chat / Discord:
//
//   payload = flags(1 byte; bit0 = deflated) + body            (body deflated only when that is actually shorter)
//   body    = name            varint byteLen + UTF-8
//             stepCount       varint — REAL steps only (the blank Steps[0] "Start" anchor is implied, not sent)
//             per step:       comment (varint byteLen + UTF-8), markCount (varint),
//                             per mark: slot (varint), then X, Y, Z
//
// Coordinates are quantized to 1 cm (q = round(v * 100)) and each axis is written as a zigzag varint DELTA from the
// same axis of the PREVIOUSLY WRITTEN mark — across the whole stream, starting at 0. Marks in one step sit a few
// metres apart and consecutive steps tend to reuse near-identical spots, so after the first mark almost every delta
// is 1-3 bytes instead of 12 bytes of floats; deflate then squeezes the repeated slot/comment bytes.
//
// Decode is STRICT and never throws: anything malformed (bad prefix / base64 / flags, truncated or overlong varint,
// trailing bytes, out-of-range counts/slots/lengths, invalid UTF-8, a zip bomb) comes back as a MarkCodeError.
// Types here are plain DTOs; mapping to the plugin's private MarkPreset/MarkStep/MarkPos is in Plugin.Marks.Share.cs.
internal sealed class MarkCodePreset
{
    public string Name { get; set; } = "";
    public List<MarkCodeStep> Steps { get; set; } = new();   // REAL steps only (no Start anchor)
}

internal sealed class MarkCodeStep
{
    public string Comment { get; set; } = "";
    public List<MarkCodeMark> Marks { get; set; } = new();
}

internal readonly record struct MarkCodeMark(int Slot, float X, float Y, float Z);

internal enum MarkCodeError
{
    None,
    Empty,          // blank input
    BadPrefix,      // not an "RMP1." code (or a future version we can't read)
    BadEncoding,    // not valid Base64Url
    BadFlags,       // unknown flag bits / empty payload
    BadCompression, // inflate failed, or the inflated body exceeds the size cap (zip-bomb guard)
    Truncated,      // ran out of bytes mid-field (incl. an overlong varint)
    TrailingData,   // bytes left over after the last step
    BadText,        // invalid UTF-8
    BadName,        // empty or > MaxNameChars
    TooManySteps,
    TooManyMarks,
    BadSlot,
    CommentTooLong,
    BadCoordinate,  // non-finite or absurdly far (encode) / out of range (decode)
}

internal static class MarkPresetCode
{
    public const string Prefix = "RMP1.";

    public const int MaxNameChars = 64;
    public const int MaxSteps = 64;
    public const int MaxMarksPerStep = 6;
    public const int MaxCommentChars = 200;
    public const int MinSlot = 1, MaxSlot = 6;

    private const byte FlagDeflated = 0x01;
    private const int MaxInflatedBytes = 64 * 1024;   // zip-bomb guard; a max-size preset body is ~15 KB
    // |q| cap (1e9 cm = 10,000 km). Keeps every delta/zigzag far inside long range on both sides; real world
    // coords are a few thousand metres at most.
    private const long MaxAbsQ = 1_000_000_000L;

    // Strict decoder: invalid UTF-8 throws (caught → BadText) instead of silently becoming U+FFFD.
    private static readonly UTF8Encoding Utf8Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    // ── Encode ───────────────────────────────────────────────────────────────────────────────────────────────────
    // Name / comments longer than the decode limits are TRUNCATED (so every exported code is importable); counts
    // and slots can't be meaningfully trimmed, so those fail with an error instead.
    public static bool TryEncode(MarkCodePreset preset, out string code, out MarkCodeError error)
    {
        code = "";
        error = MarkCodeError.None;
        if (preset == null) { error = MarkCodeError.BadName; return false; }

        string name = Clip((preset.Name ?? "").Trim(), MaxNameChars);
        if (name.Length == 0) { error = MarkCodeError.BadName; return false; }
        var steps = preset.Steps ?? new List<MarkCodeStep>();
        if (steps.Count > MaxSteps) { error = MarkCodeError.TooManySteps; return false; }

        var body = new MemoryStream();
        WriteString(body, name);
        WriteVarint(body, (ulong)steps.Count);

        long px = 0, py = 0, pz = 0;   // previous written mark, per axis (stream-wide delta chain)
        foreach (var st in steps)
        {
            var marks = st?.Marks ?? new List<MarkCodeMark>();
            if (marks.Count > MaxMarksPerStep) { error = MarkCodeError.TooManyMarks; return false; }
            WriteString(body, Clip(st?.Comment ?? "", MaxCommentChars));
            WriteVarint(body, (ulong)marks.Count);
            foreach (var m in marks)
            {
                if (m.Slot < MinSlot || m.Slot > MaxSlot) { error = MarkCodeError.BadSlot; return false; }
                if (!TryQuantize(m.X, out long qx) || !TryQuantize(m.Y, out long qy) || !TryQuantize(m.Z, out long qz))
                { error = MarkCodeError.BadCoordinate; return false; }
                WriteVarint(body, (ulong)m.Slot);
                WriteVarint(body, ZigZag(qx - px));
                WriteVarint(body, ZigZag(qy - py));
                WriteVarint(body, ZigZag(qz - pz));
                px = qx; py = qy; pz = qz;
            }
        }

        byte[] raw = body.ToArray();
        byte[] deflated = Deflate(raw);
        bool useDeflate = deflated.Length < raw.Length;
        byte[] chosen = useDeflate ? deflated : raw;

        var payload = new byte[chosen.Length + 1];
        payload[0] = useDeflate ? FlagDeflated : (byte)0;
        Buffer.BlockCopy(chosen, 0, payload, 1, chosen.Length);

        code = Prefix + ToBase64Url(payload);
        return true;
    }

    // ── Decode ───────────────────────────────────────────────────────────────────────────────────────────────────
    public static bool TryDecode(string? text, out MarkCodePreset preset, out MarkCodeError error)
    {
        preset = new MarkCodePreset();
        try
        {
            error = DecodeCore(text, preset);
        }
        catch
        {
            error = MarkCodeError.BadEncoding;   // belt-and-braces: decode must never throw out to the caller
        }
        if (error != MarkCodeError.None) preset = new MarkCodePreset();
        return error == MarkCodeError.None;
    }

    private static MarkCodeError DecodeCore(string? text, MarkCodePreset preset)
    {
        // Chat / Discord pastes can wrap a long line or carry stray spaces — base64url never contains whitespace,
        // so drop ALL of it (not just the ends) before looking at the prefix.
        var sb = new StringBuilder(text?.Length ?? 0);
        if (text != null)
            foreach (char c in text)
                if (!char.IsWhiteSpace(c)) sb.Append(c);
        string s = sb.ToString();
        if (s.Length == 0) return MarkCodeError.Empty;
        if (!s.StartsWith(Prefix, StringComparison.Ordinal)) return MarkCodeError.BadPrefix;

        if (!TryFromBase64Url(s.Substring(Prefix.Length), out byte[] payload)) return MarkCodeError.BadEncoding;
        if (payload.Length < 1) return MarkCodeError.BadFlags;
        byte flags = payload[0];
        if ((flags & ~FlagDeflated) != 0) return MarkCodeError.BadFlags;

        byte[] body;
        if ((flags & FlagDeflated) != 0)
        {
            if (!TryInflate(payload, 1, payload.Length - 1, out body)) return MarkCodeError.BadCompression;
        }
        else
        {
            body = new byte[payload.Length - 1];
            Buffer.BlockCopy(payload, 1, body, 0, body.Length);
        }

        var r = new Reader(body);
        var e = r.ReadString(MaxNameChars, out string name);
        if (e != MarkCodeError.None) return e == MarkCodeError.CommentTooLong ? MarkCodeError.BadName : e;
        if (name.Trim().Length == 0) return MarkCodeError.BadName;
        preset.Name = name;

        if (!r.ReadVarint(out ulong stepCount)) return MarkCodeError.Truncated;
        if (stepCount > MaxSteps) return MarkCodeError.TooManySteps;

        long px = 0, py = 0, pz = 0;
        for (ulong i = 0; i < stepCount; i++)
        {
            var step = new MarkCodeStep();
            e = r.ReadString(MaxCommentChars, out string comment);
            if (e != MarkCodeError.None) return e;
            step.Comment = comment;

            if (!r.ReadVarint(out ulong markCount)) return MarkCodeError.Truncated;
            if (markCount > MaxMarksPerStep) return MarkCodeError.TooManyMarks;
            for (ulong k = 0; k < markCount; k++)
            {
                if (!r.ReadVarint(out ulong slot)) return MarkCodeError.Truncated;
                if (slot < MinSlot || slot > MaxSlot) return MarkCodeError.BadSlot;
                if (!r.ReadVarint(out ulong dx) || !r.ReadVarint(out ulong dy) || !r.ReadVarint(out ulong dz))
                    return MarkCodeError.Truncated;
                if (!TryAdvance(ref px, dx) || !TryAdvance(ref py, dy) || !TryAdvance(ref pz, dz))
                    return MarkCodeError.BadCoordinate;
                step.Marks.Add(new MarkCodeMark((int)slot, px / 100f, py / 100f, pz / 100f));
            }
            preset.Steps.Add(step);
        }

        return r.AtEnd ? MarkCodeError.None : MarkCodeError.TrailingData;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────────────────────
    // Truncate to maxChars UTF-16 units without splitting a surrogate pair (a lone high surrogate is invalid UTF-8
    // and would make our own decoder reject the code).
    private static string Clip(string s, int maxChars)
    {
        if (s.Length <= maxChars) return s;
        int n = maxChars;
        if (char.IsHighSurrogate(s[n - 1])) n--;
        return s.Substring(0, n);
    }

    private static bool TryQuantize(float v, out long q)
    {
        q = 0;
        if (!float.IsFinite(v)) return false;
        double d = Math.Round((double)v * 100.0);   // double: v*100 in float would lose the cm on large coords
        if (Math.Abs(d) > MaxAbsQ) return false;
        q = (long)d;
        return true;
    }

    // Apply a zigzag delta to an axis accumulator, rejecting anything that leaves the sane coordinate range (this
    // also keeps a hostile stream from overflowing the long).
    private static bool TryAdvance(ref long acc, ulong zz)
    {
        if (zz > (ulong)(4 * MaxAbsQ)) return false;   // |delta| <= 2*MaxAbsQ, so its zigzag <= 4*MaxAbsQ
        long next = acc + UnZigZag(zz);
        if (next > MaxAbsQ || next < -MaxAbsQ) return false;
        acc = next;
        return true;
    }

    private static ulong ZigZag(long v) => (ulong)((v << 1) ^ (v >> 63));
    private static long UnZigZag(ulong v) => (long)(v >> 1) ^ -(long)(v & 1);

    private static void WriteVarint(Stream s, ulong v)
    {
        while (v >= 0x80) { s.WriteByte((byte)(v | 0x80)); v >>= 7; }
        s.WriteByte((byte)v);
    }

    private static void WriteString(Stream s, string str)
    {
        byte[] b = Encoding.UTF8.GetBytes(str);
        WriteVarint(s, (ulong)b.Length);
        s.Write(b, 0, b.Length);
    }

    private static byte[] Deflate(byte[] raw)
    {
        var ms = new MemoryStream();
        using (var ds = new DeflateStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
            ds.Write(raw, 0, raw.Length);
        return ms.ToArray();
    }

    private static bool TryInflate(byte[] src, int offset, int count, out byte[] result)
    {
        result = Array.Empty<byte>();
        try
        {
            using var ds = new DeflateStream(new MemoryStream(src, offset, count, writable: false), CompressionMode.Decompress);
            var outMs = new MemoryStream();
            var buf = new byte[4096];
            int n;
            while ((n = ds.Read(buf, 0, buf.Length)) > 0)
            {
                if (outMs.Length + n > MaxInflatedBytes) return false;   // zip-bomb guard: stop reading, reject
                outMs.Write(buf, 0, n);
            }
            result = outMs.ToArray();
            return true;
        }
        catch { return false; }
    }

    private static string ToBase64Url(byte[] data)
        => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool TryFromBase64Url(string s, out byte[] data)
    {
        data = Array.Empty<byte>();
        if (s.Length == 0 || s.Length % 4 == 1) return false;   // %4==1 can never be valid base64
        foreach (char c in s)
            if (!((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '_'))
                return false;   // also rejects '=' padding / standard '+' '/' — our codes never contain them
        string b64 = s.Replace('-', '+').Replace('_', '/');
        b64 += new string('=', (4 - b64.Length % 4) % 4);
        try { data = Convert.FromBase64String(b64); return true; }
        catch (FormatException) { return false; }
    }

    // Bounds-checked cursor over the body. Every read returns false/error instead of throwing on short input.
    private sealed class Reader
    {
        private readonly byte[] _b;
        private int _pos;
        public Reader(byte[] b) { _b = b; }
        public bool AtEnd => _pos == _b.Length;

        public bool ReadVarint(out ulong value)
        {
            value = 0;
            for (int shift = 0; shift < 64; shift += 7)
            {
                if (_pos >= _b.Length) return false;
                byte b = _b[_pos++];
                if (shift == 63 && b > 1) return false;   // 10th byte may only carry the top bit — overflow otherwise
                value |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return true;
            }
            return false;   // more than 10 continuation bytes = overlong / garbage
        }

        // Length-prefixed UTF-8. maxChars is checked on the decoded string; the byte length is pre-capped at 4x
        // (max UTF-8 bytes per UTF-16 unit is 3, surrogate pairs 4 per 2) so a huge length can't allocate first.
        public MarkCodeError ReadString(int maxChars, out string value)
        {
            value = "";
            if (!ReadVarint(out ulong len)) return MarkCodeError.Truncated;
            if (len > (ulong)(maxChars * 4)) return MarkCodeError.CommentTooLong;
            if (len > (ulong)(_b.Length - _pos)) return MarkCodeError.Truncated;
            try { value = Utf8Strict.GetString(_b, _pos, (int)len); }
            catch (ArgumentException) { return MarkCodeError.BadText; }
            _pos += (int)len;
            return value.Length > maxChars ? MarkCodeError.CommentTooLong : MarkCodeError.None;
        }
    }
}
