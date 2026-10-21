namespace DotCharter.Services.Import.GuitarPro.Report.AnswerKey;

/// <summary>Both / tab-only / chart-only counts of one technique over matched sounds.</summary>
public sealed class TechniqueCounts
{
    public int Both { get; set; }
    public int TabOnly { get; set; }
    public int ChartOnly { get; set; }

    /// <summary>Jaccard: agreeing sounds over sounds either side marked; null when neither did.</summary>
    public double? Agreement => Both + TabOnly + ChartOnly == 0 ? null : (double)Both / (Both + TabOnly + ChartOnly);

    public void Add(TechniqueCounts other)
    {
        Both += other.Both;
        TabOnly += other.TabOnly;
        ChartOnly += other.ChartOnly;
    }
}

/// <summary>
///     The counts of one comparison (a measure, an arrangement or a whole corpus). "Tab" is the
///     Guitar Pro import, "chart" the finished chart, the answer key.
/// </summary>
public sealed class ComparisonCounts
{
    public int TabOnsets { get; set; }
    public int ChartOnsets { get; set; }
    public int MatchedOnsets { get; set; }

    public int TabSounds { get; set; }
    public int ChartSounds { get; set; }

    /// <summary>Same onset, same string, same fret.</summary>
    public int ExactSounds { get; set; }

    /// <summary>Same onset and same pitch (exact sounds included).</summary>
    public int PitchSounds { get; set; }

    /// <summary>Matched onsets where both sides strike a chord (two or more sounds).</summary>
    public int ChordOnsets { get; set; }
    public int ChordShapeSame { get; set; }
    public int ChordNamedBoth { get; set; }
    public int ChordNameSame { get; set; }

    /// <summary>Exactly matched fretted sounds with an anchor governing them on both sides.</summary>
    public int FhpSounds { get; set; }
    public int FhpSame { get; set; }

    /// <summary>Same-shape chords whose chart template fingers every fretted string.</summary>
    public int FingeredChords { get; set; }
    public int FingeringSame { get; set; }
    public int FingeredStrings { get; set; }
    public int FingeredStringsSame { get; set; }

    /// <summary>Pairs of tab measures the link detector linked whose chart measures are both aligned.</summary>
    public int LinkPairs { get; set; }

    /// <summary>…of those, pairs the chart also repeats (equal sounds-only content).</summary>
    public int LinkPairsChartEqual { get; set; }

    /// <summary>Pairs of equal sounding chart measures (the chart's real repeats) with both tab measures aligned.</summary>
    public int ChartRepeatPairs { get; set; }

    /// <summary>…of those, pairs the detector put in one group at the same offset.</summary>
    public int ChartRepeatPairsLinked { get; set; }

    public Dictionary<string, TechniqueCounts> Techniques { get; set; } = [];

    public double OnsetF1 => F1(MatchedOnsets, TabOnsets, ChartOnsets);
    public double ExactF1 => F1(ExactSounds, TabSounds, ChartSounds);
    public double PitchF1 => F1(PitchSounds, TabSounds, ChartSounds);

    public static double F1(int matched, int a, int b) => a + b == 0 ? 1.0 : 2.0 * matched / (a + b);

    public TechniqueCounts Technique(SoundTechnique t)
    {
        var key = t.ToString();
        if (!Techniques.TryGetValue(key, out var counts)) Techniques[key] = counts = new TechniqueCounts();
        return counts;
    }

    public void Add(ComparisonCounts o)
    {
        TabOnsets += o.TabOnsets;
        ChartOnsets += o.ChartOnsets;
        MatchedOnsets += o.MatchedOnsets;
        TabSounds += o.TabSounds;
        ChartSounds += o.ChartSounds;
        ExactSounds += o.ExactSounds;
        PitchSounds += o.PitchSounds;
        ChordOnsets += o.ChordOnsets;
        ChordShapeSame += o.ChordShapeSame;
        ChordNamedBoth += o.ChordNamedBoth;
        ChordNameSame += o.ChordNameSame;
        FhpSounds += o.FhpSounds;
        FhpSame += o.FhpSame;
        FingeredChords += o.FingeredChords;
        FingeringSame += o.FingeringSame;
        FingeredStrings += o.FingeredStrings;
        FingeredStringsSame += o.FingeredStringsSame;
        LinkPairs += o.LinkPairs;
        LinkPairsChartEqual += o.LinkPairsChartEqual;
        ChartRepeatPairs += o.ChartRepeatPairs;
        ChartRepeatPairsLinked += o.ChartRepeatPairsLinked;
        foreach (var (key, counts) in o.Techniques)
        {
            if (!Techniques.TryGetValue(key, out var mine)) Techniques[key] = mine = new TechniqueCounts();
            mine.Add(counts);
        }
    }
}

/// <summary>
///     One difference, for the report and for level 2b's triage. <see cref="Class" /> is one of
///     <see cref="DifferenceClasses" />; <see cref="Tab" /> and <see cref="Chart" /> spell the
///     sounds (<c>string:fret</c>, chords joined by <c>+</c>) or values involved.
/// </summary>
public sealed record Difference(int TabMeasure, int ChartMeasure, double Beat, string Class, string? Tab, string? Chart, string? Section = null);

/// <summary>The difference classes, one constant each.</summary>
public static class DifferenceClasses
{
    public const string MissingOnset = "missing-onset";
    public const string ExtraOnset = "extra-onset";
    public const string OnsetShift = "onset-shift";
    public const string OtherString = "same-pitch-other-string";
    public const string Octave = "octave";
    public const string WrongPitch = "wrong-pitch";
    public const string ChordNoteMissing = "chord-note-missing";
    public const string ChordNoteExtra = "chord-note-extra";
    public const string ChordShape = "chord-shape";
    public const string ChordName = "chord-name";
    public const string Fhp = "fhp";
    public const string Fingering = "fingering";
    public const string UnalignedTabMeasure = "unaligned-tab-measure";
    public const string UnalignedChartMeasure = "unaligned-chart-measure";

    /// <summary><c>technique:&lt;name&gt;:missing</c> (the chart has it) or <c>:extra</c> (only the import).</summary>
    public static string Technique(SoundTechnique t, bool missing) => $"technique:{t}:{(missing ? "missing" : "extra")}";
}

/// <summary>One aligned (or unaligned, the other index −1) measure, and the chart section it plays in.</summary>
public sealed record MeasureComparison(int TabMeasure, int ChartMeasure, double Similarity, int TabSounds, int ChartSounds,
    int ExactSounds, int TabOnsets, int ChartOnsets, int MatchedOnsets, int Differences, string? Section = null)
{
    public double ExactF1 => ComparisonCounts.F1(ExactSounds, TabSounds, ChartSounds);
}

/// <summary>One chart arrangement against the tab track that matches it best.</summary>
public sealed class ArrangementComparison
{
    /// <summary>Lead, Rhythm, Combo or Bass.</summary>
    public string Role { get; set; } = "";
    public string ChartFile { get; set; } = "";
    public int TabTrack { get; set; }
    public string TabTrackName { get; set; } = "";
    public string TabTuning { get; set; } = "";
    public string ChartTuning { get; set; } = "";
    public int TabCapo { get; set; }
    public int ChartCapo { get; set; }
    public int TabMeasures { get; set; }
    public int ChartMeasures { get; set; }
    public int AlignedMeasures { get; set; }
    public string AlignmentMethod { get; set; } = "";
    public int FirstNoteOffset { get; set; }
    public double AtFirstNoteOffset { get; set; }
    public double MeanSimilarity { get; set; }
    public ComparisonCounts Counts { get; set; } = new();
    public List<MeasureComparison> Measures { get; set; } = [];
    public List<Difference> Differences { get; set; } = [];

    /// <summary>The chart's sections where they play (<see cref="TabChartSections" />).</summary>
    public List<ChartSection> Sections { get; set; } = [];

    /// <summary>Phrase iterations of the chart's file the importer moved as <c>moveR#</c> phrases.</summary>
    public int MoveRPhrases { get; set; }

    /// <summary>The tunings differ on a string both parts have (an RS XML always lists six), or the capos differ.</summary>
    public bool TuningDiffers
    {
        get
        {
            var tab = TabTuning.Split(' ');
            var chart = ChartTuning.Split(' ');
            return TabCapo != ChartCapo || !tab.Take(chart.Length).SequenceEqual(chart.Take(tab.Length));
        }
    }
}
