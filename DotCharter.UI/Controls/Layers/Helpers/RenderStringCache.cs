using System.Collections.Concurrent;
using System.Globalization;
using Avalonia.Media;

namespace DotCharter.UI.Controls.Layers.Helpers;

/// <summary>
/// Per-frame string allocation hot-path is expensive — these caches turn
/// `int.ToString` / `TimeSpan.ToString` / `string.ToUpper` calls into
/// dictionary lookups so the GC has nothing to do during render.
/// </summary>
public static class RenderStringCache
{
    // Frets 0..30 cover every realistic guitar/bass position. Pre-allocated
    // so HighwayNoteHeadRenderer never calls byte.ToString() on the hot path.
    private static readonly string[] FretStrings = BuildFrets();

    private static string[] BuildFrets()
    {
        var arr = new string[32];
        for (var i = 0; i < arr.Length; i++)
            arr[i] = i.ToString(CultureInfo.InvariantCulture);
        return arr;
    }

    public static string Fret(int fret)
    {
        return (uint)fret < (uint)FretStrings.Length
            ? FretStrings[fret]
            : fret.ToString(CultureInfo.InvariantCulture);
    }

    public static string Fret(byte fret) => FretStrings[fret < FretStrings.Length ? fret : 0];

    // Playhead/ruler time labels: cache by integer key so identical timestamps
    // within a session are zero-alloc lookups.
    private static readonly ConcurrentDictionary<int, string> MmSsFfCache = new();

    private static readonly ConcurrentDictionary<long, string> RulerTimeCache = new();

    /// <summary>Cached TIME-ruler label (<see cref="TimeRulerPolicy.Format" />, <c>m:ss</c>),
    /// keyed by the whole second.</summary>
    public static string RulerTime(double ms)
    {
        var key = (long)Math.Round(Math.Max(0, ms) / 1000.0);
        if (RulerTimeCache.TryGetValue(key, out var s)) return s;
        s = TimeRulerPolicy.Format(key * 1000.0);
        if (RulerTimeCache.Count < 8192) RulerTimeCache.TryAdd(key, s);
        return s;
    }

    // The 10 ms bucket means one new key every 10 ms of SONG, so the old
    // `Count < 16384` refusal stopped caching 163.84 s (2:44) into every song
    // and left every later frame formatting a fresh TimeSpan — on three callers
    // (PlayheadLayer, CursorLayer, StatusBarViewModel) at up to 60 Hz. The cap
    // is now a CLEAR-and-continue, so the dictionary keeps working for the whole
    // song, and a one-entry memo sits in front of it: at 60 fps the key only
    // moves every 10 ms, so most frames never reach the dictionary at all.
    private const int MmSsFfCacheCap = 16384;

    /// <summary>
    ///     Immutable so the memo can be published by a single reference write —
    ///     a mutable (key, value) pair would tear across the two threads that
    ///     call this (render thread and UI thread) and pair one timestamp's key
    ///     with another's text.
    /// </summary>
    private sealed record MmSsFfMemo(int Key, string Value);

    private static MmSsFfMemo? _mmSsFfMemo;

    /// <summary>Test seam: the key the one-entry memo currently holds, or −1.</summary>
    internal static int MmSsFfMemoKey => _mmSsFfMemo?.Key ?? -1;

    /// <summary>Test seam: current size of the backing dictionary.</summary>
    internal static int MmSsFfCacheCount => MmSsFfCache.Count;

    public static string MmSsFf(double ms)
    {
        // Quantize to 10ms (matches @"mm:ss.ff" precision).
        var key = (int)(ms / 10.0);
        if (key < 0) key = 0;

        var memo = _mmSsFfMemo;
        if (memo is not null && memo.Key == key) return memo.Value;

        if (!MmSsFfCache.TryGetValue(key, out var s))
        {
            s = TimeSpan.FromMilliseconds(key * 10.0).ToString(@"mm\:ss\.ff", CultureInfo.InvariantCulture);
            // Clear-and-continue rather than refuse: an unbounded dictionary is
            // the only other way to keep caching past the cap, and a song longer
            // than the cap would otherwise fall off the cache permanently.
            if (MmSsFfCache.Count >= MmSsFfCacheCap) MmSsFfCache.Clear();
            MmSsFfCache.TryAdd(key, s);
        }

        _mmSsFfMemo = new MmSsFfMemo(key, s);
        return s;
    }

    // Section labels in EventLayer are uppercased every frame — cache them.
    private static readonly ConcurrentDictionary<string, string> UpperCache = new();

    public static string Upper(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        if (UpperCache.TryGetValue(s, out var u)) return u;
        u = s.ToUpperInvariant();
        if (UpperCache.Count < 4096) UpperCache.TryAdd(s, u);
        return u;
    }

    // Pre-allocated FPS strings 0..999 — MUCH cheaper than string interpolation.
    private static readonly string[] FpsStrings = BuildFps();

    private static string[] BuildFps()
    {
        var arr = new string[1000];
        for (var i = 0; i < arr.Length; i++)
            arr[i] = "FPS: " + i.ToString(CultureInfo.InvariantCulture);
        return arr;
    }

    public static string Fps(int fps)
    {
        return (uint)fps < (uint)FpsStrings.Length
            ? FpsStrings[fps]
            : "FPS: " + fps.ToString(CultureInfo.InvariantCulture);
    }

    // Ruler / measure-metadata labels are formatted *every frame* for every
    // visible measure (typ. 20–60 measures × 60 fps = thousands of allocs/sec).
    // Cache by quantized BPM + beats/denom + measureNumber so repeated draws
    // of the same measure metadata become a single dictionary lookup.
    private static readonly ConcurrentDictionary<long, string> MeasureLabelCache = new();
    private static readonly ConcurrentDictionary<int, string> MeasureNumLabelCache = new();
    private static readonly ConcurrentDictionary<int, string> BpmLabelCache = new();
    private static readonly ConcurrentDictionary<int, string> TimeSigLabelCache = new();

    public static string MeasureLabel(int measureNumber, double bpm, int beatsInMeasure, int noteDenominator)
    {
        // Quantize BPM to 0.01 to keep cache size bounded yet match the
        // "0.##" formatter precision exactly.
        var bpmKey = (int)Math.Round(bpm * 100.0);
        // Pack: measure (24b) | bpmKey (16b) | beats (8b) | denom (8b).
        // Cast each field through unsigned so negative inputs cannot
        // sign-extend into neighbouring bit slots (CS0675).
        var key = ((long)(uint)(measureNumber & 0xFFFFFF) << 32)
                  | ((long)(uint)(bpmKey & 0xFFFF) << 16)
                  | ((long)(uint)(beatsInMeasure & 0xFF) << 8)
                  | (uint)(noteDenominator & 0xFF);
        if (MeasureLabelCache.TryGetValue(key, out var s)) return s;

        var bpmRounded = bpmKey / 100.0;
        s = $"M{measureNumber}  ·  {bpmRounded.ToString("0.##", CultureInfo.InvariantCulture)} BPM  ·  {beatsInMeasure}/{noteDenominator}";
        if (MeasureLabelCache.Count < 8192) MeasureLabelCache.TryAdd(key, s);
        return s;
    }

    /// <summary>Cached "M{n}" prefix for measure pill rendering.</summary>
    public static string MeasureNumberLabel(int measureNumber)
    {
        if (MeasureNumLabelCache.TryGetValue(measureNumber, out var s)) return s;
        s = "M" + measureNumber.ToString(CultureInfo.InvariantCulture);
        if (MeasureNumLabelCache.Count < 4096) MeasureNumLabelCache.TryAdd(measureNumber, s);
        return s;
    }

    /// <summary>Cached "{bpm} BPM" label for measure pill rendering.</summary>
    public static string BpmLabel(double bpm)
    {
        var bpmKey = (int)Math.Round(bpm * 100.0);
        if (BpmLabelCache.TryGetValue(bpmKey, out var s)) return s;
        s = (bpmKey / 100.0).ToString("0.##", CultureInfo.InvariantCulture) + " BPM";
        if (BpmLabelCache.Count < 1024) BpmLabelCache.TryAdd(bpmKey, s);
        return s;
    }

    private static readonly ConcurrentDictionary<int, string> MeasureNumCompactCache = new();
    private static readonly ConcurrentDictionary<int, string> BpmCompactCache = new();

    /// <summary>Cached bare measure number ("17") for the compact measure pill.</summary>
    public static string MeasureNumberCompactLabel(int measureNumber)
    {
        if (MeasureNumCompactCache.TryGetValue(measureNumber, out var s)) return s;
        s = measureNumber.ToString(CultureInfo.InvariantCulture);
        if (MeasureNumCompactCache.Count < 4096) MeasureNumCompactCache.TryAdd(measureNumber, s);
        return s;
    }

    /// <summary>Cached bare tempo ("120", "131.87") for the compact measure pill.</summary>
    public static string BpmCompactLabel(double bpm)
    {
        var bpmKey = (int)Math.Round(bpm * 100.0);
        if (BpmCompactCache.TryGetValue(bpmKey, out var s)) return s;
        s = (bpmKey / 100.0).ToString("0.##", CultureInfo.InvariantCulture);
        if (BpmCompactCache.Count < 1024) BpmCompactCache.TryAdd(bpmKey, s);
        return s;
    }

    /// <summary>Cached "{beats}/{denom}" time-signature label.</summary>
    public static string TimeSigLabel(int beatsInMeasure, int noteDenominator)
    {
        var key = (beatsInMeasure << 8) | noteDenominator;
        if (TimeSigLabelCache.TryGetValue(key, out var s)) return s;
        s = beatsInMeasure.ToString(CultureInfo.InvariantCulture) + "/" +
            noteDenominator.ToString(CultureInfo.InvariantCulture);
        if (TimeSigLabelCache.Count < 256) TimeSigLabelCache.TryAdd(key, s);
        return s;
    }

    // Bend "step / 2" fallback labels — quantized to 0.1 to bound cache size.
    private static readonly ConcurrentDictionary<int, string> BendStepLabelCache = new();

    public static string BendStepLabel(float step)
    {
        var key = (int)Math.Round(step * 10f);
        if (BendStepLabelCache.TryGetValue(key, out var s)) return s;
        s = (key / 20.0).ToString("0.#", CultureInfo.InvariantCulture);
        if (BendStepLabelCache.Count < 256) BendStepLabelCache.TryAdd(key, s);
        return s;
    }

    // Full bend-arrow labels ("ノ" + step text). Cached separately so the
    // note-head renderer never concatenates per visible bend per frame —
    // that concat was the only steady-state allocation in the render loop.
    private static readonly ConcurrentDictionary<int, string> BendArrowLabelCache = new();

    public static string BendArrowLabel(float step, string stepLabel)
    {
        var key = (int)Math.Round(step * 10f);
        if (BendArrowLabelCache.TryGetValue(key, out var s)) return s;
        s = "ノ" + stepLabel;
        if (BendArrowLabelCache.Count < 256) BendArrowLabelCache.TryAdd(key, s);
        return s;
    }
}

public static class ColorAlphaExtensions
{
    /// <summary>Allocation-free color clone with replaced alpha (bitwise, no boxing).</summary>
    public static Color WithAlpha(this Color c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);
}
