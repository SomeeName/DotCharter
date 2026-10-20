using DotCharter.Tone.Audio.Reamp;

namespace DotCharter.Tests.Audio.Reamp;

/// <summary>
///     A synthetic engine for the DI recorder and the re-amp view model: a
///     capture STREAM whose sample at absolute position n is <see cref="SampleAt" />,
///     advanced by <see cref="Capture" />, with a ring of <see cref="RingSize" /> so
///     a slow reader genuinely loses samples; and an injection slot that records
///     what was loaded and plays it at <see cref="ReampSampleRate" />.
/// </summary>
internal sealed class FakeReampEngine : IReampEngine
{
    public const int RingSize = 4096;
    private ulong _written;
    private ulong _streamBase; // a "rebuild" restarts the stream at 0

    public bool IsReampAvailable { get; set; } = true;
    public int ReampSampleRate { get; set; } = 48000;

    public static float SampleAt(ulong n) => (float)((n % 1000) / 1000.0) - 0.5f;

    /// <summary>The audio thread captured <paramref name="frames" /> more samples.</summary>
    public void Capture(int frames) => _written += (ulong)frames;

    /// <summary>An engine rebuild: the ring restarts from position 0.</summary>
    public void RestartStream()
    {
        _streamBase += _written;
        _written = 0;
    }

    public bool TryGetCaptureTotal(int player, out ulong total)
    {
        total = _written;
        return IsReampAvailable;
    }

    public int ReadCaptureFrom(int player, ulong from, Span<float> dest, out ulong first)
    {
        var w = _written;
        // A cursor past the stream means the stream RESTARTED: from its start.
        var r = from > w ? 0 : from;
        if (w - r > RingSize) r = w - RingSize;
        first = r;
        var n = (int)Math.Min((ulong)dest.Length, w - r);
        for (var i = 0; i < n; i++) dest[i] = SampleAt(_streamBase + r + (ulong)i);
        return n;
    }

    // ---- injection ---------------------------------------------------------

    public float[]? LoadedTake { get; private set; }
    public int LoadedRate { get; private set; }
    public bool Loop { get; private set; }
    public bool Playing { get; private set; }
    public double Position { get; set; }
    public bool RefuseLoads { get; set; }
    public bool Remembered { get; set; }
    public List<double> Seeks { get; } = [];

    public bool LoadTake(int player, float[] mono, int sampleRate, bool loop)
    {
        if (RefuseLoads || !IsReampAvailable) return false;
        LoadedTake = mono;
        LoadedRate = sampleRate;
        Loop = loop;
        Playing = false;
        Position = 0;
        Remembered = true;
        return true;
    }

    public bool HasTake(int player) => Remembered;

    public void ClearTake(int player)
    {
        LoadedTake = null;
        Playing = false;
        Remembered = false;
    }

    public bool SetTakePlaying(int player, bool playing)
    {
        if (LoadedTake == null) return false;
        Playing = playing;
        return true;
    }

    public bool SetTakeLoop(int player, bool loop)
    {
        if (LoadedTake == null) return false;
        Loop = loop;
        return true;
    }

    public bool SeekTake(int player, double seconds)
    {
        if (LoadedTake == null) return false;
        Seeks.Add(seconds);
        Position = seconds;
        return true;
    }

    public bool TryGetTakeStatus(int player, out ReampTransportStatus status)
    {
        status = default;
        if (LoadedTake == null) return false;
        status = new ReampTransportStatus(Position, (double)LoadedTake.Length / LoadedRate, Playing, Loop);
        return true;
    }
}
