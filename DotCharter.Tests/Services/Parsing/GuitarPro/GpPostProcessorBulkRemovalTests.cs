using DotCharter.Services.Parsing.GuitarPro;

namespace DotCharter.Tests.Services.Parsing.GuitarPro;

/// <summary>
///     Result coverage for the two import passes that DELETE sounds —
///     <c>RemoveDuplicateSounds</c> and <c>MergeTiedNotes</c> — which had none of
///     their own beyond the rules they implement.
///     <para>
///     The 2026-07-25 audit (its 5.9) proposed replacing their per-item
///     <c>ObservableCollection.Remove</c> with a mark-and-rebuild, since each
///     <c>Remove</c> is a linear scan plus a <c>CollectionChanged</c> raise. That
///     was MEASURED and rejected: with 100 000 notes and 50 000 of them doomed —
///     far past any real chart — the per-item version still finishes in about a
///     second, and for the actual case (a handful of duplicates in a few thousand
///     notes) a rebuild is strictly WORSE, trading `m` events for `n`. These
///     tests are what the change would have needed, and are worth keeping on
///     their own.
///     </para>
/// </summary>
public sealed class GpPostProcessorBulkRemovalTests
{
    [Fact]
    public void RemoveDuplicateSounds_KeepsTheSurvivorsInTheirOriginalOrder()
    {
        var level = new ChartLevel();
        // Positions 0..4 on one string; 1 and 3 are duplicated, and the duplicate
        // carries no extra information so the FIRST one wins.
        for (var i = 0; i < 5; i++)
        {
            level.Notes.Add(new GuitarNote
            { StringIndex = 0, Fret = i, Position = new FractionalPosition(i) });
            if (i is 1 or 3)
            {
                level.Notes.Add(new GuitarNote
                { StringIndex = 0, Fret = 90 + i, Position = new FractionalPosition(i) });
            }
        }

        GpPostProcessor.RemoveDuplicateSounds(level);

        Assert.Equal([0, 1, 2, 3, 4], level.Notes.Select(n => n.Fret).ToArray());
    }

    [Fact]
    public void RemoveDuplicateSounds_ANoteAChordAlreadyClaimsIsDropped()
    {
        var level = new ChartLevel();
        var chord = new Chord { Position = new FractionalPosition(1) };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 2, Fret = 7 });
        level.Chords.Add(chord);

        level.Notes.Add(new GuitarNote
        { StringIndex = 2, Fret = 3, Position = new FractionalPosition(0) });
        level.Notes.Add(new GuitarNote
        { StringIndex = 2, Fret = 5, Position = new FractionalPosition(1) });
        level.Notes.Add(new GuitarNote
        { StringIndex = 2, Fret = 9, Position = new FractionalPosition(2) });

        GpPostProcessor.RemoveDuplicateSounds(level);

        Assert.Equal([3, 9], level.Notes.Select(n => n.Fret).ToArray());
    }

    /// <summary>Nothing doomed must leave the collection completely untouched —
    /// including raising no events at all.</summary>
    [Fact]
    public void RemoveDuplicateSounds_WithNothingToRemove_RaisesNoCollectionEvent()
    {
        var level = new ChartLevel();
        for (var i = 0; i < 4; i++)
        {
            level.Notes.Add(new GuitarNote
            { StringIndex = i, Fret = i, Position = new FractionalPosition(i) });
        }

        var events = 0;
        level.Notes.CollectionChanged += (_, _) => events++;

        GpPostProcessor.RemoveDuplicateSounds(level);

        Assert.Equal(0, events);
        Assert.Equal(4, level.Notes.Count);
    }

    [Fact]
    public void MergeTiedNotes_MergesAChainAndKeepsTheRemainingOrder()
    {
        var level = new ChartLevel();
        // Three quarter notes on one string, each linked into the next.
        for (var i = 0; i < 3; i++)
        {
            level.Notes.Add(new GuitarNote
            {
                StringIndex = 0,
                Fret = 5,
                Position = new FractionalPosition(i),
                Sustain = new FractionalPosition(1),
                LinkNext = i < 2
            });
        }

        // ...and one unrelated note on another string, after them in the list.
        level.Notes.Add(new GuitarNote
        { StringIndex = 1, Fret = 9, Position = new FractionalPosition(1) });

        GpPostProcessor.MergeTiedNotes(level, 6);

        Assert.Equal(2, level.Notes.Count);
        Assert.Equal(5, level.Notes[0].Fret);
        Assert.Equal(0, level.Notes[0].Sustain.CompareTo(new FractionalPosition(3)));
        Assert.Equal(9, level.Notes[1].Fret);
    }

    [Fact]
    public void MergeTiedNotes_MergesTiedChords()
    {
        var level = new ChartLevel();
        for (var i = 0; i < 2; i++)
        {
            var chord = new Chord { Position = new FractionalPosition(i) };
            for (var s = 0; s < 3; s++)
            {
                chord.ChordNotes.Add(new ChordNote
                {
                    StringIndex = s,
                    Fret = 3,
                    Sustain = new FractionalPosition(1),
                    LinkNext = i == 0
                });
            }

            level.Chords.Add(chord);
        }

        GpPostProcessor.MergeTiedNotes(level, 6);

        var merged = Assert.Single(level.Chords);
        Assert.All(merged.ChordNotes,
            cn => Assert.Equal(0, cn.Sustain.CompareTo(new FractionalPosition(2))));
    }

    /// <summary>A level far denser than any real chart still resolves correctly —
    /// the bound this pass actually has to meet.</summary>
    [Fact]
    public void RemoveDuplicateSounds_ResolvesADenseLevel()
    {
        const int pairs = 20_000;
        var level = new ChartLevel();
        for (var i = 0; i < pairs; i++)
        {
            var pos = new FractionalPosition(i);
            level.Notes.Add(new GuitarNote { StringIndex = 0, Fret = 5, Position = pos });
            level.Notes.Add(new GuitarNote { StringIndex = 0, Fret = 7, Position = pos });
        }

        GpPostProcessor.RemoveDuplicateSounds(level);

        Assert.Equal(pairs, level.Notes.Count);
        Assert.All(level.Notes, n => Assert.Equal(5, n.Fret));
    }
}
