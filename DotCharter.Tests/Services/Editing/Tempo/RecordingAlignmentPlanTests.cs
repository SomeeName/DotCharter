using DotCharter.Analysis.BeatMap;
using DotCharter.Services.Editing.Tempo.RecordingAlignment;

namespace DotCharter.Tests.Services.Editing.Tempo;

/// <summary>Align to recording (#784): the pure halves — what the chart expects, which beats may move,
///     how a skipped section is composed, and which stems are listened to.</summary>
public sealed class RecordingAlignmentPlanTests
{
    [Fact]
    public void TheRange_FixesEveryBeatOutsideIt_AndKeptAnchorsInside()
    {
        var project = Project(32);
        project.Beats[20].IsAnchor = true;

        var fromBar3 = RecordingAlignmentPlan.Build(project, RecordingAlignmentRange.FromBarNumber(3), keepAnchors: true);
        Assert.All(fromBar3.Beats.Take(8), b => Assert.True(b.IsFixed));
        Assert.True(fromBar3.Beats[20].IsFixed);
        Assert.Equal(1, fromBar3.AnchoredInRange);
        Assert.Equal(23, fromBar3.FreeBeats);

        var released = RecordingAlignmentPlan.Build(project, RecordingAlignmentRange.WholeSong, keepAnchors: false);
        Assert.Equal(32, released.FreeBeats);

        var span = RecordingAlignmentPlan.Build(project, RecordingAlignmentRange.Span(2000, 4000), keepAnchors: true);
        Assert.Equal(Enumerable.Range(4, 5), Enumerable.Range(0, 32).Where(i => !span.Beats[i].IsFixed));
    }

    [Fact]
    public void Events_MergeArrangementsByPosition_AndAFullMuteKeepsItsOnsetWithoutAPitch()
    {
        var project = Project(8);
        var lead = project.Arrangements[0];
        var bass = new Arrangement { Name = "Bass", ArrangementType = ArrangementType.Bass };
        bass.Levels.Add(new ChartLevel());
        project.Arrangements.Add(bass);
        var vocals = new Arrangement { Name = "Vocals", ArrangementType = ArrangementType.Vocal };
        vocals.Levels.Add(new ChartLevel());
        vocals.Levels[0].Notes.Add(Note(3, 0, 5));
        project.Arrangements.Add(vocals);

        lead.Levels[0].Notes.Add(Note(1, 0, 5));
        bass.Levels[0].Notes.Add(Note(1, 0, 0));
        lead.Levels[0].Notes.Add(new GuitarNote { Position = new FractionalPosition(2, new Fraction(1, 2)), Mute = MuteType.Full });

        var events = RecordingAlignmentPlan.ExpectedEvents(project);

        Assert.Equal(2, events.Count);
        Assert.Equal(1.0, events[0].BeatPosition);
        Assert.Equal([28, 45], events[0].Midi);
        Assert.Equal(2.5, events[1].BeatPosition);
        Assert.Empty(events[1].Midi);
    }

    [Fact]
    public void Sections_ComeFromTheMarkers_OrBlocksOfEightBars()
    {
        var project = Project(80);
        var blocks = RecordingAlignmentPlan.Build(project, RecordingAlignmentRange.WholeSong, true).Sections;
        Assert.Equal([0, 32, 64], blocks.Select(s => s.StartBeat));
        Assert.Equal([1, 9, 17], blocks.Select(s => s.FirstBar));
        Assert.Equal(80, blocks[^1].EndBeat);

        project.Arrangements[0].EventPoints.Add(new EventPoint { Position = new FractionalPosition(4), Section = "verse" });
        project.Arrangements[0].EventPoints.Add(new EventPoint { Position = new FractionalPosition(40), Section = "chorus" });
        var marked = RecordingAlignmentPlan.Build(project, RecordingAlignmentRange.WholeSong, true).Sections;
        Assert.Equal([0, 4, 40], marked.Select(s => s.StartBeat));
        Assert.Equal(["", "verse", "chorus"], marked.Select(s => s.Name));
    }

    [Fact]
    public void ASkippedSection_IsStretchedBetweenItsAlignedNeighbours()
    {
        var project = Project(12);
        foreach (var beat in project.Beats) beat.IsMeasure = project.Beats.IndexOf(beat) % 4 == 0;
        project.Arrangements[0].EventPoints.Add(new EventPoint { Position = new FractionalPosition(4), Section = "b" });
        project.Arrangements[0].EventPoints.Add(new EventPoint { Position = new FractionalPosition(8), Section = "c" });
        var plan = RecordingAlignmentPlan.Build(project, RecordingAlignmentRange.WholeSong, true);
        // The recording is 1 s later and 10 % slower everywhere.
        var aligned = plan.OldTimes.Select(t => 1000 + 1.1 * t).ToArray();

        var composed = RecordingAlignmentComposer.Compose(project.Beats, plan, aligned, new HashSet<int> { 0, 2 })!;

        for (var i = 0; i < 12; i++) Assert.Equal(aligned[i], composed[i].Time, 6);
        Assert.Equal(60000.0 / 550, composed[5].Bpm, 6);
        Assert.Equal(project.Beats.Select(b => b.IsMeasure), composed.Select(b => b.IsMeasure));

        var noneAfter = RecordingAlignmentComposer.Compose(project.Beats, plan, aligned, new HashSet<int> { 0 })!;
        Assert.Equal(aligned[3] - plan.OldTimes[3] + plan.OldTimes[11], noneAfter[11].Time, 6);
    }

    [Fact]
    public void AComposedGrid_ThatWouldRunBackwards_IsRefused()
    {
        var project = Project(4);
        var plan = RecordingAlignmentPlan.Build(project, RecordingAlignmentRange.WholeSong, true);
        Assert.Null(RecordingAlignmentComposer.Compose(project.Beats, plan, [0, 600, 500, 1500], new HashSet<int> { 0 }));
    }

    [Fact]
    public void InstrumentStems_AreTheExactNames_BestFormatFirst()
    {
        var picked = RecordingAlignmentAudio.PickInstrumentStems(
            ["stems/vocals.wav", "stems/drums.wav", "stems/bass.opus", "stems/bass.wav", "stems/other.opus", "stems/no_guitar.wav", "stems/guitar.txt"]);
        Assert.Equal(["stems/bass.wav", "stems/other.opus"], picked);
    }

    [Fact]
    public void ALowConfidenceSection_IsNotSuggested_NorOneWithNothingToMove()
    {
        var span = new RecordingAlignmentSectionSpan("", 0, 4, 1, 1);
        Assert.True(new RecordingAlignmentSectionResult(0, span, 0.9, 8, 10, 4).SuggestApply);
        Assert.False(new RecordingAlignmentSectionResult(0, span, RecordingAligner.LowConfidence - 0.01, 8, 10, 4).SuggestApply);
        Assert.False(new RecordingAlignmentSectionResult(0, span, double.NaN, 0, 10, 4).SuggestApply);
        Assert.False(new RecordingAlignmentSectionResult(0, span, 0.9, 8, 0, 0).SuggestApply);
    }

    private static GuitarNote Note(int beat, int stringIndex, int fret) =>
        new() { Position = new FractionalPosition(beat), StringIndex = stringIndex, Fret = fret };

    private static ChartProject Project(int beats)
    {
        var project = new ChartProject();
        for (var i = 0; i < beats; i++) project.Beats.Add(new BeatMarker { Time = i * 500, IsMeasure = i % 4 == 0 });
        var lead = new Arrangement { Name = "Lead" };
        lead.Levels.Add(new ChartLevel());
        project.Arrangements.Add(lead);
        return project;
    }
}
