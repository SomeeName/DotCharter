using System.Runtime.CompilerServices;
using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Quality;

namespace DotCharter.Tests.Services.Editing.Quality;

/// <summary>
///     #711: the quality report restores the live chart into the copy its last check gave back
///     instead of cloning a new one. The restored copy must be the copy a clone would have been,
///     whatever chart the spare held before, and the report over it the same report; a copy a check
///     is still reading is never handed out again.
/// </summary>
public sealed class QualityReportCopiesTests
{
    private static readonly ChartQualityReporter Reporter = new(new ArrangementValidationService(new FakeAppLogger()));

    /// <summary>A chart the rules and the export gate both have something to say about.</summary>
    private static ChartProject Chart(int notes, int levels)
    {
        var project = new ChartProject { Bpm = 120 };
        for (var i = 0; i < 32; i++)
            project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120, BeatsInMeasure = 4, NoteDenominator = 4, IsMeasure = i % 4 == 0 });
        var arrangement = new Arrangement { Name = "Lead" };
        for (var l = 0; l < levels; l++)
        {
            var level = new ChartLevel { DifficultyIndex = l };
            for (var beat = 4; beat < 4 + notes; beat++)
                level.Notes.Add(new GuitarNote { Position = new FractionalPosition(beat), StringIndex = beat % 6, Fret = beat % 3 == 0 ? 25 : 5 });
            level.Chords.Add(new Chord
            {
                Position = new FractionalPosition(2),
                ChordNotes = { new ChordNote { StringIndex = 0, Fret = 3 }, new ChordNote { StringIndex = 1, Fret = 5 } }
            });
            arrangement.Levels.Add(level);
        }

        project.Arrangements.Add(arrangement);
        project.Arrangements.Add(new Arrangement { Name = "Bass", ArrangementType = ArrangementType.Bass });
        return project;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ReturnACopy(QualityReportCopies copies, ChartProject live) => copies.Return(copies.Copy(live, out _));

    private static void Collect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    [Fact]
    public void WithoutASpare_TheCopyIsAClone()
    {
        var live = Chart(8, 2);
        var copy = new QualityReportCopies().Copy(live, out var reused);

        Assert.False(reused);
        Assert.NotSame(live, copy);
        Assert.Empty(DeepCompare.Diff("ChartProject", ChartProjectCloner.Clone(live), copy, new HashSet<string>()));
    }

    /// <summary>The spare held a bigger chart, then a smaller one, then a populated one: each restore
    /// must equal a fresh clone, so nothing of the chart the spare held before survives.</summary>
    [Fact]
    public void ASpareRestoredFromAnotherChart_EqualsAFreshClone_AndReportsTheSame()
    {
        var copies = new QualityReportCopies();
        copies.Return(ChartProjectCloner.Clone(Chart(20, 5)));
        foreach (var live in new[] { Chart(8, 2), Chart(30, 6), DomainFieldProbe.Populated<ChartProject>(), Chart(3, 1) })
        {
            var fresh = ChartProjectCloner.Clone(live);
            var copy = copies.Copy(live, out var reused);

            Assert.True(reused);
            Assert.Empty(DeepCompare.Diff("ChartProject", fresh, copy, new HashSet<string>()));
            Assert.Equal(QualityReportFingerprint.Of(Reporter.Build(fresh), fresh), QualityReportFingerprint.Of(Reporter.Build(copy), copy));
            copies.Return(copy);
        }
    }

    [Fact]
    public void ACopyIsHandedOutOnce()
    {
        var copies = new QualityReportCopies();
        var live = Chart(8, 2);
        var first = copies.Copy(live, out _);
        copies.Return(first);

        var reading = copies.Copy(live, out var reused);
        var next = copies.Copy(live, out var reusedAgain);

        Assert.True(reused);
        Assert.Same(first, reading);
        Assert.False(reusedAgain);
        Assert.NotSame(reading, next);
    }

    [Fact]
    public void TheLiveChart_IsNeverItsOwnSpare()
    {
        var copies = new QualityReportCopies();
        var live = Chart(8, 2);
        copies.Return(live);

        var copy = copies.Copy(live, out var reused);

        Assert.False(reused);
        Assert.NotSame(live, copy);
    }

    /// <summary>While a report is open its spare survives a collection; once the last one closes, the
    /// spare is the collector's.</summary>
    [Fact]
    public void TheSpareIsHeldWhileAReportIsOpen_AndWeaklyOnceItCloses()
    {
        var copies = new QualityReportCopies();
        var live = Chart(8, 2);
        var open = copies.Open();
        ReturnACopy(copies, live);
        Collect();
        _ = copies.Copy(live, out var heldWhileOpen);
        Assert.True(heldWhileOpen);

        ReturnACopy(copies, live);
        open.Dispose();
        open.Dispose();
        Collect();
        _ = copies.Copy(live, out var heldOnceClosed);
        Assert.False(heldOnceClosed);
    }

    [Fact]
    public void ASnapshot_GivesItsCopyBackOnRelease_AndRefusesAfter()
    {
        var copies = new QualityReportCopies();
        var live = Chart(8, 2);
        var snapshot = QualityReportSnapshot.Take(live, null, copies);
        var check = snapshot.Check(Reporter, null);
        Assert.NotNull(snapshot.Rebind(check));
        Assert.False(snapshot.Reused);

        snapshot.Release();
        snapshot.Release();

        Assert.Throws<ObjectDisposedException>(() => snapshot.Check(Reporter, null));
        Assert.Throws<ObjectDisposedException>(() => snapshot.Rebind(check));
        var next = QualityReportSnapshot.Take(live, null, copies);
        Assert.True(next.Reused);
        Assert.Same(snapshot.Copy, next.Copy);
    }

    /// <summary>#720: two arrangements, each with levels, and a history whose scope sees the chart.</summary>
    private static (ChartProject Live, HistoryService History) Edited()
    {
        var live = Chart(8, 3);
        var bass = live.Arrangements[1];
        for (var l = 0; l < 3; l++)
        {
            bass.Levels.Add(new ChartLevel
            {
                DifficultyIndex = l,
                Notes = { new GuitarNote { Position = new FractionalPosition(4), StringIndex = 0, Fret = 3 } }
            });
        }

        var state = new ProjectState
        {
            ActiveProject = live,
            ActiveArrangement = live.Arrangements[0],
            ActiveLevel = live.Arrangements[0].Levels[0]
        };
        return (live, new HistoryService(new FakeAppLogger(), new EditingScope(state)));
    }

    /// <summary>The spare's arrangement is bumped only when the restore copied into it.</summary>
    private static bool[] Copied(ChartProject before, long[] revisions) =>
        before.Arrangements.Select((a, i) => a.GetRevision() != revisions[i]).ToArray();

    /// <summary>#720: an arrangement nobody edited since the spare was filled is not copied again; an edited
    /// one is, after the edit, its undo and its redo — and the copy always equals a fresh clone.</summary>
    [Fact]
    public void AnUnchangedArrangement_IsNotCopied_AndAnEditedOneIs()
    {
        var (live, history) = Edited();
        var copies = new QualityReportCopies();
        var spare = copies.Copy(live, out _);
        copies.Return(spare);

        Check(() => { }, [false, false]);
        var bassNotes = live.Arrangements[1].Levels[2].Notes;
        Check(() => history.Do(new MutateNotesAction(bassNotes, [bassNotes[0]], n => n.Fret = 7)), [false, true]);
        Check(history.Undo, [false, true]);
        Check(history.Redo, [false, true]);
        Check(() => history.Do(new AddNotesAction(live.Arrangements[0].Levels[1].Notes, [new GuitarNote { Position = new FractionalPosition(30) }])),
            [true, false]);
        return;

        void Check(Action change, bool[] expected)
        {
            change();
            var revisions = spare.Arrangements.Select(a => a.GetRevision()).ToArray();
            var copy = copies.Copy(live, out var reused);
            Assert.True(reused);
            Assert.Same(spare, copy);
            Assert.Equal(expected, Copied(copy, revisions));
            Assert.Empty(DeepCompare.Diff("ChartProject", ChartProjectCloner.Clone(live), copy, new HashSet<string>()));
            copies.Return(copy);
        }
    }

    /// <summary>#720: a spare filled from another chart is copied in full, even where the revisions agree.</summary>
    [Fact]
    public void ASpareFilledFromAnotherChart_IsCopiedInFull()
    {
        var (first, _) = Edited();
        var (second, _) = Edited();
        second.Arrangements[0].Levels[0].Notes[0].Fret = 9;
        var copies = new QualityReportCopies();
        copies.Return(copies.Copy(first, out _));

        var spare = copies.Copy(first, out _);
        copies.Return(spare);
        var revisions = spare.Arrangements.Select(a => a.GetRevision()).ToArray();
        var copy = copies.Copy(second, out _);

        Assert.Equal([true, true], Copied(copy, revisions));
        Assert.Empty(DeepCompare.Diff("ChartProject", ChartProjectCloner.Clone(second), copy, new HashSet<string>()));
    }

    /// <summary>#720: skipping is sound only while a check reads its copy and writes nothing to it.</summary>
    [Fact]
    public void TheCheck_LeavesItsCopyAsItFoundIt()
    {
        foreach (var live in new[] { Chart(30, 6), DomainFieldProbe.Populated<ChartProject>() })
        {
            var snapshot = QualityReportSnapshot.Take(live, null, new QualityReportCopies());
            _ = snapshot.Check(Reporter, 60_000);
            Assert.Empty(DeepCompare.Diff("ChartProject", ChartProjectCloner.Clone(live), snapshot.Copy, new HashSet<string>()));
        }
    }
}
