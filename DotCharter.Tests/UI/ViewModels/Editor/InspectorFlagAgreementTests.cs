using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     The inspector's tri-state chips used to be seven (notes) and five (chords)
///     separate <c>AllSameNullable</c> walks over a <c>ToList()</c> snapshot of the
///     selection — eight and six full passes plus a full copy, and the whole sync
///     runs TWICE per selection edit (the selection service re-derives its scalar
///     <c>SelectedNote</c> from inside its own <c>CollectionChanged</c> handler, so
///     the view model is signalled on both). One pass now does all of them.
///     <para>
///         "All of them at once" is only allowed to be the same answer, so this
///         compares against <c>AllSameNullable</c> itself — the walk the chips have
///         always used, still <c>internal</c> and still tested in its own right —
///         over every homogeneous, every one-note-disagrees and a batch of random
///         selections.
///     </para>
/// </summary>
public sealed class InspectorFlagAgreementTests
{
    private static GuitarNote Note(Random rng) => new()
    {
        StringIndex = rng.Next(6),
        Fret = rng.Next(22),
        Accent = rng.Next(2) == 0,
        Vibrato = rng.Next(2) == 0,
        LinkNext = rng.Next(2) == 0,
        PassOtherNotes = rng.Next(2) == 0,
        Ignore = rng.Next(2) == 0,
        Tremolo = rng.Next(2) == 0,
        UnpitchedSlide = rng.Next(2) == 0
    };

    private static Chord ChordOf(Random rng) => new()
    {
        Accent = rng.Next(2) == 0,
        SplitIntoNotes = rng.Next(2) == 0,
        ForceNoNotes = rng.Next(2) == 0,
        PassOtherNotes = rng.Next(2) == 0,
        Ignore = rng.Next(2) == 0
    };

    private static void AssertNotesAgree(List<GuitarNote> notes, string what)
    {
        var agreed = InspectorViewModel.NoteFlagAgreement.Over(notes);
        Assert.Equal(InspectorViewModel.AllSameNullable(notes, n => n.Accent), agreed.Accent);
        Assert.Equal(InspectorViewModel.AllSameNullable(notes, n => n.Vibrato), agreed.Vibrato);
        Assert.Equal(InspectorViewModel.AllSameNullable(notes, n => n.LinkNext), agreed.LinkNext);
        Assert.Equal(InspectorViewModel.AllSameNullable(notes, n => n.PassOtherNotes), agreed.PassOtherNotes);
        Assert.Equal(InspectorViewModel.AllSameNullable(notes, n => n.Ignore), agreed.Ignore);
        Assert.Equal(InspectorViewModel.AllSameNullable(notes, n => n.Tremolo), agreed.Tremolo);
        Assert.Equal(InspectorViewModel.AllSameNullable(notes, n => n.UnpitchedSlide), agreed.UnpitchedSlide);
        Assert.True(true, what);
    }

    private static void AssertChordsAgree(List<Chord> chords)
    {
        var agreed = InspectorViewModel.ChordFlagAgreement.Over(chords);
        Assert.Equal(InspectorViewModel.AllSameNullable(chords, c => c.Accent), agreed.Accent);
        Assert.Equal(InspectorViewModel.AllSameNullable(chords, c => c.SplitIntoNotes), agreed.SplitIntoNotes);
        Assert.Equal(InspectorViewModel.AllSameNullable(chords, c => c.ForceNoNotes), agreed.ForceNoNotes);
        Assert.Equal(InspectorViewModel.AllSameNullable(chords, c => c.PassOtherNotes), agreed.PassOtherNotes);
        Assert.Equal(InspectorViewModel.AllSameNullable(chords, c => c.Ignore), agreed.Ignore);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(13)]
    public void RandomSelections_agreeWithTheSevenSeparateWalks(int seed)
    {
        var rng = new Random(seed);
        for (var size = 0; size <= 40; size++)
        {
            var notes = new List<GuitarNote>();
            for (var i = 0; i < size; i++) notes.Add(Note(rng));
            AssertNotesAgree(notes, $"{size} notes, seed {seed}");

            var chords = new List<Chord>();
            for (var i = 0; i < size; i++) chords.Add(ChordOf(rng));
            AssertChordsAgree(chords);
        }
    }

    /// <summary>
    ///     The case the chips exist for: everything agrees except ONE note, on ONE
    ///     flag. That flag goes indeterminate; the other six must not.
    /// </summary>
    [Fact]
    public void OneDisagreeingNote_takesOnlyItsOwnFlagIndeterminate()
    {
        for (var flag = 0; flag < 7; flag++)
        {
            var notes = new List<GuitarNote>();
            for (var i = 0; i < 12; i++)
            {
                notes.Add(new GuitarNote
                {
                    StringIndex = i % 6, Fret = 3,
                    Accent = true, Vibrato = true, LinkNext = true,
                    PassOtherNotes = true, Ignore = true, Tremolo = true, UnpitchedSlide = true
                });
            }

            var odd = notes[7];
            switch (flag)
            {
                case 0: odd.Accent = false; break;
                case 1: odd.Vibrato = false; break;
                case 2: odd.LinkNext = false; break;
                case 3: odd.PassOtherNotes = false; break;
                case 4: odd.Ignore = false; break;
                case 5: odd.Tremolo = false; break;
                default: odd.UnpitchedSlide = false; break;
            }

            var agreed = InspectorViewModel.NoteFlagAgreement.Over(notes);
            var values = new[]
            {
                agreed.Accent, agreed.Vibrato, agreed.LinkNext, agreed.PassOtherNotes,
                agreed.Ignore, agreed.Tremolo, agreed.UnpitchedSlide
            };

            for (var i = 0; i < values.Length; i++)
            {
                if (i == flag) Assert.Null(values[i]);
                else Assert.True(values[i], $"flag {i} went indeterminate when only flag {flag} disagreed.");
            }

            AssertNotesAgree(notes, $"one note disagreeing on flag {flag}");
        }
    }

    /// <summary>An empty selection is every flag indeterminate, as the walk was.</summary>
    [Fact]
    public void AnEmptySelection_isIndeterminateEverywhere()
    {
        var agreed = InspectorViewModel.NoteFlagAgreement.Over([]);
        Assert.Null(agreed.Accent);
        Assert.Null(agreed.Vibrato);
        Assert.Null(agreed.LinkNext);
        Assert.Null(agreed.PassOtherNotes);
        Assert.Null(agreed.Ignore);
        Assert.Null(agreed.Tremolo);
        Assert.Null(agreed.UnpitchedSlide);

        var chords = InspectorViewModel.ChordFlagAgreement.Over([]);
        Assert.Null(chords.Accent);
        Assert.Null(chords.SplitIntoNotes);
        Assert.Null(chords.ForceNoNotes);
        Assert.Null(chords.PassOtherNotes);
        Assert.Null(chords.Ignore);
    }
}
