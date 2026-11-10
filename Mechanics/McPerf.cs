using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using Stellar.Abstractions.Diagnostics;

namespace Stellar.RaidManager;

// Per-feature timing for the Mechanic Callouts tick paths (raid perf, 2026-11-10). Two sinks, both inert unless armed:
//  - HitchProbe buckets ("rm:mech.<step>") → a slow frame's [Hitch] line names WHICH mechanic step ran inside it
//    (the 11-10 raid log only had the whole-plugin "stellar.raidmanager=…" figure).
//  - A 10 s accumulator (calls / mean / max ms + managed bytes allocated by the step) → one [MechPerf] line every 10 s,
//    for the STEADY cost a hitch line can't show. The alloc column matters most: in the 11-10 raid a managed gen-0 GC
//    fell inside 139 of the 159 RaidManager frames over 10 ms — the tick's garbage, not its CPU, made the spikes.
// Armed only by the framework diagnostics flag (HitchProbe.IsEnabled) — RaidManager has no diagnostics option of its
// own (Experiment also arms it from "Diagnostics only"). Disarmed cost: one static bool read per section.
// Main thread only, except EndHook (Harmony prefixes — Interlocked, no HitchProbe).
internal static class McPerf
{
    public const int Poll = 0, Scan = 1, Casts = 2, Rows = 3, Rules = 4, Occur = 5, Minimap = 6, MapFrame = 7,
                     MapPaint = 8, BuffHook = 9;
    private const int SlotCount = 10;
    private static readonly string[] Names =
        { "poll", "scan", "casts", "rows", "rules", "occur", "minimap", "mapFrame", "mapPaint", "buffHook" };
    private static readonly string[] Buckets =   // fixed strings: no per-call allocation (HitchProbe asks for stable names)
    {
        "rm:mech.poll", "rm:mech.scan", "rm:mech.casts", "rm:mech.rows", "rm:mech.rules", "rm:mech.occur",
        "rm:mech.minimap", "rm:mech.mapFrame", "rm:mech.mapPaint", "rm:mech.buffHook",
    };

    public static bool On => HitchProbe.IsEnabled;

    private static readonly long[] _ticks = new long[SlotCount], _max = new long[SlotCount], _bytes = new long[SlotCount];
    private static readonly int[]  _calls = new int[SlotCount];
    private static long _windowStart;
    private static int  _gc0, _gc1;
    private static readonly StringBuilder _sb = new(512);
    private static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;

    public readonly struct Mark
    {
        public readonly long Ts, Bytes;
        public Mark(long ts, long bytes) { Ts = ts; Bytes = bytes; }
    }

    public static Mark Begin() =>
        On ? new Mark(Stopwatch.GetTimestamp(), GC.GetAllocatedBytesForCurrentThread()) : default;

    public static void End(int slot, Mark m)
    {
        if (m.Ts == 0) return;
        long dt = Stopwatch.GetTimestamp() - m.Ts;
        _ticks[slot] += dt;
        _calls[slot]++;
        if (dt > _max[slot]) _max[slot] = dt;
        _bytes[slot] += GC.GetAllocatedBytesForCurrentThread() - m.Bytes;
        HitchProbe.End(Buckets[slot], m.Ts);
    }

    // Harmony prefixes may run off the main thread: thread-safe adds only (max is best-effort), no HitchProbe bucket.
    public static void EndHook(Mark m)
    {
        if (m.Ts == 0) return;
        long dt = Stopwatch.GetTimestamp() - m.Ts;
        Interlocked.Add(ref _ticks[BuffHook], dt);
        Interlocked.Increment(ref _calls[BuffHook]);
        Interlocked.Add(ref _bytes[BuffHook], GC.GetAllocatedBytesForCurrentThread() - m.Bytes);
        if (dt > _max[BuffHook]) _max[BuffHook] = dt;
    }

    /// <summary>Called from the tracker's 100 ms poll: emits one [MechPerf] line per 10 s while armed.</summary>
    public static void MaybeFlush(Action<string> log)
    {
        if (!On) { _windowStart = 0; return; }
        long now = Environment.TickCount64;
        if (_windowStart == 0) { Reset(now); return; }
        if (now - _windowStart < 10_000) return;

        double ms = 1000.0 / Stopwatch.Frequency;
        _sb.Clear().Append("[MechPerf] ").Append(((now - _windowStart) / 1000.0).ToString("0.0", Inv)).Append("s:");
        for (int i = 0; i < SlotCount; i++)
        {
            if (_calls[i] == 0) continue;
            _sb.Append(' ').Append(Names[i]).Append(" n=").Append(_calls[i])
               .Append(" avg=").Append((_ticks[i] * ms / _calls[i]).ToString("0.00", Inv))
               .Append(" max=").Append((_max[i] * ms).ToString("0.0", Inv))
               .Append(" tot=").Append((_ticks[i] * ms).ToString("0", Inv))
               .Append("ms alloc=").Append(_bytes[i] / 1024).Append("KB |");
        }
        _sb.Append(" gc0+").Append(GC.CollectionCount(0) - _gc0).Append(" gc1+").Append(GC.CollectionCount(1) - _gc1);
        log(_sb.ToString());
        Reset(now);
    }

    private static void Reset(long now)
    {
        _windowStart = now;
        Array.Clear(_ticks); Array.Clear(_max); Array.Clear(_bytes); Array.Clear(_calls);
        _gc0 = GC.CollectionCount(0); _gc1 = GC.CollectionCount(1);
    }
}
