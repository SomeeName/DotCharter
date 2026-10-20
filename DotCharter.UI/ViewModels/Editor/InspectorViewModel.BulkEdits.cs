// InspectorViewModel: the multi-selection verbs - fret/string steps, reset flags, apply the current values to every selected sound, and set a finger on the masked strings.

using System.Globalization;
using CommunityToolkit.Mvvm.Input;

namespace DotCharter.UI.ViewModels.Editor;

public partial class InspectorViewModel
{
    // ---- the bulk verbs (§4.4) ---------------------------------------------
    //
    // These are NOT new commands: they are the existing SelectedNoteMutation
    // verbs that until now existed ONLY as keybinds, appearing in no menu and no
    // palette. A user who never opens the Keybinds tab could not discover that
    // they exist. Surfacing them on the multi-selection card is the cheapest
    // possible fix, and it is the ONLY state where per-field editors are
    // meaningless anyway.

    /// <summary>Fret ±1 / ±12. The octave step is twelve single moves, exactly
    /// as the keybind does it, so the clamping and template repointing stay one
    /// implementation.</summary>
    /// <remarks>
    ///     The parameter is a STRING because it comes from a XAML
    ///     <c>CommandParameter="1"</c> literal, which is a string. Declaring it
    ///     <c>int</c> makes CommunityToolkit generate a <c>RelayCommand&lt;int&gt;</c>
    ///     whose <c>CanExecute</c> THROWS <see cref="ArgumentException" /> the moment
    ///     the Button attaches to the logical tree — an unhandled crash during
    ///     layout, not a binding warning. `SheetMusicPaneViewModel.ApplyDurationValue`
    ///     already takes a string for exactly this reason; keep them consistent.
    /// </remarks>
    [RelayCommand]
    private void BulkFret(string? deltaText)
    {
        if (!int.TryParse(deltaText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var delta)) return;
        var steps = Math.Abs(delta);
        for (var i = 0; i < steps; i++)
        {
            if (delta > 0) _highwayEditingService.MoveSelectedFretUp();
            else _highwayEditingService.MoveSelectedFretDown();
        }
    }

    /// <summary>String ±1. On a chord this moves the WHOLE shape or none of it —
    /// clamping per string would stack two chord notes on one lane, which the
    /// model, the renderers and the SNG's fixed-size arrays cannot express.</summary>
    /// <remarks>
    ///     The parameter is a STRING because it comes from a XAML
    ///     <c>CommandParameter="1"</c> literal, which is a string. Declaring it
    ///     <c>int</c> makes CommunityToolkit generate a <c>RelayCommand&lt;int&gt;</c>
    ///     whose <c>CanExecute</c> THROWS <see cref="ArgumentException" /> the moment
    ///     the Button attaches to the logical tree — an unhandled crash during
    ///     layout, not a binding warning. `SheetMusicPaneViewModel.ApplyDurationValue`
    ///     already takes a string for exactly this reason; keep them consistent.
    /// </remarks>
    [RelayCommand]
    private void BulkString(string? deltaText)
    {
        if (!int.TryParse(deltaText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var delta)) return;
        if (delta > 0) _highwayEditingService.MoveSelectedStringUp(NoteStringCount);
        else _highwayEditingService.MoveSelectedStringDown();
    }

    // ----- Bottom-action-bar commands (UX7) -----

    // "Reset" wipes all binary flags on every selected note/chord back to
    // their default (false). Doesn't touch fret/string/sustain/technique
    // ints — those have meaningful "unset" representations elsewhere.

    [RelayCommand]
    private void ResetNoteFlags()
    {
        if (_isSyncing || !SelectedNotes.Any()) return;
        ApplyNoteMutation(n =>
        {
            n.Accent = false;
            SetVibrato(n, false);
            n.LinkNext = false;
            n.PassOtherNotes = false;
            n.Ignore = false;
            n.Tremolo = false;
            n.UnpitchedSlide = false;
        });
    }

    [RelayCommand]
    private void ResetChordFlags()
    {
        if (_isSyncing || !SelectedChords.Any()) return;
        ApplyChordMutation(c =>
        {
            c.Accent = false;
            c.SplitIntoNotes = false;
            c.ForceNoNotes = false;
            c.PassOtherNotes = false;
            c.Ignore = false;
        });
    }

    // "Apply current values to all selected" — re-issues the current
    // chip/combo state of the inspector to every note in SelectedNotes,
    // even when the chip is indeterminate (in which case we treat the
    // indeterminate slot as "leave alone"). Useful when the user picks
    // a multi-selection, sets a couple of chips explicitly, and wants
    // to broadcast that exact picture to every member without iterating.

    [RelayCommand]
    private void ApplyNoteValuesToAll()
    {
        if (_isSyncing || !SelectedNotes.Any()) return;

        // "Indeterminate = leave alone" has to hold for the four ENUM pickers
        // too, and it did not. `Seg` has no indeterminate state, so Mute / HOPO /
        // Harmonic / Picking show the FIRST selected sound's value — and these
        // four lines wrote it back UNCONDITIONALLY, so one click stamped that
        // sound's palm mute (or HOPO, or pinch harmonic, or slap/pop) over every
        // other sound in a mixed selection, in the same gesture that carefully
        // skipped every indeterminate chip. NoteEnums remembers whether the
        // selection actually agreed; a picker the user has since written is
        // recorded as agreed, because that write already reached every selected
        // sound through ApplyNoteMutation.
        var enums = NoteEnums;
        ApplyNoteMutation(n =>
        {
            if (NoteAccent.HasValue) n.Accent = NoteAccent.Value;
            if (NoteVibrato.HasValue) SetVibrato(n, NoteVibrato.Value);
            if (NoteLinkNext.HasValue) n.LinkNext = NoteLinkNext.Value;
            if (NotePassOtherNotes.HasValue) n.PassOtherNotes = NotePassOtherNotes.Value;
            if (NoteIgnore.HasValue) n.Ignore = NoteIgnore.Value;
            if (NoteTremolo.HasValue) n.Tremolo = NoteTremolo.Value;
            if (NoteUnpitchedSlide.HasValue) n.UnpitchedSlide = NoteUnpitchedSlide.Value;
            if (enums.Mute.HasValue) n.Mute = (MuteType)enums.Mute.Value;
            if (enums.Hopo.HasValue) n.Hopo = (HopoType)enums.Hopo.Value;
            if (enums.Harmonic.HasValue) n.Harmonic = (HarmonicType)enums.Harmonic.Value;
            if (enums.BassPicking.HasValue) n.BassPicking = (BassPickingType)enums.BassPicking.Value;
        });
    }

    [RelayCommand]
    private void ApplyChordValuesToAll()
    {
        if (_isSyncing || !SelectedChords.Any()) return;
        ApplyChordMutation(c =>
        {
            if (ChordAccent.HasValue) c.Accent = ChordAccent.Value;
            if (ChordSplitIntoNotes.HasValue) c.SplitIntoNotes = ChordSplitIntoNotes.Value;
            if (ChordForceNoNotes.HasValue) c.ForceNoNotes = ChordForceNoNotes.Value;
            if (ChordPassOtherNotes.HasValue) c.PassOtherNotes = ChordPassOtherNotes.Value;
            if (ChordIgnore.HasValue) c.Ignore = ChordIgnore.Value;
        });
    }

    /// <summary>
    /// Set the fingering for the strings the string-edit mask targets. An EMPTY
    /// mask means every string of the active chord template — the same "empty =
    /// all" reading every other per-string verb uses. fingerId: -1 =
    /// unspecified, 0 = thumb, 1..4 = index/middle/ring/pinky. Used by the
    /// SetFingerNone/Thumb/1..4 keybinds.
    /// </summary>
    [RelayCommand]
    private void SetFingerOnSelectedStrings(int fingerId)
    {
        if (SelectedChord == null) return;
        var mask = _projectState.StringEditMask;
        // null, not an empty list, for "every string": an empty list means NO
        // strings, which is the honest answer when the mask is set and this
        // chord plays none of the masked ones. Passing an empty list for both
        // made that case write the finger to the WHOLE chord - the opposite of
        // what the mask asked for. The scope bar already names this state
        // (Inspector_Scope_NoStringInMask), and the sibling funnel
        // ApplyChordNoteMutation no-ops on it through MaskedChordNotes.
        var indices = mask.IsEmpty
            ? null
            : SelectedChord.ChordNotes.Where(n => mask.IsExplicit(n.StringIndex))
                .Select(n => n.StringIndex).ToList();
        ChordTemplateEditor.SetFingerForStrings(fingerId, indices);
    }
}
