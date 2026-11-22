using CommunityToolkit.Mvvm.Input;

namespace DotCharter.UI.ViewModels.Editor.Inspector;

/// <summary>
///     The Technique page of a note (design D's "Note techniques" and "Note detail" cards): every technique,
///     the finger and the pick direction STAGED (<see cref="InspectorStagedEdits.StageTechnique" />) and written by the
///     footer's Apply on this note or every selected note (the footer's scope). A choice is its enum's value (the
///     combo's index); the finger's index is the finger + 1 (0 = unset, 1 = thumb).
/// </summary>
public sealed partial class StagedNoteTechniqueViewModel(InspectorStagedPages owner) : StagedPageViewModel(owner)
{
    private int _selectedNotes;

    public int Mute { get => Choice(TechniqueField.Mute); set => Stage(TechniqueField.Mute, value); }

    public int Hopo { get => Choice(TechniqueField.Hopo); set => Stage(TechniqueField.Hopo, value); }

    public int Harmonic { get => Choice(TechniqueField.Harmonic); set => Stage(TechniqueField.Harmonic, value); }

    public int BassPicking { get => Choice(TechniqueField.BassPicking); set => Stage(TechniqueField.BassPicking, value); }

    public int PickDirection { get => Choice(TechniqueField.PickDirection); set => Stage(TechniqueField.PickDirection, value); }

    /// <summary>The finger picker's index: unset, thumb, 1-4.</summary>
    public int FingerIndex
    {
        get => Engine.Source is GuitarNote ? (IsMixed(TechniqueField.Finger) ? -1 : Read(TechniqueField.Finger) + 1) : 0;
        set => Stage(TechniqueField.Finger, value - 1);
    }

    public bool Accent { get => On(TechniqueField.Accent); set => Stage(TechniqueField.Accent, Bit(value)); }

    public bool Tremolo { get => On(TechniqueField.Tremolo); set => Stage(TechniqueField.Tremolo, Bit(value)); }

    public bool Vibrato { get => On(TechniqueField.Vibrato); set => Stage(TechniqueField.Vibrato, Bit(value)); }

    /// <summary>The unpitched slide (the "Slide" toggle); the pitched one is <see cref="SlideTo" />.</summary>
    public bool UnpitchedSlide { get => On(TechniqueField.UnpitchedSlide); set => Stage(TechniqueField.UnpitchedSlide, Bit(value)); }

    public bool LinkNext { get => On(TechniqueField.LinkNext); set => Stage(TechniqueField.LinkNext, Bit(value)); }

    public bool PassOtherNotes { get => On(TechniqueField.PassOtherNotes); set => Stage(TechniqueField.PassOtherNotes, Bit(value)); }

    public bool Ignore { get => On(TechniqueField.Ignore); set => Stage(TechniqueField.Ignore, Bit(value)); }

    /// <summary>The pitched slide's end fret on this note; on every selected note it keeps the interval.</summary>
    public int? SlideTo
    {
        get => Value(TechniqueField.SlideTo);
        set
        {
            if (Engine.Source is not GuitarNote || value == SlideTo) return;
            Engine.StageTechnique(new StagedKey(TechniqueField.SlideTo), value);
            Staged();
        }
    }

    /// <summary>Reset is staged: every technique of each target cleared, then the staged values.</summary>
    public bool IsReset => Engine.TechniqueReset;

    /// <summary>The page holds a draft for this note.</summary>
    public bool IsStaged => Engine.HasDraft(StagedPage.Technique);

    // One click on a segmented option stages its value (the strip's buttons bind these; the parameter is the choice).
    [RelayCommand]
    private void SetMute(string choice) => Pick(v => Mute = v, choice);

    [RelayCommand]
    private void SetHopo(string choice) => Pick(v => Hopo = v, choice);

    [RelayCommand]
    private void SetHarmonic(string choice) => Pick(v => Harmonic = v, choice);

    [RelayCommand]
    private void SetBassPicking(string choice) => Pick(v => BassPicking = v, choice);

    [RelayCommand]
    private void SetPickDirection(string choice) => Pick(v => PickDirection = v, choice);

    [RelayCommand]
    private void SetFinger(string choice) => Pick(v => FingerIndex = v, choice);

    [RelayCommand]
    private void Reset()
    {
        if (Engine.Source is not GuitarNote) return;
        Engine.ResetTechnique();
        Staged();
    }

    /// <summary>A multi-note selection opens on every selected note; one note on itself.</summary>
    internal void FollowSelection()
    {
        var count = Owner.Editor.Selection.SelectedNotes.Count;
        if (count == _selectedNotes) return;
        _selectedNotes = count;
        Engine.TechniqueOnSelectedNotes = count > 1;
    }

    private int? Value(TechniqueField field)
    {
        if (Engine.Source is not GuitarNote note) return null;
        var key = new StagedKey(field);
        return Engine.Technique.TryGetValue(key, out var staged) ? staged : StagedTechnique.Read(note, key, out _);
    }

    private int Read(TechniqueField field) => Value(field) ?? 0;

    /// <summary>A choice's value, or -1 (no option highlighted) when the selected notes disagree on it.</summary>
    private int Choice(TechniqueField field) => IsMixed(field) ? -1 : Read(field);

    private bool IsMixed(TechniqueField field)
    {
        var key = new StagedKey(field);
        if (!Engine.TechniqueOnSelectedNotes || Engine.Technique.ContainsKey(key)) return false;
        return Owner.Editor.Selection.SelectedNotes.Select(n => StagedTechnique.Read(n, key, out _)).Distinct().Skip(1).Any();
    }

    /// <summary>Stages the clicked option, then re-reads every choice so a click on the option already staged puts its
    /// highlight back (a toggle unchecks itself on that click).</summary>
    private void Pick(Action<int> set, string choice)
    {
        set(Parse(choice));
        OnPropertyChanged(nameof(Mute));
        OnPropertyChanged(nameof(Hopo));
        OnPropertyChanged(nameof(Harmonic));
        OnPropertyChanged(nameof(BassPicking));
        OnPropertyChanged(nameof(PickDirection));
        OnPropertyChanged(nameof(FingerIndex));
    }

    private static int Parse(string choice) => int.Parse(choice, System.Globalization.CultureInfo.InvariantCulture);

    private bool On(TechniqueField field) => Value(field) == 1;

    private static int Bit(bool on) => on ? 1 : 0;

    private void Stage(TechniqueField field, int value)
    {
        // A combo writes -1 while its items are replaced, and a re-read writes back what it shows: neither is an edit.
        if (Engine.Source is not GuitarNote || value < (field == TechniqueField.Finger ? -1 : 0) || value == Value(field)) return;
        Engine.StageTechnique(new StagedKey(field), value);
        Staged();
    }
}
