using CommunityToolkit.Mvvm.Input;
using DotCharter.UI.Controls;

namespace DotCharter.UI.ViewModels.Editor.Inspector;

/// <summary>
///     The Placement page of one note (design D's "Place a note" and "Position" cards): string, fret,
///     sustain and slide target, and the board click, all STAGED (<see cref="InspectorStagedEdits.StagePlacement" />)
///     and written by the footer's Apply. Each field shows the staged value, else the note's own; the sounding
///     pitch and the board follow the staged string and fret.
/// </summary>
public sealed partial class StagedPlacementViewModel(InspectorStagedPages owner) : StagedPageViewModel(owner)
{
    private IReadOnlyList<string>? _stringLabels;

    private Chord? _preview;
    private (int String, int Fret)? _previewAt;

    private GuitarNote? Note => Engine.Source as GuitarNote;

    private PlacementDraft? Draft => Engine.Placement;

    /// <summary>Any field of the note is staged and not yet written.</summary>
    public bool IsStaged => Draft is not null;

    /// <summary>The string picker's index: the staged string, else the note's.</summary>
    public int StringIndex
    {
        get => Draft?.StringIndex ?? Note?.StringIndex ?? 0;
        set
        {
            // The picker writes -1 while its items are replaced: never a string.
            if (value < 0 || Note is null || value == StringIndex) return;
            Stage(d => d with { StringIndex = value });
        }
    }

    public int Fret
    {
        get => Draft?.Fret ?? Note?.Fret ?? 0;
        set
        {
            if (Note is null || value == Fret) return;
            Stage(d => d with { Fret = Math.Clamp(value, 0, FretLimits.MaxFret) });
        }
    }

    /// <summary>The grid-relative length ("1/8", "2"); commits on Enter or focus loss. Garbage is refused.</summary>
    public string SustainText
    {
        get => Note is null ? string.Empty : FractionalLengthFormat.Format(Sustain.DoubleValue) ?? "0";
        set
        {
            if (Note is null || value == SustainText) return;
            if (!FractionalLengthFormat.TryParseBeats(value, out var numerator, out var denominator))
            {
                Reread();
                return;
            }

            var sustain = new FractionalPosition(new Fraction(numerator, denominator));
            Stage(d => d with { Sustain = sustain });
        }
    }

    /// <summary>The slide's end fret (a staged fret move carries it); empty is no slide.</summary>
    public int? SlideTo
    {
        get => Note is { } note && Draft is { } draft ? StagedPlacement.SlideEnd(note, draft) : Note?.SlideTo;
        set
        {
            if (Note is null || value == SlideTo) return;
            Stage(d => d with { SlideSet = true, SlideTo = value });
        }
    }

    /// <summary>What the staged string and fret sound (empty when there is nothing honest to name).</summary>
    public string PitchName => Note is null ? string.Empty : SoundingPitchReadout.For(Owner.Arrangement, StringIndex, Fret);

    public bool HasPitch => PitchName.Length > 0;

    /// <summary>The length in words ("1 beat"), mono beside the pitch.</summary>
    public string SustainLabel => Note is null
        ? string.Empty
        : FractionalLengthFormat.Format(Sustain.DoubleValue) is { } text
            ? Loc.PluralWith("Inspector_SustainBeatsOne", "Inspector_SustainBeats", Sustain.DoubleValue == 1.0 ? 1 : 2, text)
            : Loc["Inspector_SustainNone"];

    /// <summary>The string picker's items, named like the lane gutter, in model order. The SAME list while the tuning holds: a new list on every re-read resets
    /// the picker, whose reset writes its first item back as an edit.</summary>
    public IReadOnlyList<string> StringLabels
    {
        get
        {
            var labels = GuitarStringLabel.All(Owner.StringCount, Owner.TuningNames, Loc["Inspector_StringLabelFmt"]);
            if (_stringLabels is null || !_stringLabels.SequenceEqual(labels)) _stringLabels = labels;
            return _stringLabels;
        }
    }

    /// <summary>The board's one dot at the staged string and fret (a fresh chord only when it moves: the board
    /// repaints on a new reference).</summary>
    public Chord? PreviewChord
    {
        get
        {
            if (Note is not { } note) return null;
            var at = (StringIndex, Fret);
            if (_preview is not null && _previewAt == at) return _preview;
            _previewAt = at;
            _preview = new Chord { Position = note.Position };
            _preview.ChordNotes.Add(new ChordNote { StringIndex = at.StringIndex, Fret = at.Fret, Mute = note.Mute });
            return _preview;
        }
    }

    private FractionalPosition Sustain => Draft?.Sustain ?? Note?.Sustain ?? default;

    /// <summary>A click on the board stages the clicked string and fret in one go.</summary>
    [RelayCommand]
    private void SetTarget((int stringIdx, int fret) pos)
    {
        if (Note is null) return;
        var stringIndex = Math.Clamp(pos.stringIdx, 0, Math.Max(0, Owner.StringCount - 1));
        var fret = Math.Clamp(pos.fret, 0, FretLimits.MaxFret);
        Stage(d => d with { StringIndex = stringIndex, Fret = fret });
    }

    /// <summary>The sustain spinner: one grid step longer or shorter (never below zero, and a step up never past the
    /// end of the song's audio, as the keyboard's sustain step), staged like a typed length.</summary>
    public void StepSustain(int steps)
    {
        if (Note is not { } note) return;
        var step = new FractionalPosition(new Fraction(1, Math.Max(1, Owner.GridDivisor()))) * steps;
        var sustain = FractionalPosition.Max(FractionalPosition.Zero, Sustain + step);
        if (steps > 0 && AudioEndSustain(note) is { } ceiling && sustain > ceiling) sustain = FractionalPosition.Max(Sustain, ceiling);
        Stage(d => d with { Sustain = sustain });
    }

    /// <summary>The longest sustain that still ends on the song's audio; null with no audio length.</summary>
    private FractionalPosition? AudioEndSustain(GuitarNote note)
    {
        var audioMs = Owner.ProjectState.TotalAudioLengthMs;
        var beats = Owner.Editor.Beats;
        if (audioMs <= 0 || beats.Count == 0) return null;
        return FractionalPosition.Max(FractionalPosition.Zero, TimelineMath.PositionFromMs(audioMs, beats) - note.Position);
    }

    private void Stage(Func<PlacementDraft, PlacementDraft> change)
    {
        Engine.StagePlacement(change(Draft ?? new PlacementDraft()));
        Staged();
    }
}
