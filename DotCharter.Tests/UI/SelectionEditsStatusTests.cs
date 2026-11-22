using DotCharter.Localization;
using DotCharter.Services.Editing.Selection;
using DotCharter.UI.ViewModels.Shell;

namespace DotCharter.Tests.UI;

/// <summary>
///     The words the status rail says after select-similar, a saved pattern and a technique toggle
///     (#369, #371): "Added 12 notes, 2 chords (14 selected)", "Hammer-on set on 9 items".
/// </summary>
public sealed class SelectionEditsStatusTests
{
    private static readonly ILocalizationService L = FakeLocalization.English();

    [Fact]
    public void An_addition_lists_each_kind_that_grew_and_the_new_total()
    {
        var (text, severity) = SelectionAdditionStatus.Describe(
            new SelectionAddition(SelectionAddOutcome.Searched, 12, 2, 0, 0, 14), L);

        Assert.Equal("Added 12 notes, 2 chords (14 selected)", text);
        Assert.Equal(StatusSeverity.Info, severity);
    }

    [Fact]
    public void One_of_a_kind_uses_the_singular_and_hand_shapes_and_anchors_are_named()
    {
        var (text, _) = SelectionAdditionStatus.Describe(
            new SelectionAddition(SelectionAddOutcome.Searched, 1, 0, 3, 1, 6), L);

        Assert.Equal("Added 1 note, 3 hand shapes, 1 anchor (6 selected)", text);
    }

    [Fact]
    public void A_search_that_found_nothing_says_so_as_information()
    {
        var (text, severity) = SelectionAdditionStatus.Describe(
            new SelectionAddition(SelectionAddOutcome.Searched, 0, 0, 0, 0, 3), L);

        Assert.Equal("Nothing similar found.", text);
        Assert.Equal(StatusSeverity.Info, severity);
    }

    [Theory]
    [InlineData(SelectionAddOutcome.NothingSelected, "Select a note")]
    [InlineData(SelectionAddOutcome.NoTrack, "Select a note")]
    [InlineData(SelectionAddOutcome.ForeignSeed, "another arrangement")]
    public void A_refused_run_says_why_as_a_warning(SelectionAddOutcome outcome, string fragment)
    {
        var (text, severity) = SelectionAdditionStatus.Describe(SelectionAddition.None(outcome), L);

        Assert.Contains(fragment, text);
        Assert.Equal(StatusSeverity.Warning, severity);
    }

    [Fact]
    public void A_technique_every_note_now_carries_reads_as_set_with_the_kind()
    {
        var notes = Enumerable.Range(0, 9).Select(_ => new GuitarNote { Hopo = HopoType.HammerOn }).ToList();

        var (text, severity) = TechniqueToggleStatus.Describe(
            TechniqueSpec.Hopo, TechniqueSpec.Hopo.StatesOf(notes, []).ToList(), L);

        Assert.Equal("Hammer-on set on 9 items", text);
        Assert.Equal(StatusSeverity.Info, severity);
    }

    [Fact]
    public void The_next_kind_in_a_cycle_is_named_not_called_a_hammer_on()
    {
        var notes = new[] { new GuitarNote { Hopo = HopoType.PullOff } };

        var (text, _) = TechniqueToggleStatus.Describe(
            TechniqueSpec.Hopo, TechniqueSpec.Hopo.StatesOf(notes, []).ToList(), L);

        Assert.Equal("Pull-off set on 1 item", text);
    }

    [Fact]
    public void A_technique_no_note_carries_reads_as_cleared()
    {
        var notes = new[] { new GuitarNote(), new GuitarNote() };

        var (text, _) = TechniqueToggleStatus.Describe(
            TechniqueSpec.Vibrato, TechniqueSpec.Vibrato.StatesOf(notes, []).ToList(), L);

        Assert.Equal("Vibrato cleared on 2 items", text);
    }

    [Fact]
    public void Notes_that_ended_up_different_read_as_toggled()
    {
        var notes = new[] { new GuitarNote { Tremolo = true }, new GuitarNote() };

        var (text, _) = TechniqueToggleStatus.Describe(
            TechniqueSpec.Tremolo, TechniqueSpec.Tremolo.StatesOf(notes, []).ToList(), L);

        Assert.Equal("Tremolo toggled on 2 items", text);
    }

    [Fact]
    public void A_chord_is_read_through_its_notes_or_its_own_flag()
    {
        var chord = new Chord { Accent = true };
        chord.ChordNotes.Add(new ChordNote { Mute = MuteType.Palm });
        chord.ChordNotes.Add(new ChordNote { Mute = MuteType.Palm });

        var accent = TechniqueSpec.Accent.StatesOf([], [chord]).ToList();
        var mute = TechniqueSpec.Mute.StatesOf([], [chord]).ToList();

        Assert.Equal(["Technique_Accent"], accent);
        Assert.Equal(["Technique_Mute_Palm", "Technique_Mute_Palm"], mute);
    }

    [Fact]
    public void Nothing_selected_is_a_warning_not_a_cleared_message()
    {
        var (text, severity) = TechniqueToggleStatus.Describe(TechniqueSpec.Hopo, [], L);

        Assert.Equal("Select notes or chords first.", text);
        Assert.Equal(StatusSeverity.Warning, severity);
    }
}
