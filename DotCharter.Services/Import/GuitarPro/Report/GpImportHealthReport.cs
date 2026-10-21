namespace DotCharter.Services.Import.GuitarPro.Report;

/// <summary>What <see cref="GpImportHealthProbe" /> found for one imported track.</summary>
public sealed class GpProbeArrangement
{
    public int Track { get; init; }
    public string Name { get; init; } = "";
    public ArrangementType Type { get; init; }
    public bool IsBass { get; init; }
    public int StringCount { get; init; }
    public string Tuning { get; init; } = "";
    public int Capo { get; init; }

    /// <summary>The <c>LinkCompatibility</c> class, or null when the part holds no sound or may not link.</summary>
    public string? LinkClass { get; init; }

    public int Notes { get; init; }
    public int Chords { get; init; }

    /// <summary>Single notes plus chord notes.</summary>
    public int Sounds { get; init; }

    /// <summary>A sound on a string the arrangement does not have.</summary>
    public int BadString { get; init; }

    /// <summary>A fret above 24 (off the end of the Rocksmith neck).</summary>
    public int FretOver24 { get; init; }

    /// <summary>A fretted (non-open) sound below the capo (inspector L01).</summary>
    public int BelowCapo { get; init; }

    /// <summary>A sound before the first measure or past the last beat.</summary>
    public int OffGrid { get; init; }

    public int Measures { get; init; }

    /// <summary>Measures in which at least one sound starts.</summary>
    public int ContentMeasures { get; init; }

    /// <summary>Content measures inside a group of repeats within this arrangement.</summary>
    public int RepeatedMeasures { get; init; }

    /// <summary>Of <see cref="RepeatedMeasures" />, those whose written bar Guitar Pro plays only
    /// once — repetition the tab wrote out instead of using a repeat sign.</summary>
    public int RepeatedBeyondGp { get; init; }

    public int RepeatGroups { get; init; }

    /// <summary>Content measures inside a group shared with another arrangement of the same class.</summary>
    public int SharedMeasures { get; init; }

    /// <summary>Content measures equal to the SAME measure of another arrangement of the same class.</summary>
    public int SharedSamePosition { get; init; }

    /// <summary>Pairs of content measures that are one written bar played twice (GP repeats).</summary>
    public int GpRepeatPairs { get; init; }

    /// <summary>Of <see cref="GpRepeatPairs" />, those with equal content keys.</summary>
    public int GpRepeatPairsEqual { get; init; }

    /// <summary>Of <see cref="GpRepeatPairs" />, those the detector put in one group.</summary>
    public int GpRepeatPairsGrouped { get; init; }

    /// <summary>Technique class → authored count (gpif only; empty otherwise).</summary>
    public IReadOnlyDictionary<string, int> SourceTechniques { get; init; } = new Dictionary<string, int>();

    /// <summary>Technique class → imported count.</summary>
    public IReadOnlyDictionary<string, int> ImportedTechniques { get; init; } = new Dictionary<string, int>();

    /// <summary>"ok", "skipped", or "error:&lt;exception type&gt;".</summary>
    public string RoundTrip { get; init; } = "skipped";

    public int RoundTripMeasures { get; init; }

    /// <summary>Measures whose sounds (<c>MeasureContentOptions.SoundsOnly</c>) survive
    /// import → Guitar Pro export → re-import unchanged.</summary>
    public int RoundTripMeasuresEqual { get; init; }

    public int RoundTripSoundsAfter { get; init; }

    /// <summary>Unequal round-trip measures by difference class (<see cref="GpRoundTripDiff" />).</summary>
    public IReadOnlyDictionary<string, int> RoundTripDiffs { get; init; } = new Dictionary<string, int>();
}

/// <summary>What <see cref="GpImportHealthProbe" /> found for one file.</summary>
public sealed class GpProbeReport
{
    public string Path { get; init; } = "";
    public string Format { get; init; } = "";

    /// <summary>The import ran without an exception.</summary>
    public bool Ok { get; init; }

    public string? ErrorClass { get; init; }
    public string? ErrorMessage { get; init; }
    public long ElapsedMs { get; set; }
    public int Tracks { get; init; }
    public int Measures { get; init; }

    /// <summary>Written (master) bars, before repeats unfold.</summary>
    public int SourceBars { get; init; }

    /// <summary>Measures that replay a written bar (second and later passes of a repeat).</summary>
    public int RepeatPlayedMeasures { get; init; }

    public int CrossGroups { get; init; }

    /// <summary>The detector gave up on a degenerate part (hundreds of identical bars).</summary>
    public bool Truncated { get; init; }

    public IReadOnlyList<GpProbeArrangement> Arrangements { get; init; } = [];

    /// <summary>Short machine-readable warning codes, e.g. <c>empty-track</c>, <c>out-of-range</c>.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}
