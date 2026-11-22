using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.UI.ViewModels.Panels;

namespace DotCharter.UI.ViewModels.Editor.Inspector;

/// <summary>
///     The Note tab's Bends page on the staged engine (design D): the curve card (the inspector's note or chord
///     <see cref="BendEditorViewModel" />, drawn by <c>Controls/ColumnFlowBendPlot</c>), the presets and the scope card. A
///     curve edit STAGES on the sound (<see cref="InspectorStagedEdits.StageBend" />); the footer's Apply writes it onto
///     every target the scope reaches (a note alone; a chord's "Apply to" rung), each remapped onto its own onset and
///     sustain (<see cref="StagedBends" />). The editors read "staged curve, else the sound's own", so the plot shows the
///     draft until Apply or Discard.
/// </summary>
public sealed partial class StagedBendsViewModel : StagedPageViewModel
{
    private static readonly int[] RangeChoices = [2, 4, 6];
    private BendEditorViewModel? _note;
    private BendEditorViewModel? _chord;
    private bool _staging;
    private int _range = 2;
    private IReadOnlyList<string>? _ranges;

    internal StagedBendsViewModel(InspectorStagedPages owner) : base(owner)
    {
    }

    /// <summary>The curve the page edits: the selected note's, or the selected chord's on the picked string.</summary>
    public BendEditorViewModel? Editor => Engine.Source switch
    {
        GuitarNote => _note,
        Chord => _chord,
        _ => null
    };

    public bool IsNote => Engine.Source is GuitarNote;

    public bool IsChord => Engine.Source is Chord;

    /// <summary>The semitones the plot shows: "+2 st", "+4 st" or "+6 st" (a curve above it is drawn on the top edge).</summary>
    public IReadOnlyList<string> Ranges => _ranges ??= [.. RangeChoices.Select(r => Loc.Format("Inspector_Bends_RangeFmt", r))];

    public int RangeIndex
    {
        get => Array.IndexOf(RangeChoices, _range);
        set
        {
            if (value < 0 || value >= RangeChoices.Length || RangeChoices[value] == _range) return;
            _range = RangeChoices[value];
            OnPropertyChanged();
            OnPropertyChanged(nameof(Range));
        }
    }

    public double Range => _range;

    /// <summary>Where the edited sound sits on the chart ("0:12.50").</summary>
    public string ChartTarget => Engine.Source is { } sound && Owner.Editor.Beats is { Count: > 0 } beats
        ? InspectorPlayheadClock.Format(SoundQueueViewModel.PositionOf(sound).GetPositionMs(beats))
        : "—";

    /// <summary>How many points the curve on the page has (the draft's, while one is staged).</summary>
    public int PointCount => Editor?.Points.Count ?? 0;

    /// <summary>The curve has a point: the readout then shows its values (mono), else the muted "click the plot" hint.</summary>
    public bool HasPoints => PointCount > 0;

    /// <summary>A note's bend reaches the note alone; a chord's what the "Apply to" rung shows.</summary>
    public string NoteScope => Loc["Inspector_Bends_OnlyThisNote"];

    /// <summary>"Add point": a point after the selected one (or at the start), on the curve's own grid.</summary>
    [RelayCommand]
    private void AddPoint() => Editor?.InsertPointAfterSelection();

    /// <summary>"Delete point": the selected point (disabled while none is).</summary>
    [RelayCommand(CanExecute = nameof(CanDeletePoint))]
    private void DeletePoint() => Editor?.DeleteSelected();

    private bool CanDeletePoint() => Editor?.SelectedPoint is not null;

    /// <summary>Builds the curve editor for <paramref name="sound" /> (on <paramref name="stringIndex" /> for a chord):
    /// it reads the staged curve, else the sound's own, and STAGES each edit.</summary>
    internal BendEditorViewModel BuildEditor(ITimedElement sound, int? stringIndex, Func<FractionalPosition> start,
        Func<FractionalPosition> end)
    {
        var editor = new BendEditorViewModel(() => Curve(sound, stringIndex), values => Stage(sound, stringIndex, values),
            start, end, () => { }, Loc);
        if (sound is Chord) Swap(ref _chord, editor);
        else Swap(ref _note, editor);
        Reread();
        return editor;
    }

    private void Swap(ref BendEditorViewModel? slot, BendEditorViewModel editor)
    {
        slot?.PropertyChanged -= OnEditorChanged;
        slot = editor;
        editor.PropertyChanged += OnEditorChanged;
    }

    private void OnEditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(BendEditorViewModel.SelectedPoint) or nameof(BendEditorViewModel.Points))
            DeletePointCommand.NotifyCanExecuteChanged();
    }

    /// <summary>A loaded curve that peaks above the plot's range raises the range to the smallest that holds it (a curve
    /// is never drawn flat on the top edge); a lower curve keeps the range the user chose.</summary>
    private void FitRange()
    {
        var peak = Editor?.Points.Select(p => p.Step).DefaultIfEmpty(0).Max() ?? 0;
        if (peak <= _range) return;
        var fit = RangeChoices.FirstOrDefault(r => r >= peak - 1e-9, RangeChoices[^1]);
        RangeIndex = Array.IndexOf(RangeChoices, fit);
    }

    /// <summary>The curve the editor shows: the draft on this sound and string, else the sound's own.</summary>
    private IReadOnlyList<BendValue> Curve(ITimedElement sound, int? stringIndex) =>
        ReferenceEquals(Engine.Source, sound) && Engine.BendOn(stringIndex) is { } draft
            ? [.. draft.Values]
            : [.. StagedBends.Curve(sound, stringIndex) ?? []];

    private void Stage(ITimedElement sound, int? stringIndex, IReadOnlyList<BendValue> values)
    {
        if (!ReferenceEquals(Engine.Source, sound)) return;
        Engine.StageBend(stringIndex, values);
        _staging = true;
        try
        {
            Staged();
        }
        finally
        {
            _staging = false;
        }
    }

    /// <summary>Re-reads the page; an Apply, Discard, undo or selection change also reloads the curve (a stage of its own
    /// does not, so the point being dragged stays selected).</summary>
    internal override void Reread()
    {
        if (!_staging)
        {
            if (Engine.Source is GuitarNote) _note?.ReloadFromDomain();
            else if (Engine.Source is Chord) _chord?.ReloadFromDomain();
            FitRange();
        }

        base.Reread();
        DeletePointCommand.NotifyCanExecuteChanged();
    }
}
