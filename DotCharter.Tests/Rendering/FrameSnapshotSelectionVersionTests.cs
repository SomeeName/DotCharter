using DotCharter.Rendering.Data;
using DotCharter.Rendering.Data.Render;
using DotCharter.Tests.Perf;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     v0.30 leg b — the idle selection-set rebuild skip: a VERSIONED context
///     (<see cref="SnapshotUpdateContext.SelectionVersion"/> wired to
///     <c>ISelectionService.SelectionVersion</c>) re-hashes the selection
///     lookup sets only when the version moves; unversioned contexts (−1)
///     keep the historical rebuild-every-frame behaviour. The correctness
///     tests pin the latch semantics; the perf test prints the measured
///     before/after via <see cref="RenderPerfHarness"/>.
/// </summary>
[Collection("FrameSnapshotPerfGate")]
public sealed class FrameSnapshotSelectionVersionTests
{
    private readonly ITestOutputHelper _out;

    public FrameSnapshotSelectionVersionTests(ITestOutputHelper output) => _out = output;

    private sealed record Harness(
        SnapshotUpdateContext Ctx, FrameSnapshot Snapshot, double LengthMs,
        IReadOnlyList<GuitarNote> AllNotes);

    private static Harness CreateHarness(int soundCount, double lengthMinutes = 1)
    {
        var chart = StressChartFactory.Chart(soundCount, lengthMinutes);
        var ctx = new SnapshotUpdateContext
        {
            ActiveBeats = chart.Beats,
            ActiveNotes = chart.Notes,
            ActiveChords = chart.Chords,
            ActiveAnchors = chart.Anchors,
            ActiveHandShapes = chart.HandShapes
        };
        return new Harness(ctx, new FrameSnapshot(), chart.LengthMs, chart.Notes);
    }

    private static void Rebuild(Harness h, double timeMs)
        => FrameSnapshot.BuildSnapshot(timeMs - 1000, timeMs + 3000, timeMs, h.Snapshot, h.Ctx,
            []);

    private static int SelectedNoteCount(FrameSnapshot s)
    {
        var count = 0;
        for (var i = 0; i < s.NoteCount; i++)
        {
            if ((s.Notes[i].Flags & NoteFlags.IsSelected) != 0)
                count++;
        }

        return count;
    }

    [Fact]
    public void Versioned_context_skips_the_rebuild_until_the_version_moves()
    {
        var h = CreateHarness(2_000);
        var mid = h.LengthMs / 2;

        // Select everything, version 5: visible notes come back flagged.
        h.Ctx.SelectedNotes = h.AllNotes;
        h.Ctx.SelectionVersion = 5;
        Rebuild(h, mid);
        Assert.True(h.Snapshot.NoteCount > 0);
        Assert.Equal(h.Snapshot.NoteCount, SelectedNoteCount(h.Snapshot));

        // Same version, selection list emptied out from under it: the skip
        // keeps the cached sets — THE point of the version contract (the
        // service only ever mutates the lists together with a bump).
        h.Ctx.SelectedNotes = Array.Empty<GuitarNote>();
        Rebuild(h, mid);
        Assert.Equal(h.Snapshot.NoteCount, SelectedNoteCount(h.Snapshot));

        // Version moves: the empty selection is picked up.
        h.Ctx.SelectionVersion = 6;
        Rebuild(h, mid);
        Assert.Equal(0, SelectedNoteCount(h.Snapshot));
    }

    [Fact]
    public void Unversioned_context_rebuilds_every_frame_as_before()
    {
        var h = CreateHarness(2_000);
        var mid = h.LengthMs / 2;

        h.Ctx.SelectedNotes = h.AllNotes;
        Rebuild(h, mid); // SelectionVersion stays −1 (default)
        Assert.Equal(h.Snapshot.NoteCount, SelectedNoteCount(h.Snapshot));

        h.Ctx.SelectedNotes = Array.Empty<GuitarNote>();
        Rebuild(h, mid); // no version — the change lands immediately
        Assert.Equal(0, SelectedNoteCount(h.Snapshot));
    }

    [Fact]
    public void Measured_delta_with_a_large_live_selection()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("DOTCHARTER_PERF_GATE") != "1",
            "Dev-machine wall-clock measurement — set DOTCHARTER_PERF_GATE=1 to run.");

        var h = CreateHarness(20_000, lengthMinutes: 10);
        var mid = h.LengthMs / 2;
        h.Ctx.SelectedNotes = h.AllNotes; // marquee select-all
        Rebuild(h, mid); // warm arrays/caches

        var t = 0;
        h.Ctx.SelectionVersion = -1; // historical: re-hash the selection every frame
        var before = RenderPerfHarness.Measure(_out, "snapshot rebuild, 20k selected, UNVERSIONED",
            warmup: 50, iterations: 400, () => Rebuild(h, mid + t++ % 500));

        t = 0;
        h.Ctx.SelectionVersion = 7; // versioned + idle: skip until it moves
        var after = RenderPerfHarness.Measure(_out, "snapshot rebuild, 20k selected, versioned idle",
            warmup: 50, iterations: 400, () => Rebuild(h, mid + t++ % 500));

        // The printed lines are the deliverable; assert only the sane bound
        // (the skip must never make the rebuild slower beyond noise).
        Assert.True(after.MedianMs <= before.MedianMs * 1.25,
            $"versioned idle rebuild ({after.MedianMs:0.000} ms) slower than unversioned ({before.MedianMs:0.000} ms)");
    }
}
