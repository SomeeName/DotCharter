using DotCharter.Services.Editing.DraftMerge;
using static DotCharter.Tests.Services.Editing.DraftMerge.DraftMergeFixture;

namespace DotCharter.Tests.Services.Editing.DraftMerge;

/// <summary>
///     The per-phrase diff of a chart-from-audio draft against the sounds already there (#647):
///     counted per string at each position, independent of either list's order.
/// </summary>
public sealed class DraftPhraseDiffTests
{
    [Fact]
    public void A_phrase_with_no_notes_takes_every_draft_note_as_added()
    {
        var diff = DraftPhraseDiff.Compute([], [], [Note(0, 0, 3), Note(1, 1, 5), Note(1, 2, 5)]);

        Assert.Equal(new DraftPhraseDiff(3, 0, 0, 0), diff);
        Assert.False(diff.IsEmpty);
    }

    [Fact]
    public void An_identical_draft_changes_nothing()
    {
        GuitarNote[] existing = [Note(0, 0, 3), Note(1, 1, 5), Note(2, 4, 0)];
        GuitarNote[] draft = [Note(2, 4, 0), Note(0, 0, 3), Note(1, 1, 5)];

        var diff = DraftPhraseDiff.Compute(existing, [], draft);

        Assert.Equal(new DraftPhraseDiff(0, 0, 0, 3), diff);
        Assert.True(diff.IsEmpty);
    }

    [Fact]
    public void A_moved_fret_is_one_change_not_an_add_and_a_remove()
    {
        var diff = DraftPhraseDiff.Compute([Note(0, 0, 3), Note(1, 1, 5)], [], [Note(0, 0, 5), Note(1, 1, 5)]);

        Assert.Equal(new DraftPhraseDiff(0, 1, 0, 1), diff);
    }

    [Fact]
    public void A_moved_string_at_the_same_position_is_a_change()
    {
        var diff = DraftPhraseDiff.Compute([Note(0, 0, 5)], [], [Note(0, 1, 0)]);

        Assert.Equal(new DraftPhraseDiff(0, 1, 0, 0), diff);
    }

    [Fact]
    public void A_sound_the_draft_has_nothing_for_is_removed_and_a_new_position_is_added()
    {
        var diff = DraftPhraseDiff.Compute([Note(0, 0, 3), Note(2, 0, 3)], [], [Note(0, 0, 3), Note(3, 2, 2)]);

        Assert.Equal(new DraftPhraseDiff(1, 0, 1, 1), diff);
    }

    [Fact]
    public void An_existing_chord_counts_one_sound_per_chord_note()
    {
        var chord = Chord(0, 4, (0, 3), (1, 5), (2, 5));

        var diff = DraftPhraseDiff.Compute([], [chord], [Note(0, 0, 3), Note(0, 1, 7)]);

        // String 0 restated, string 1 re-fretted, string 2 has no draft note.
        Assert.Equal(new DraftPhraseDiff(0, 1, 1, 1), diff);
    }

    [Fact]
    public void A_chord_without_chord_notes_is_one_sound_no_draft_note_restates()
    {
        var diff = DraftPhraseDiff.Compute([], [new Chord { Position = new FractionalPosition(0), TemplateId = 2 }], [Note(0, 0, 3)]);

        Assert.Equal(new DraftPhraseDiff(0, 1, 0, 0), diff);
    }
}
