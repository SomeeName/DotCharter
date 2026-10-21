using DotCharter.Services.Import.GuitarPro.Report.AnswerKey;
using DotCharter.Services.Parsing.GuitarPro;
using DotCharter.Services.Parsing.Xml;
using DotCharter.Tests.Services.Parsing.GuitarPro;

namespace DotCharter.Tests.Services.Import.GuitarPro.Report.AnswerKey;

/// <summary>
///     The scoring half of <c>tools/tab-vs-chart</c>, end to end on SYNTHETIC files: a tiny Guitar
///     Pro tab through the real importer, against a tiny RS XML "finished chart" written and read
///     back by the real serializer. The chart is the tab's own notes after a one-bar count-in, with
///     known edits, so every count below is known in advance.
/// </summary>
public sealed class TabChartComparerTests
{
    /// <summary><c>|: X Y :| Z</c>: five measures of four quarter notes from three written bars.</summary>
    private static readonly FxMeasure[] Tab = Bars(1, 3, 5);

    [Fact]
    public void TheChartsEdits_AreCountedAndClassed_AfterACountIn()
    {
        var (tab, tabBeats) = ImportTab();
        var tabLevel = tab.Levels.MaxBy(l => l.DifficultyIndex)!;
        var sounds = tabLevel.Notes.Count;
        Assert.Equal(20, sounds);

        var chart = ChartFromTab(tab, tabBeats, countInBars: 1, edit: notes =>
        {
            // Measure 4 (the written bar Z): one wrong fret, one note moved to the same pitch on
            // another string, one palm mute only the chart has, one note the chart dropped.
            var z = notes.Where(n => n.Position.BeatId >= 4 + 16).OrderBy(n => n.Position).ToList();
            z[0].Fret += 2;
            var moved = z[1];
            var lower = moved.StringIndex > 0 ? moved.StringIndex - 1 : moved.StringIndex + 1;
            var pitch = SoundingPitch.MidiFor(tab, moved.StringIndex, moved.Fret)!.Value;
            moved.Fret = SoundingPitch.FretForPitch(tab, lower, pitch)!.Value;
            moved.StringIndex = lower;
            z[2].Mute = MuteType.Palm;
            notes.Remove(z[3]);
        });

        var result = TabChartComparer.Compare(tab, tabBeats, chart.Part, chart.Beats);
        var c = result.Counts;

        Assert.Equal("dp", result.AlignmentMethod);
        Assert.Equal(1, result.FirstNoteOffset);
        Assert.Equal(5, result.AlignedMeasures);
        Assert.Equal(1.0, result.AtFirstNoteOffset);

        Assert.Equal(20, c.TabSounds);
        Assert.Equal(19, c.ChartSounds);
        Assert.Equal(17, c.ExactSounds);
        Assert.Equal(18, c.PitchSounds);
        Assert.Equal(19, c.MatchedOnsets);

        Assert.Equal(1, Count(result, DifferenceClasses.WrongPitch));
        Assert.Equal(1, Count(result, DifferenceClasses.OtherString));
        Assert.Equal(1, Count(result, DifferenceClasses.ExtraOnset));
        Assert.Equal(1, Count(result, DifferenceClasses.Technique(SoundTechnique.PalmMute, missing: true)));
        Assert.Equal(1, c.Technique(SoundTechnique.PalmMute).ChartOnly);
        Assert.All(result.Differences, d => Assert.Equal(4, d.TabMeasure));

        // Linked measures: the detector's repeat of X Y is a real repeat in the chart, both ways.
        Assert.True(c.LinkPairs >= 2, $"link pairs {c.LinkPairs}");
        Assert.Equal(c.LinkPairs, c.LinkPairsChartEqual);
        Assert.Equal(2, c.ChartRepeatPairs);
        Assert.Equal(2, c.ChartRepeatPairsLinked);
    }

    [Fact]
    public void TheSameChartAtAnotherOffset_AgreesCompletely_AndTheBestTrackIsPicked()
    {
        var (tab, tabBeats) = ImportTab();
        var chart = ChartFromTab(tab, tabBeats, countInBars: 3, edit: _ => { });
        var decoy = new Arrangement { Name = "Decoy", ArrangementType = ArrangementType.Lead, StringCount = 6 };
        foreach (var offset in tab.TuningPitches) decoy.TuningPitches.Add(new TuningOffset(offset.Step));
        var decoyLevel = new ChartLevel();
        foreach (var n in tab.Levels.MaxBy(l => l.DifficultyIndex)!.Notes)
        {
            var copy = n.Clone();
            copy.Fret += 7;
            decoyLevel.Notes.Add(copy);
        }

        decoy.Levels.Add(decoyLevel);

        var results = TabChartComparer.CompareSong([decoy, tab], tabBeats, [("Lead_RS2.xml", chart.Part, chart.Beats)]);

        var result = Assert.Single(results);
        Assert.Equal(1, result.TabTrack);
        Assert.Equal("Lead", result.Role);
        Assert.Equal(3, result.FirstNoteOffset);
        Assert.Equal(1.0, result.Counts.ExactF1);
        Assert.Equal(1.0, result.Counts.OnsetF1);
        Assert.Empty(result.Differences);
    }

    /// <summary>An RS XML lists six strings for a four-string bass: only shared strings and the capo count.</summary>
    [Theory]
    [InlineData("0 0 0 0", "0 0 0 0 0 0", 0, false)]
    [InlineData("-1 -1 -1 -1", "-1 -1 -1 -1 -1 -1", 0, false)]
    [InlineData("0 0 0 0 0 0", "-2 0 0 0 0 0", 0, true)]
    [InlineData("0 0 0 0", "0 0 0 0 0 0", 2, true)]
    public void ATuningDiffers_OnlyOnSharedStringsOrTheCapo(string tab, string chart, int chartCapo, bool differs) =>
        Assert.Equal(differs, new ArrangementComparison { TabTuning = tab, ChartTuning = chart, ChartCapo = chartCapo }.TuningDiffers);

    private static int Count(ArrangementComparison r, string cls) => r.Differences.Count(d => d.Class == cls);

    private static (Arrangement Part, IList<BeatMarker> Beats) ImportTab()
    {
        var path = LegacyGpFixtureBuilder.WriteFile("5.10", Tab, trackName: "Lead");
        try
        {
            var importer = new GpImporter();
            var imported = importer.ImportTracksWithMetadata(path, [.. importer.GetAvailableTracks(path).Select(t => t.Id)]);
            var beats = GpBeatMapBuilder.Build(imported.Metadata, GpBeatMapBuilder.InitialBpm(imported.Metadata, 120), 0);
            return (Assert.Single(imported.Arrangements), beats);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    ///     The tab's notes after <paramref name="countInBars" /> silent 4/4 bars at 100 BPM, edited,
    ///     written as an RS XML and read back.
    /// </summary>
    private static (Arrangement Part, IList<BeatMarker> Beats) ChartFromTab(Arrangement tab, IList<BeatMarker> tabBeats, int countInBars,
        Action<List<GuitarNote>> edit)
    {
        var shift = countInBars * 4;
        var notes = tab.Levels.MaxBy(l => l.DifficultyIndex)!.Notes
            .Select(n =>
            {
                var copy = n.Clone();
                copy.Position = new FractionalPosition(n.Position.BeatId + shift, n.Position.Fraction);
                copy.LeftHandFinger = -1;
                return copy;
            })
            .ToList();
        edit(notes);

        var part = new Arrangement { Name = "Lead", ArrangementType = ArrangementType.Lead, StringCount = 6 };
        for (var i = 0; i < 6; i++) part.TuningPitches.Add(new TuningOffset(0));
        var level = new ChartLevel();
        foreach (var n in notes) level.Notes.Add(n);
        part.Levels.Add(level);

        var project = new ChartProject();
        var beatCount = tabBeats.Count + shift + 4;
        for (var i = 0; i < beatCount; i++)
            project.Beats.Add(new BeatMarker { Time = i * 600.0, Bpm = 100, BeatsInMeasure = 4, NoteDenominator = 4, IsMeasure = i % 4 == 0 });
        project.Arrangements.Add(part);

        var path = Path.Combine(Path.GetTempPath(), $"dc-tab-vs-chart-{Guid.NewGuid():N}_lead.xml");
        try
        {
            new XmlChartSerializer().Save(path, project);
            var loaded = new XmlChartSerializer().Load(path);
            return (Assert.Single(loaded.Arrangements), loaded.Beats);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>|: a b :| c — the first two repeat twice.</summary>
    private static FxMeasure[] Bars(int a, int b, int c)
    {
        var first = Bar(a);
        first.RepeatStart = true;
        var second = Bar(b);
        second.RepeatTotalPasses = 2;
        return [first, second, Bar(c)];
    }

    /// <summary>Four quarter notes climbing from <paramref name="fret" />, on two strings.</summary>
    private static FxMeasure Bar(int fret) => new FxMeasure().AddVoice(
        FxBeat.Note(0, 2, fret), FxBeat.Note(0, 3, fret + 1), FxBeat.Note(0, 2, fret + 2), FxBeat.Note(0, 3, fret));
}
