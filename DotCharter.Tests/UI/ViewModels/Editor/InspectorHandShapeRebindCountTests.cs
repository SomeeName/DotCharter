using DotCharter.UI.ViewModels.Editor.Inspector;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     The Shape card's rebind warning states, BEFORE the edit, how many chords
///     a shape edit would rebind and reshape — the set
///     <c>ChordApplyScope.EnclosedUnbound</c> acts on.
///     <para>
///         It open-coded a weaker test: enclosed by the bar and carrying a
///         DIFFERENT template id. The real rule also requires the chord to
///         already SOUND the bar's grip — a chord of another shape under the
///         same bar is deliberately left alone — so the warning over-counted,
///         and a bar drawn over a riff of mixed shapes claimed it would rebind
///         sounds no edit would ever touch.
///     </para>
/// </summary>
public sealed class InspectorHandShapeRebindCountTests
{
    [Fact]
    public void ChordsOfAnotherShapeUnderTheSameBar_AreNotCounted()
    {
        var shape = Template(0, (1, 7), (2, 7));
        var bar = Bar(0, 0, 16);

        var matching = Chord(-1, 0, (1, 7), (2, 7));
        var otherShape = Chord(-1, 4, (3, 3), (4, 5));
        var samePartialGrip = Chord(-1, 8, (1, 7)); // one string of the grip, not the grip

        var count = HandShapeEditorViewModel.CountRebindableChords([matching, otherShape, samePartialGrip], bar, shape);

        Assert.Equal(1, count);
    }

    [Fact]
    public void AlreadyBoundAndOutOfRangeChords_AreNotCounted()
    {
        var shape = Template(0, (1, 7), (2, 7));
        var bar = Bar(0, 0, 16);

        var bound = Chord(0, 0, (1, 7), (2, 7));
        var afterTheEnd = Chord(-1, 16, (1, 7), (2, 7)); // end is EXCLUSIVE
        var inside = Chord(-1, 12, (1, 7), (2, 7));

        Assert.Equal(1, HandShapeEditorViewModel.CountRebindableChords([bound, afterTheEnd, inside], bar, shape));
    }

    [Fact]
    public void ABarWhoseTemplateIsMissing_CountsNothing()
    {
        // A dangling TemplateId declares no grip, so a shape edit has nothing to
        // project onto the enclosed chords.
        var bar = Bar(-1, 0, 16);
        var inside = Chord(-1, 4, (1, 7), (2, 7));

        Assert.Equal(0, HandShapeEditorViewModel.CountRebindableChords([inside], bar, null));
    }

    private static ChordTemplate Template(int id, params (int stringIndex, int fret)[] frets)
    {
        var template = new ChordTemplate { Id = id, Name = "A5" };
        foreach (var (stringIndex, fret) in frets) template.Frets[stringIndex] = fret;
        return template;
    }

    private static HandShape Bar(int templateId, int start, int end) => new()
    {
        TemplateId = templateId,
        Position = new FractionalPosition(start),
        EndPosition = new FractionalPosition(end)
    };

    private static Chord Chord(int templateId, int beat, params (int stringIndex, int fret)[] notes)
    {
        var chord = new Chord { TemplateId = templateId, Position = new FractionalPosition(beat) };
        foreach (var (stringIndex, fret) in notes)
            chord.ChordNotes.Add(new ChordNote { StringIndex = stringIndex, Fret = fret });
        return chord;
    }
}
