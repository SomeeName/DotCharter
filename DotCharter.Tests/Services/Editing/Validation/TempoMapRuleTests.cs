using DotCharter.Services.Editing.Tempo.Estimate;
using DotCharter.Services.Editing.Validation.Rules;
using DotCharter.Tests.Services.Editing.HandShapes;

namespace DotCharter.Tests.Services.Editing.Validation;

/// <summary>
///     D60-D67 (<see cref="TempoMapRule" />): tempo against the audio's estimate, out-of-range and spiking beats,
///     redundant anchors, and the time signature on measure 1 and at every change.
/// </summary>
public sealed class TempoMapRuleTests
{
    /// <summary>One beat per tempo, 4/4, measures every 4 beats.</summary>
    private static List<BeatMarker> Beats(params double[] tempos)
    {
        var beats = new List<BeatMarker>();
        var time = 0.0;
        for (var i = 0; i < tempos.Length; i++)
        {
            beats.Add(new BeatMarker { Time = time, Bpm = tempos[i], IsMeasure = i % 4 == 0 });
            time += 60_000.0 / tempos[i];
        }

        beats.Add(new BeatMarker { Time = time, Bpm = tempos[^1], IsMeasure = tempos.Length % 4 == 0 });
        return beats;
    }

    private static double[] Steady(double bpm, int count) => [.. Enumerable.Repeat(bpm, count)];

    private static Arrangement NotesOn(int firstBeat, int lastBeat)
    {
        var arr = ChordHandShapePlannerTests.Arr();
        foreach (var beat in new[] { firstBeat, lastBeat })
        {
            arr.Levels[0].Notes.Add(new GuitarNote
            {
                Position = ChordHandShapePlannerTests.Pos(beat), StringIndex = 1, Fret = 5
            });
        }

        return arr;
    }

    private static List<ArrangementIssue> Run(IList<BeatMarker> beats, Arrangement? arr = null,
        double? audioBpm = null)
    {
        var store = new AudioTempoEstimateStore();
        var project = new ChartProject { AudioFileName = "song.ogg" };
        if (audioBpm is { } bpm) store.Remember(Path.Combine("x", "song.ogg"), TempoEstimate.From(bpm, 0, 100));
        var issues = new List<ArrangementIssue>();
        new TempoMapRule(store).Validate(
            ArrangementValidationContext.Build(arr ?? NotesOn(0, beats.Count - 2), beats, null, project), issues);
        return issues;
    }

    private static List<ArrangementIssueType> Types(IList<BeatMarker> beats, Arrangement? arr = null,
        double? audioBpm = null) => [.. Run(beats, arr, audioBpm).Select(i => i.Type)];

    [Fact]
    public void TheCodesAreD60ToD67_Warnings()
    {
        ArrangementIssueType[] types =
        [
            ArrangementIssueType.TempoLooksDoubled, ArrangementIssueType.TempoLooksHalved,
            ArrangementIssueType.TempoOutOfRange, ArrangementIssueType.TempoSpike,
            ArrangementIssueType.TempoAnchorSpam, ArrangementIssueType.TempoOffAudio,
            ArrangementIssueType.MeterMissingOnFirstMeasure, ArrangementIssueType.MeterChangeUnmarked
        ];
        Assert.Equal(["D60", "D61", "D62", "D63", "D64", "D65", "D66", "D67"], types.Select(t => t.GetCode()));
        Assert.All(types, t => Assert.Equal(ArrangementIssueSeverity.Warning, t.GetSeverity()));
    }

    [Fact]
    public void TheRuleRunsByDefault()
    {
        var beats = Beats(Steady(320, 16));
        var issues = new ArrangementValidationService(new FakeAppLogger()).Validate(NotesOn(0, 14), beats, null);
        Assert.Contains(issues, i => i.Type == ArrangementIssueType.TempoOutOfRange);
    }

    [Fact]
    public void ASteadyChart_HasNoFindings() =>
        Assert.Empty(Run(Beats(Steady(120, 32)), audioBpm: 120.4));

    [Theory]
    [InlineData(39.5, true)]
    [InlineData(40, false)]
    [InlineData(300, false)]
    [InlineData(301, true)]
    public void TheTempoRange(double bpm, bool expected) =>
        Assert.Equal(expected, Types(Beats(Steady(bpm, 16))).Contains(ArrangementIssueType.TempoOutOfRange));

    [Fact]
    public void AnOutOfRangeRun_IsOneFinding() =>
        Assert.Single(Types(Beats([.. Steady(120, 4), .. Steady(30, 6), .. Steady(120, 6)])),
            t => t == ArrangementIssueType.TempoOutOfRange);

    [Fact]
    public void ARitardandoAfterTheLastNote_IsNotTheChartsTempo()
    {
        var beats = Beats([.. Steady(120, 12), 60, 35, 30, 30]);
        Assert.DoesNotContain(ArrangementIssueType.TempoOutOfRange, Types(beats, NotesOn(0, 11)));
        Assert.DoesNotContain(ArrangementIssueType.TempoSpike, Types(beats, NotesOn(0, 11)));
    }

    [Theory]
    [InlineData(240, true)] // a beat line pushed half a beat early
    [InlineData(170, true)]
    [InlineData(165, false)] // 1.375x: a pushed beat the reference charts do have
    [InlineData(60, false)] // a held fermata beat is slow on purpose
    [InlineData(30, false)]
    public void TheSpike(double odd, bool expected) =>
        Assert.Equal(expected,
            Types(Beats([.. Steady(120, 8), odd, .. Steady(120, 7)])).Contains(ArrangementIssueType.TempoSpike));

    [Fact]
    public void ATempoChange_IsAStepNotASpike() =>
        Assert.DoesNotContain(ArrangementIssueType.TempoSpike, Types(Beats([.. Steady(120, 8), .. Steady(240, 8)])));

    [Fact]
    public void TripletFeel_LeavesTheBeatLinesEven_SoItIsNeverFlagged()
    {
        // Swing moves the off-beat eighth to 2/3 of the beat; the beat lines themselves stay even.
        var beats = Beats(Steady(96, 16));
        var arr = NotesOn(0, 14);
        for (var b = 0; b < 15; b++)
        {
            arr.Levels[0].Notes.Add(new GuitarNote
            {
                Position = new FractionalPosition(b, new Fraction(2, 3)), StringIndex = 2, Fret = 7
            });
        }

        Assert.Empty(Run(beats, arr));
    }

    [Fact]
    public void RedundantAnchors_AtTheLimit_AreSpam()
    {
        var beats = Beats(Steady(120, 40));
        for (var i = 1; i <= TempoMapRule.RedundantAnchorLimit; i++) beats[i].IsAnchor = true;
        Assert.Equal(TempoMapRule.RedundantAnchorLimit, TempoMapRule.RedundantAnchors(beats));
        Assert.Contains(ArrangementIssueType.TempoAnchorSpam, Types(beats));

        beats[1].IsAnchor = false;
        Assert.DoesNotContain(ArrangementIssueType.TempoAnchorSpam, Types(beats));
    }

    [Fact]
    public void AnchorsThatFollowALiveDrummer_AreNotSpam()
    {
        var tempos = Enumerable.Range(0, 64).Select(i => 120 + (i % 5 - 2) * 0.7).ToArray();
        var beats = Beats(tempos);
        foreach (var beat in beats) beat.IsAnchor = true;
        Assert.DoesNotContain(ArrangementIssueType.TempoAnchorSpam, Types(beats));
    }

    [Theory]
    [InlineData(60.0, ArrangementIssueType.TempoLooksDoubled)]
    [InlineData(240.0, ArrangementIssueType.TempoLooksHalved)]
    [InlineData(118.0, ArrangementIssueType.TempoOffAudio)]
    [InlineData(80.0, ArrangementIssueType.TempoOffAudio)]
    public void TheAudioComparison(double audioBpm, ArrangementIssueType expected) =>
        Assert.Contains(expected, Types(Beats(Steady(120, 32)), audioBpm: audioBpm));

    [Fact]
    public void WithinOnePercent_NoAudioFinding() =>
        Assert.Empty(Types(Beats(Steady(120, 32)), audioBpm: 121.1));

    [Fact]
    public void WithoutAnAudioEstimate_TheAudioChecksStaySilent() =>
        Assert.Empty(Types(Beats(Steady(120, 32)), audioBpm: null));

    [Fact]
    public void AnEstimateForAnotherFile_IsNotUsed()
    {
        var store = new AudioTempoEstimateStore();
        store.Remember("other.ogg", TempoEstimate.From(60, 0, 100));
        var issues = new List<ArrangementIssue>();
        new TempoMapRule(store).Validate(ArrangementValidationContext.Build(NotesOn(0, 30), Beats(Steady(120, 32)), null,
            new ChartProject { AudioFileName = "song.ogg" }), issues);
        Assert.Empty(issues);
    }

    [Fact]
    public void AThreeBeatFirstMeasureSaidFourFour_NeedsItsTimeSignature()
    {
        var beats = Beats(Steady(120, 16));
        for (var i = 0; i < beats.Count; i++) beats[i].IsMeasure = i == 0 || (i >= 3 && (i - 3) % 4 == 0);
        var types = Types(beats);
        Assert.Contains(ArrangementIssueType.MeterMissingOnFirstMeasure, types);
        Assert.DoesNotContain(ArrangementIssueType.MeterChangeUnmarked, types);
    }

    [Fact]
    public void AMapThatDoesNotOpenOnAMeasure_NeedsItsTimeSignature()
    {
        var beats = Beats(Steady(120, 16));
        beats[0].IsMeasure = false;
        Assert.Contains(ArrangementIssueType.MeterMissingOnFirstMeasure, Types(beats));
    }

    [Fact]
    public void AnUnmarkedMeterChange_IsReportedOnceWhereItStarts()
    {
        // 4 + 4 measures of 4/4, then three measures that hold 3 beats but still say 4/4, then 4/4 again.
        var beats = Beats(Steady(120, 25));
        int[] starts = [0, 4, 8, 11, 14, 17, 21, 25];
        for (var i = 0; i < beats.Count; i++) beats[i].IsMeasure = starts.Contains(i);
        var issue = Assert.Single(Run(beats), i => i.Type == ArrangementIssueType.MeterChangeUnmarked);
        Assert.Equal(beats[8].Time, issue.TimeMs);
    }

    [Fact]
    public void AMarkedMeterChange_IsFine()
    {
        var beats = Beats(Steady(120, 25));
        int[] starts = [0, 4, 8, 11, 14, 17, 21, 25];
        for (var i = 0; i < beats.Count; i++)
        {
            beats[i].IsMeasure = starts.Contains(i);
            if (i is >= 8 and < 17) beats[i].BeatsInMeasure = 3;
        }

        Assert.Empty(Run(beats));
    }

    [Fact]
    public void TheShortLastMeasure_IsNeverJudged()
    {
        var beats = Beats(Steady(120, 10));
        Assert.DoesNotContain(ArrangementIssueType.MeterChangeUnmarked, Types(beats));
    }

    [Fact]
    public void ValidationNeverChangesTheBeatMap()
    {
        var beats = Beats([.. Steady(120, 8), 240, .. Steady(120, 7)]);
        var before = beats.Select(b => (b.Time, b.Bpm, b.IsMeasure, b.IsAnchor)).ToList();
        Run(beats, audioBpm: 60);
        Assert.Equal(before, beats.Select(b => (b.Time, b.Bpm, b.IsMeasure, b.IsAnchor)));
    }
}
