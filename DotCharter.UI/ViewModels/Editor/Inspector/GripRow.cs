using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DotCharter.UI.ViewModels.Editor.Inspector;

/// <summary>
///     One string of the Shape page's grid: the chord's fret on it (Note), the grip's fret (Grip), its ghost
///     toggle and its finger. Values read across every target; a disagreement shows as mixed.
/// </summary>
public sealed class GripRow : ObservableObject
{
    private bool _isKeyRow;

    private readonly GripInspectorViewModel _owner;
    private readonly string _mixed;
    private int?[] _chordFrets = [];
    private int?[] _gripFrets = [];
    private int[] _fingers = [];
    private bool? _ghost;
    private bool _canFinger;

    internal GripRow(GripInspectorViewModel owner, int index, string label, string mixed, string fingerMixed)
    {
        _owner = owner;
        StringIndex = index;
        Label = label;
        _mixed = mixed;
        FretboardFingerChoices = Enumerable.Range(0, 5).Select(f => new GripFingerChoice(this, f, fingerMixed)).ToArray();
    }

    public int StringIndex { get; }

    /// <summary>The row the keyboard edits (Up/Down move it, Left/Right step its finger).</summary>
    public bool IsKeyRow
    {
        get => _isKeyRow;
        internal set => SetProperty(ref _isKeyRow, value);
    }
    public string Label { get; }
    public string Pitch { get; private set; } = "—";
    public IReadOnlyList<GripFingerChoice> FretboardFingerChoices { get; }
    public bool IsIncludedInMask => _owner.Mask.Includes(StringIndex);
    public bool IsMaskExplicit => _owner.Mask.IsExplicit(StringIndex);
    public bool CanEditGrip => _owner.CanEditGrip && IsIncludedInMask;
    public bool CanEditFinger => CanEditGrip && _canFinger;
    public bool CanEditGhost => CanEditGrip && HasGrip;
    public bool HasChord => _chordFrets.Any(f => f.HasValue);
    public bool HasGrip => _gripFrets.Any(f => f.HasValue);
    public bool HasMixedChordFret => _chordFrets.Length > 1;
    public bool HasMixedGripFret => _gripFrets.Length > 1;
    public bool HasMixedFinger => _fingers.Length > 1;
    public int? Finger => _fingers.Length == 1 ? _fingers[0] : null;

    /// <summary>The fretboard dot's caption on a mixed string, "1/2" (a third value adds "+"); null when not mixed.</summary>
    public string? MixedFingerCaption => HasMixedFinger
        ? string.Join("/", _fingers.Order().Take(2).Select(FingerText)) + (_fingers.Length > 2 ? "+" : "")
        : null;
    public string ChordPlaceholder => HasMixedChordFret ? _mixed : _owner.MutedStrings.Contains(StringIndex) ? "×" : "—";
    public string GripPlaceholder => HasMixedGripFret ? _mixed : "—";

    public string ChordFretText
    {
        get => FretText(_chordFrets);
        set
        {
            if (HasChord && IsIncludedInMask && value != ChordFretText && TryFret(value, out var fret))
            {
                _owner.SetChordFret(StringIndex, fret);
            }
        }
    }

    public string GripFretText
    {
        get => FretText(_gripFrets);
        set
        {
            if (!CanEditGrip || value == GripFretText) return;
            if (string.IsNullOrWhiteSpace(value)) _owner.SetGripFret(StringIndex, null);
            else if (TryFret(value, out var fret)) _owner.SetGripFret(StringIndex, fret);
        }
    }

    public bool? IsGhost
    {
        get => _ghost;
        set
        {
            if (CanEditGhost && value is { } next && value != _ghost) _owner.SetGhost(StringIndex, next);
        }
    }

    // chordFingers: the fingers the chords' own templates state here. They join the grips' only on a string a
    // grip covers, so a chord whose template disagrees with its shape shows as mixed.
    internal void Update(int?[] chord, ChordTemplate[] grips, Arrangement? arrangement, IReadOnlyCollection<int> chordFingers)
    {
        _chordFrets = chord;
        _gripFrets = grips.Select(g => g.Frets.TryGetValue(StringIndex, out var fret) ? (int?)fret : null).Distinct().ToArray();
        var gripFingers = grips.Where(g => g.Frets.ContainsKey(StringIndex)).Select(g => g.GetFinger(StringIndex)).ToArray();
        _fingers = gripFingers.Length == 0 ? [] : gripFingers.Concat(chordFingers).Distinct().ToArray();
        var ghosts = grips.Where(g => g.Frets.ContainsKey(StringIndex)).Select(g => g.IsGhost(StringIndex)).Distinct().ToArray();
        _ghost = ghosts.Length > 1 ? null : ghosts.FirstOrDefault();
        _canFinger = grips.Any(g => !g.Fingerless && g.Frets.ContainsKey(StringIndex));
        Pitch = chord is [int fret] ? SoundingPitch.NameFor(arrangement, StringIndex, fret) ?? "—" : "—";
        OnPropertyChanged(string.Empty);
        foreach (var choice in FretboardFingerChoices) choice.Refresh();
    }

    internal bool IncludesFinger(int finger) => _fingers.Contains(finger);

    public void ChooseFinger(int finger) => _owner.SetFinger(StringIndex, finger);

    private static string FingerText(int finger) => finger switch
    {
        < 0 => "–",
        0 => "T",
        _ => finger.ToString(CultureInfo.InvariantCulture)
    };

    private static string FretText(int?[] values) =>
        values is [int value] ? value.ToString(CultureInfo.InvariantCulture) : "";

    private static bool TryFret(string? text, out int fret) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out fret) && fret <= FretLimits.MaxFret;
}

/// <summary>One finger cell (T, 1–4) of a grip row; a second press on the lit finger clears it.</summary>
public sealed partial class GripFingerChoice(GripRow row, int value, string mixedHint) : ObservableObject
{
    public int Value { get; } = value;
    public string Display => Value == 0 ? "T" : Value.ToString(CultureInfo.InvariantCulture);
    public bool IsSelected => Value == row.Finger;
    public bool IsFretboardMixed => row.HasMixedFinger && row.IncludesFinger(Value);
    public string? FretboardMixedHint => IsFretboardMixed ? mixedHint : null;

    internal void Refresh() => OnPropertyChanged(string.Empty);

    [RelayCommand] private void Toggle() => row.ChooseFinger(IsSelected ? -1 : Value);
}
