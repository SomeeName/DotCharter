using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DotCharter.UI.ViewModels.Editor.Inspector;

/// <summary>One string key of the chord Technique page: its number, whether it is the string being edited, whether
/// the chord plays it and whether the string-edit mask includes it.</summary>
public sealed partial class StagedStringChip(int stringIndex) : ObservableObject
{
    public int StringIndex { get; } = stringIndex;

    [ObservableProperty] private string _label = string.Empty;

    [ObservableProperty] private string _name = string.Empty;

    [ObservableProperty] private bool _isActive;

    [ObservableProperty] private bool _isPlayed;

    [ObservableProperty] private bool _isMasked;
}

/// <summary>
///     The Technique page of a chord (design D's string and "Whole chord" cards). One string at a time is
///     edited (its key lit); its techniques are STAGED per string (<see cref="StagedKey.StringIndex" />) and land on
///     the same string of every chord the "Apply to" ladder reaches. A string the chord does not play, or one the
///     string-edit mask leaves out, is not editable. "Copy to N" stages the edited string's techniques on every other
///     played string inside the mask — its vibrato depth with its vibrato, its Ignore too (bug c: Copy techniques
///     copies the per-string Ignore), and its slide as an interval onto the FRETTED strings only (an open string cannot
///     slide: <see cref="ChordSlideScope.Movers" />). The whole-chord flags, strum and Reset stage on the chord.
/// </summary>
public sealed partial class StagedChordTechniqueViewModel(InspectorStagedPages owner) : StagedPageViewModel(owner)
{
    private static readonly TechniqueField[] StringFields =
    [
        TechniqueField.Mute, TechniqueField.Harmonic, TechniqueField.BassPicking, TechniqueField.Hopo, TechniqueField.Vibrato,
        TechniqueField.UnpitchedSlide, TechniqueField.LinkNext, TechniqueField.Tremolo, TechniqueField.Ignore
    ];

    private int _active = -1;

    /// <summary>The string keys, highest string first (string 1 on the left), updated in place.</summary>
    public ObservableCollection<StagedStringChip> Strings { get; } = [];

    private Chord? Chord => Engine.Source as Chord;

    /// <summary>The string being edited (model index).</summary>
    public int ActiveString => _active;

    /// <summary>The chord plays the edited string and the mask includes it.</summary>
    public bool IsEditable => Editable(_active);

    public bool IsNotEditable => Chord is not null && !IsEditable;

    /// <summary>"D (4) / All strings": the edited string and the strings a copy and the mask reach.</summary>
    public string Header => Chord is null
        ? string.Empty
        : Loc.Format("Inspector_Staged_StringHeader", StringName(_active), Owner.ProjectState.StringEditMask.IsEmpty
            ? Loc["Inspector_Staged_AllStrings"]
            : GuitarStringLabel.List(Reach(), Owner.StringCount, Owner.TuningNames, Loc["Inspector_StringLabelFmt"]));

    /// <summary>Why the edited string takes no edit.</summary>
    public string NotEditableText => Loc.Format("Inspector_Staged_StringNotEditable", StringName(_active));

    /// <summary>The mask includes the edited string explicitly: the Mask key reads "Unmask".</summary>
    public string MaskLabel => Loc[Owner.ProjectState.StringEditMask.IsExplicit(_active) ? "Inspector_Staged_Unmask" : "Inspector_Staged_Mask"];

    public int Mute { get => Read(TechniqueField.Mute); set => Stage(TechniqueField.Mute, value); }

    public int Harmonic { get => Read(TechniqueField.Harmonic); set => Stage(TechniqueField.Harmonic, value); }

    public int BassPicking { get => Read(TechniqueField.BassPicking); set => Stage(TechniqueField.BassPicking, value); }

    public int Hopo { get => Read(TechniqueField.Hopo); set => Stage(TechniqueField.Hopo, value); }

    public bool Vibrato { get => On(TechniqueField.Vibrato); set => Stage(TechniqueField.Vibrato, Bit(value)); }

    public bool UnpitchedSlide { get => On(TechniqueField.UnpitchedSlide); set => Stage(TechniqueField.UnpitchedSlide, Bit(value)); }

    public bool LinkNext { get => On(TechniqueField.LinkNext); set => Stage(TechniqueField.LinkNext, Bit(value)); }

    public bool Tremolo { get => On(TechniqueField.Tremolo); set => Stage(TechniqueField.Tremolo, Bit(value)); }

    /// <summary>The edited string's own Ignore.</summary>
    public bool StringIgnore { get => On(TechniqueField.Ignore); set => Stage(TechniqueField.Ignore, Bit(value)); }

    /// <summary>The edited string's slide end; on another chord it keeps the interval (off the neck: skipped, named).</summary>
    public int? SlideTo
    {
        get => Value(new StagedKey(TechniqueField.SlideTo, _active));
        set
        {
            if (!IsEditable || value == SlideTo) return;
            Engine.StageTechnique(new StagedKey(TechniqueField.SlideTo, _active), value);
            Staged();
        }
    }

    public int Strum { get => Whole(TechniqueField.Strum) ?? 0; set => StageWhole(TechniqueField.Strum, value); }

    public bool Accent { get => Whole(TechniqueField.Accent) == 1; set => StageWhole(TechniqueField.Accent, Bit(value)); }

    /// <summary>The whole chord's Ignore.</summary>
    public bool ChordIgnore { get => Whole(TechniqueField.Ignore) == 1; set => StageWhole(TechniqueField.Ignore, Bit(value)); }

    public bool PassOtherNotes { get => Whole(TechniqueField.PassOtherNotes) == 1; set => StageWhole(TechniqueField.PassOtherNotes, Bit(value)); }

    /// <summary>Split notes. Under it No notes takes no edit, and its STORED flag stays as it was (the export
    /// reads Split first).</summary>
    public bool SplitIntoNotes { get => Whole(TechniqueField.SplitIntoNotes) == 1; set => StageWhole(TechniqueField.SplitIntoNotes, Bit(value)); }

    public bool ForceNoNotes { get => Whole(TechniqueField.ForceNoNotes) == 1; set => StageWhole(TechniqueField.ForceNoNotes, Bit(value)); }

    /// <summary>No notes takes no edit under Split notes.</summary>
    public bool CanForceNoNotes => !SplitIntoNotes;

    public bool IsReset => Engine.TechniqueReset;

    /// <summary>How many other strings "Copy to" reaches.</summary>
    public int CopyCount => IsEditable ? Reach().Count(s => s != _active) : 0;

    public string CopyLabel => Loc.Format("Inspector_Staged_CopyTo", CopyCount);

    public bool CanCopy => CopyCount > 0;

    internal override void Reread()
    {
        FollowChord();
        base.Reread();
    }

    [RelayCommand]
    private void SelectString(int stringIndex)
    {
        if (Chord is null) return;
        _active = stringIndex;
        Reread();
    }

    /// <summary>The Mask key: the edited string in or out of the string-edit mask.</summary>
    [RelayCommand]
    private void ToggleMask()
    {
        if (Chord is null || _active < 0) return;
        Owner.ProjectState.ToggleStringEditMask(_active);
    }

    /// <summary>Stages the edited string's techniques on every other played string inside the mask (bug c: its
    /// Ignore too), its vibrato depth where the vibrato is on, and its slide as the edited string's interval from
    /// each string's own fret, on the strings that can slide (<see cref="ChordSlideScope.Movers" />: the fretted ones,
    /// unless none is); clearing the slide clears it on every string.</summary>
    [RelayCommand]
    private void CopyToStrings()
    {
        if (Chord is not { } chord || !IsEditable) return;
        var from = chord.ChordNotes.First(n => n.StringIndex == _active);
        var values = StringFields.ToDictionary(f => f, f => Value(new StagedKey(f, _active)));
        if (values[TechniqueField.Vibrato] == 1) values[TechniqueField.VibratoStrength] = from.VibratoStrength;
        var others = chord.ChordNotes.Where(n => n.StringIndex != _active && Editable(n.StringIndex)).ToList();
        foreach (var strand in others)
        {
            foreach (var (field, value) in values) Engine.StageTechnique(new StagedKey(field, strand.StringIndex), value);
        }

        foreach (var (stringIndex, end) in CopiedSlides(from, Value(new StagedKey(TechniqueField.SlideTo, _active)), others))
            Engine.StageTechnique(new StagedKey(TechniqueField.SlideTo, stringIndex), end);
        Staged();
    }

    /// <summary>Where "Copy to strings" ends each string's slide: the edited string's interval from the string's own
    /// fret, on the movers among the edited string and <paramref name="strings" /> only (an open string keeps its own); null clears every one.</summary>
    internal static IEnumerable<(int StringIndex, int? End)> CopiedSlides(ChordNote from, int? slide, IReadOnlyList<ChordNote> strings)
    {
        if (slide is not { } to) return strings.Select(n => (n.StringIndex, (int?)null));
        var offset = to - from.Fret;
        return ChordSlideScope.Movers([from, .. strings]).Where(n => !ReferenceEquals(n, from))
            .Select(n => (n.StringIndex, (int?)(n.Fret + offset)));
    }

    /// <summary>A mixed selection: keep the chords alone selected, so this page edits them.</summary>
    [RelayCommand]
    private void EditSelectedChords() => Owner.Editor.Selection.SelectedNotes.Clear();

    [RelayCommand]
    private void Reset()
    {
        if (Chord is null) return;
        Engine.ResetTechnique();
        Staged();
    }

    /// <summary>The played strings inside the mask (an empty mask includes every string).</summary>
    private IEnumerable<int> Reach() => Chord is { } chord ? chord.ChordNotes.Select(n => n.StringIndex).Where(Editable).Order() : [];

    private bool Editable(int stringIndex) => Chord is { } chord && stringIndex >= 0 &&
                                              chord.ChordNotes.Any(n => n.StringIndex == stringIndex) &&
                                              Owner.ProjectState.StringEditMask.Includes(stringIndex);

    private string StringName(int stringIndex) =>
        GuitarStringLabel.Format(stringIndex, Owner.StringCount, Owner.TuningNames, Loc["Inspector_StringLabelFmt"]);

    /// <summary>Keeps the edited string on the chord (else its first editable string) and the keys in step.</summary>
    private void FollowChord()
    {
        if (Chord is not { } chord) return;
        if (!chord.ChordNotes.Any(n => n.StringIndex == _active))
        {
            var played = chord.ChordNotes.Select(n => n.StringIndex).Order().ToList();
            _active = played.FirstOrDefault(Editable, played.FirstOrDefault(-1));
        }

        var count = Owner.StringCount;
        while (Strings.Count > count) Strings.RemoveAt(Strings.Count - 1);
        while (Strings.Count < count) Strings.Add(new StagedStringChip(count - 1 - Strings.Count));
        for (var i = 0; i < count; i++)
        {
            var chip = Strings[i];
            if (chip.StringIndex != count - 1 - i) Strings[i] = chip = new StagedStringChip(count - 1 - i);
            chip.Label = GuitarStringLabel.Number(chip.StringIndex, count).ToString(System.Globalization.CultureInfo.CurrentCulture);
            chip.Name = Loc.Format("Inspector_Staged_EditString", StringName(chip.StringIndex));
            chip.IsActive = chip.StringIndex == _active;
            chip.IsPlayed = chord.ChordNotes.Any(n => n.StringIndex == chip.StringIndex);
            chip.IsMasked = Owner.ProjectState.StringEditMask.IsExplicit(chip.StringIndex);
        }
    }

    private int? Value(StagedKey key)
    {
        if (Chord is not { } chord) return null;
        return Engine.Technique.TryGetValue(key, out var staged) ? staged : StagedTechnique.Read(chord, key, out _);
    }

    private int Read(TechniqueField field) => Value(new StagedKey(field, _active)) ?? 0;

    private bool On(TechniqueField field) => Value(new StagedKey(field, _active)) == 1;

    private int? Whole(TechniqueField field) => Value(new StagedKey(field));

    private static int Bit(bool on) => on ? 1 : 0;

    private void Stage(TechniqueField field, int value)
    {
        // A combo writes -1 while its items are replaced, and a re-read writes back what it shows: neither is an edit.
        if (!IsEditable || value < 0 || value == Value(new StagedKey(field, _active))) return;
        Engine.StageTechnique(new StagedKey(field, _active), value);
        Staged();
    }

    private void StageWhole(TechniqueField field, int value)
    {
        if (Chord is null || value < 0 || value == Whole(field)) return;
        Engine.StageTechnique(new StagedKey(field), value);
        Staged();
    }
}
