namespace DotCharter.Tests.Services.State;

/// <summary>
///     <c>MutateElementsAction.HasEffect</c> asks
///     <c>ChartActionStateComparer.ChordsEqual</c> whether an edit changed
///     anything, and drops the whole action — silently, with no history node —
///     when it says "no". A field missing from the comparer is therefore a field
///     that cannot be edited at all through the mutate path.
///     <para>
///         <c>ChordNote.Accent</c> and <c>ChordNote.Ignore</c> were both missing.
///         Both are real model fields the <c>.rscp</c> round-trips (accent also
///         reaches RS-XML and the SNG mask), and the chord inspector's per-string
///         chips already write <c>Ignore</c>.
///     </para>
/// </summary>
public sealed class ChordNoteStateComparerTests
{
    [Fact]
    public void AChordNoteIgnoreOnlyChangeIsNotAnEquality()
    {
        var before = Chord2(c => { });
        var after = Chord2(c => c.ChordNotes[1].Ignore = true);

        Assert.False(ChartActionStateComparer.ChordsEqual(before, after));
    }

    [Fact]
    public void AChordNoteAccentOnlyChangeIsNotAnEquality()
    {
        var before = Chord2(c => { });
        var after = Chord2(c => c.ChordNotes[0].Accent = true);

        Assert.False(ChartActionStateComparer.ChordsEqual(before, after));
    }

    [Fact]
    public void AnUnchangedChordStillCompareEqual() => Assert.True(ChartActionStateComparer.ChordsEqual(Chord2(_ => { }), Chord2(_ => { })));

    private static Chord Chord2(Action<Chord> mutate)
    {
        var chord = new Chord { Position = new FractionalPosition(2), TemplateId = 0 };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 3 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 5 });
        mutate(chord);
        return chord;
    }
}
