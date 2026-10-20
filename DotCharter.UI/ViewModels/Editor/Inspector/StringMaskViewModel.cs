using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Tone.UI.ViewModels.Shell;

namespace DotCharter.UI.ViewModels.Editor.Inspector;

/// <summary>
///     The inspector's two string-edit-mask verbs (the chord box's rail pips and
///     the scope bars' inline clear) and the keyboard readouts on the Sound tab's
///     queue strip. Exposed as <c>InspectorViewModel.StringMask</c>.
///     <para>
///         The mask itself is <see cref="ProjectState.StringEditMask" /> — the ONE
///         per-string selector the lane headers draw and <c>Ctrl+1..9</c> sets.
///         What the mask TARGETS inside the selected chord (the technique readout
///         and the scope sentences) reads the selection, so it stays on the
///         inspector. The inspector forwards the two <see cref="ProjectState" />
///         changes these readouts follow (<see cref="NotifyMaskChanged" />,
///         <see cref="NotifyPenFretChanged" />).
///     </para>
/// </summary>
public sealed partial class StringMaskViewModel : ViewModelBase
{
    private readonly ProjectState _projectState;
    private readonly ILocalizationService _localization;

    public StringMaskViewModel(ProjectState projectState, ILocalizationService localization)
    {
        _projectState = projectState;
        _localization = localization;
    }

    /// <summary>
    ///     Toggle one string in/out of the string-edit mask — the chord box's
    ///     rail pips and <c>Ctrl</c>+click. Exactly what <c>Ctrl+1..9</c> does;
    ///     there is now ONE place a per-string selection can be made.
    /// </summary>
    [RelayCommand]
    private void ToggleStringMask(int stringIdx) => _projectState.ToggleStringEditMask(stringIdx);

    /// <summary>Drop the mask back to "every string" — the inline action on a
    /// scope bar that reads "this note's string is not in the mask".</summary>
    [RelayCommand]
    private void ClearStringMask() => _projectState.ClearStringEditMask();

    // ---- the keyboard readout (§10.1) --------------------------------------
    //
    // A READOUT, not an editor. Clicking it does nothing: there is exactly ONE
    // place to change the mask (a rail pip, or Ctrl+1..9) and exactly one place
    // the pen fret comes from (typing, or the wheel). The inspector's job here
    // is to SHOW what stages 0–2 shipped, never to compete with it.

    /// <summary>"fret 12" — the ambient placement fret the pen and the wheel
    /// both write.</summary>
    public string PenFretReadout =>
        _localization.Format("Inspector_Queue_PenFret", _projectState.PreviewNoteFret);

    /// <summary>"all strings" or "strings A (5), D (4)" — the string-edit mask,
    /// with every string named the way the lane gutter and the Placement card
    /// name it (<see cref="GuitarStringLabel.MaskReadout" />).</summary>
    public string MaskReadout
    {
        get
        {
            var tuning = _projectState.ActiveArrangement?.Tuning;
            return GuitarStringLabel.MaskReadout(_projectState.StringEditMask, tuning?.Length ?? 6, tuning,
                _localization);
        }
    }

    /// <summary><see cref="ProjectState.StringEditMask" /> moved.</summary>
    internal void NotifyMaskChanged()
    {
        OnPropertyChanged(nameof(MaskReadout));
        LightChips();
    }

    // ---- the string chips -------------------------------------------

    /// <summary>
    ///     One chip per string the selected chord(s) play, lowest first, lit when the string-edit
    ///     mask names it — the Sound tab's Technique section, "Per string". A chip toggles the SAME
    ///     mask the chord box's rail pips and <c>Ctrl+1..9</c> set (<see cref="ToggleStringMaskCommand" />),
    ///     so a per-string technique can go to one string of a chord without leaving the section.
    ///     Each chip is named the way the lane gutter names the string ("A (5)",
    ///     <see cref="GuitarStringLabel" />), like the scope sentence beside them.
    /// </summary>
    public ObservableCollection<StringMaskChip> Chips { get; } = [];

    /// <summary>
    ///     Re-reads the chips for the strings the selected chords play: the set only when it
    ///     changed (a chip row is not rebuilt on every edit), the lit state always.
    /// </summary>
    internal void SyncChips(IEnumerable<int> strings, int stringCount, IReadOnlyList<string>? tuning)
    {
        var wanted = strings.Distinct().Order().ToList();
        if (!wanted.SequenceEqual(Chips.Select(c => c.StringIndex)))
        {
            Chips.Clear();
            var format = _localization["Inspector_StringLabelFmt"];
            foreach (var index in wanted)
            {
                Chips.Add(new StringMaskChip(index, GuitarStringLabel.Format(index, stringCount, tuning, format),
                    StringColorSlots.For(index, stringCount)));
            }
        }

        LightChips();
    }

    private void LightChips()
    {
        var mask = _projectState.StringEditMask;
        foreach (var chip in Chips) chip.IsInMask = mask.IsExplicit(chip.StringIndex);
    }

    /// <summary><see cref="ProjectState.PreviewNoteFret" /> moved.</summary>
    internal void NotifyPenFretChanged() => OnPropertyChanged(nameof(PenFretReadout));
}

/// <summary>One string of the selected chord(s) on the Sound tab's "Per string" row.</summary>
public sealed partial class StringMaskChip(int stringIndex, string name, int colorSlot) : ObservableObject
{
    /// <summary>The MODEL index (0 = the lowest string) — the toggle's argument.</summary>
    public int StringIndex { get; } = stringIndex;

    /// <summary>The chip's colour: the string's palette slot (<see cref="StringColorSlots.For" />), so a
    /// 7-string's low E chip is red like its lane.</summary>
    public int ColorSlot { get; } = colorSlot;

    /// <summary>The gutter's name for the string ("A (5)").</summary>
    public string Name { get; } = name;

    /// <summary>The string is in the string-edit mask.</summary>
    [ObservableProperty] private bool _isInMask;
}
