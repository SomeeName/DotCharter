using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace DotCharter.Tests.Services.State;

/// <summary>
///     Static perf audit 2026-08-29 P0 #7: <see cref="MutateElementsAction{T}" />
///     re-sorted its UI-bound <c>ObservableCollection</c> with a gnome sort on
///     every <c>Execute</c>/<c>Undo</c> — i.e. every tick of a drag or wheel
///     gesture, because <c>HistoryService.Do</c> executes before it merges — and
///     a gnome sort moves an element one SLOT at a time, raising one
///     <c>CollectionChanged(Move)</c> per slot to every bound view.
///     <para>
///         The replacement must produce the SAME final order (the stable sort
///         of the current sequence, which is what an insertion sort yields) and
///         may only raise FEWER notifications: none when nothing moved, one per
///         displaced element otherwise. The reference below is the old body,
///         verbatim — keep it dumb.
///     </para>
/// </summary>
public sealed class MutateElementsActionSortTests(ITestOutputHelper output)
{
    /// <summary>The old <c>SortCollection</c>, verbatim.</summary>
    private static void ReferenceGnomeSort(ObservableCollection<GuitarNote> collection)
    {
        var n = collection.Count;
        for (var i = 1; i < n; i++)
        {
            for (var j = i; j > 0; j--)
            {
                var itemA = collection[j - 1];
                var itemB = collection[j];

                if (itemA.Position > itemB.Position)
                    collection.Move(j - 1, j);
                else
                    break;
            }
        }
    }

    private static List<GuitarNote> Pool(int count, Random rng)
    {
        var notes = new List<GuitarNote>(count);
        var beat = 0;
        while (notes.Count < count)
        {
            var atThisPosition = Math.Min(count - notes.Count, 1 + rng.Next(3));
            for (var s = 0; s < atThisPosition; s++)
            {
                notes.Add(new GuitarNote
                {
                    Position = new FractionalPosition(beat, new Fraction(rng.Next(4), 4)),
                    StringIndex = s,
                    Fret = notes.Count % 22
                });
            }

            beat++;
        }

        notes.Sort((a, b) => a.Position.CompareTo(b.Position));
        return notes;
    }

    private static (ObservableCollection<GuitarNote> Collection, Func<int> Moves) Recording(IEnumerable<GuitarNote> seed)
    {
        var collection = new ObservableCollection<GuitarNote>(seed);
        var moves = 0;
        collection.CollectionChanged += (_, e) =>
        {
            Assert.Equal(NotifyCollectionChangedAction.Move, e.Action);
            moves++;
        };
        return (collection, () => moves);
    }

    /// <summary>Executes a mutate action whose only job is to trigger the resort
    /// — the targets' states are copied by a no-op copy so the positions the
    /// test wrote by hand are what get sorted.</summary>
    private static void Resort(ObservableCollection<GuitarNote> collection, IEnumerable<GuitarNote> targets)
    {
        var list = targets.ToList();
        var action = new MutateElementsAction<GuitarNote>(
            collection, list, list, list,
            copyFn: (_, _) => { },
            stateEquals: ReferenceEquals,
            getPosition: n => n.Position);
        action.Execute();
    }

    [Theory]
    [InlineData(5_000, 3)]
    [InlineData(300, 5)]
    public void OneNoteDraggedAcrossTheChart_IsOneMove_NotOnePerSlot(int size, int seed)
    {
        var rng = new Random(seed);
        var pool = Pool(size, rng);
        var (fast, fastMoves) = Recording(pool);
        var (reference, referenceMoves) = Recording(pool);

        // Drag the third note to the end of the chart: its position now
        // exceeds every other note's.
        var dragged = pool[2];
        var last = pool[^1].Position;
        fast[2].Position = new FractionalPosition(last.BeatId + 4);
        Assert.Same(dragged, reference[2]);

        Resort(fast, [dragged]);
        ReferenceGnomeSort(reference);

        for (var i = 0; i < reference.Count; i++)
            Assert.True(ReferenceEquals(reference[i], fast[i]), $"element {i} differs from the gnome sort's result");

        output.WriteLine($"{size} notes, one dragged to the end: gnome sort raised {referenceMoves()} Move notifications, the resort {fastMoves()}.");
        Assert.Equal(1, fastMoves());
        Assert.Equal(size - 3, referenceMoves());
    }

    [Fact]
    public void ATickThatMovedNothing_RaisesNothing_AndAllocatesNothing()
    {
        var pool = Pool(4_000, new Random(11));
        var (fast, fastMoves) = Recording(pool);
        var targets = pool.Take(200).ToList();

        var action = new MutateElementsAction<GuitarNote>(
            fast, targets, targets, targets,
            copyFn: (_, _) => { },
            stateEquals: ReferenceEquals,
            getPosition: n => n.Position);

        DotCharter.Tests.Infrastructure.AllocationProbe.AssertAllocationFree("a sorted-collection resort tick", action.Execute);
        Assert.Equal(0, fastMoves());
    }

    [Theory]
    [InlineData(2_000, 7, 50)]
    [InlineData(2_000, 13, 600)]
    [InlineData(40, 17, 40)]
    public void ABulkReposition_MatchesTheStableSort_WithOneMovePerDisplacedNote(int size, int seed, int moved)
    {
        var rng = new Random(seed);
        var pool = Pool(size, rng);
        var (fast, fastMoves) = Recording(pool);
        var (reference, referenceMoves) = Recording(pool);

        // Scatter `moved` notes to random new positions — some into an
        // existing equal-position run, so stability is exercised.
        var targets = new List<GuitarNote>();
        foreach (var index in Enumerable.Range(0, size).OrderBy(_ => rng.Next()).Take(moved))
        {
            var note = fast[index];
            note.Position = pool[rng.Next(size)].Position;
            targets.Add(note);
        }

        Resort(fast, targets);
        ReferenceGnomeSort(reference);

        for (var i = 0; i < reference.Count; i++)
            Assert.True(ReferenceEquals(reference[i], fast[i]), $"element {i} differs from the gnome sort's result");

        output.WriteLine($"{size} notes, {moved} repositioned: gnome sort {referenceMoves()} moves, resort {fastMoves()}.");
        Assert.True(fastMoves() <= referenceMoves());
        Assert.True(fastMoves() <= size);
    }
}
