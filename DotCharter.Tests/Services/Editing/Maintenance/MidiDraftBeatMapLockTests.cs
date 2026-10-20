using DotCharter.Services.Parsing.Midi;

namespace DotCharter.Tests.Services.Editing.Maintenance;

/// <summary>
///     "Lock beat map" is a promise: no verb may retime the grid while it is on.
///     Every other beat-grid writer honours it — the four tempo-map verbs, the
///     Beat dialog, Set BPM for all measures, <c>BeatScaleService</c>,
///     <c>BeatResyncService</c>, and both the Guitar Pro and Songsterr import
///     paths all check <see cref="ChartProject.IsBeatMapLocked" /> first.
///     <para>
///         <c>ChartMaintenanceService.ApplyMidiDraft</c> did not, and it is the
///         most destructive of them: it CLEARS the beat list and rebuilds it from
///         the MIDI's own tempo map, so a locked chart lost its whole grid — and
///         with it every note's meaning, since positions are grid-relative. The
///         dialog offered the checkbox unconditionally too, so nothing anywhere
///         on that path looked at the lock.
///     </para>
/// </summary>
public sealed class MidiDraftBeatMapLockTests
{
    private static MidiChartDraft Draft()
    {
        var note = new GuitarNote { Position = new FractionalPosition(0), StringIndex = 0, Fret = 3 };
        // A tempo map that is nothing like the harness grid, so a replacement is
        // unmissable: three beats at 240 BPM instead of 32 at 120.
        var beats = new List<BeatMarker>
        {
            new() { Time = 0, Bpm = 240, IsMeasure = true },
            new() { Time = 250, Bpm = 240 },
            new() { Time = 500, Bpm = 240 }
        };
        return new MidiChartDraft([new MidiDraftNote(note, 0.0, 52, 100)], beats);
    }

    [Fact]
    public void ApplyMidiDraft_WithTheBeatMapLocked_LeavesTheGridAlone()
    {
        var h = new ChartPowerToolsTests.Harness();
        var project = h.State.ActiveProject!;
        project.IsBeatMapLocked = true;
        var beatsBefore = project.Beats
            .Select(b => (b.Time, b.Bpm, b.IsMeasure, b.BeatsInMeasure, b.NoteDenominator))
            .ToList();

        h.Maintenance.ApplyMidiDraft(Draft(), replaceBeatGrid: true);

        var after = h.State.ActiveProject!;
        Assert.Equal(beatsBefore, after.Beats
            .Select(b => (b.Time, b.Bpm, b.IsMeasure, b.BeatsInMeasure, b.NoteDenominator))
            .ToList());
    }

    /// <summary>
    ///     The lock covers the GRID, not the import — the draft arrangement is
    ///     what the user asked for and still lands, exactly as the Guitar Pro
    ///     path keeps its notes when it skips the tempo map.
    /// </summary>
    [Fact]
    public void ApplyMidiDraft_WithTheBeatMapLocked_StillAddsTheDraftArrangement()
    {
        var h = new ChartPowerToolsTests.Harness();
        h.State.ActiveProject!.IsBeatMapLocked = true;
        var arrangementsBefore = h.State.ActiveProject!.Arrangements.Count;

        Assert.True(h.Maintenance.ApplyMidiDraft(Draft(), replaceBeatGrid: true));

        Assert.Equal(arrangementsBefore + 1, h.State.ActiveProject!.Arrangements.Count);
    }

    /// <summary>Unlocked, the replacement is the whole point of the checkbox.</summary>
    [Fact]
    public void ApplyMidiDraft_Unlocked_ReplacesTheGrid()
    {
        var h = new ChartPowerToolsTests.Harness();

        h.Maintenance.ApplyMidiDraft(Draft(), replaceBeatGrid: true);

        Assert.Equal(3, h.State.ActiveProject!.Beats.Count);
        Assert.Equal(240.0, h.State.ActiveProject!.Beats[0].Bpm);
    }
}
