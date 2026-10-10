using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Stellar.RaidManager;

/// <summary>
/// Local-speaker chime for the mechanic on-me alert (NOT team voice). Ported from StellarExperimentPlugin's
/// MechAudioPlayer, cut down to the built-in generated chime (no sound files, no decoder, no voice queue).
/// <para><b>API choice:</b> winmm <c>PlaySound(SND_MEMORY | SND_SYNC | SND_NODEFAULT)</c> on a dedicated background
/// thread, fed an in-memory 16-bit PCM WAV. No Unity/IL2CPP objects (an <c>AudioClip</c>/<c>AudioSource</c> route
/// would need interop arrays + a GameObject + main-thread calls), no COM, no device handles to leak; it plays through
/// the default output in shared mode alongside the game, focus or not. SYNC on our own thread = the game thread never
/// waits, and chimes never overlap (one at a time) — Mechanic-Callouts.md "Local audio playback".</para>
/// <para><b>Queue policy:</b> at most one chime waiting (a burst of alerts collapses into the newest request), and
/// plays start at least <see cref="MinGapMs"/> apart. Volume is baked into the WAV image per request.</para>
/// </summary>
internal sealed class MechAudioPlayer : IDisposable
{
    [DllImport("winmm.dll", SetLastError = true)]
    private static extern bool PlaySound(byte[]? pszSound, IntPtr hmod, uint fdwSound);
    private const uint SndSync = 0x0, SndNoDefault = 0x2, SndMemory = 0x4;

    private const int MinGapMs = 1000;
    private const int Rate = 22050;

    private static readonly short[] ChimePcm = BuildChime();
    private readonly object _lock = new();
    private readonly AutoResetEvent _signal = new(false);
    private readonly Thread _thread;
    private volatile bool _stop;
    private float? _pending;                 // volume of the waiting chime request (null = none)
    private long _lastStart = -MinGapMs;

    public MechAudioPlayer()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "RaidManager.MechChime" };
        _thread.Start();
    }

    public void PlayChime(float volume)
    {
        lock (_lock) _pending = Math.Clamp(volume, 0f, 1f);
        _signal.Set();
    }

    public void Dispose()
    {
        _stop = true;
        try { PlaySound(null, IntPtr.Zero, 0); } catch { }   // cut a sound in progress
        _signal.Set();
    }

    private void Run()
    {
        while (!_stop)
        {
            float? vol;
            lock (_lock) { vol = _pending; _pending = null; }
            if (vol == null) { _signal.WaitOne(500); continue; }
            try
            {
                long wait = MinGapMs - (Environment.TickCount64 - _lastStart);
                if (wait > 0) Thread.Sleep((int)wait);
                if (_stop) break;
                _lastStart = Environment.TickCount64;
                PlaySound(BuildWav(ChimePcm, vol.Value), IntPtr.Zero, SndMemory | SndSync | SndNoDefault);
            }
            catch { /* no audio device / winmm failure: the banner still shows */ }
        }
        try { _signal.Dispose(); } catch { }
    }

    // RIFF/WAVE image (PCM 16-bit mono) with the volume baked in.
    private static byte[] BuildWav(short[] pcm, float volume)
    {
        int dataLen = pcm.Length * 2;
        var b = new byte[44 + dataLen];
        void W32(int o, int x) => BitConverter.TryWriteBytes(new Span<byte>(b, o, 4), x);
        void W16(int o, short x) => BitConverter.TryWriteBytes(new Span<byte>(b, o, 2), x);
        "RIFF"u8.CopyTo(new Span<byte>(b, 0, 4)); W32(4, 36 + dataLen); "WAVE"u8.CopyTo(new Span<byte>(b, 8, 4));
        "fmt "u8.CopyTo(new Span<byte>(b, 12, 4)); W32(16, 16); W16(20, 1); W16(22, 1);
        W32(24, Rate); W32(28, Rate * 2); W16(32, 2); W16(34, 16);
        "data"u8.CopyTo(new Span<byte>(b, 36, 4)); W32(40, dataLen);
        for (int i = 0; i < pcm.Length; i++) W16(44 + 2 * i, (short)(pcm[i] * volume));
        return b;
    }

    // The alert chime: two short rising tones (880 → 1320 Hz), 22.05 kHz mono, soft attack/decay envelope.
    private static short[] BuildChime()
    {
        int n = (int)(Rate * 0.34);
        var pcm = new short[n];
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / Rate, local = t < 0.15 ? t : t - 0.17;
            if (t >= 0.15 && t < 0.17) continue;                              // tiny gap between the two notes
            double f = t < 0.15 ? 880 : 1320, len = t < 0.15 ? 0.15 : 0.17;
            double env = Math.Min(1, local / 0.01) * Math.Max(0, 1 - local / len);
            pcm[i] = (short)(Math.Sin(2 * Math.PI * f * t) * env * 0.6 * short.MaxValue);
        }
        return pcm;
    }
}
