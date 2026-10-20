using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Services.Editing.ChordShapes;
using DotCharter.Tone.UI.ViewModels.Shell;
using DotCharter.UI.Controls;

namespace DotCharter.UI.ViewModels.Editor.Inspector;

/// <summary>
///     The Sound tab's hand-shape cards: the Span card's typed start/end (end
///     EXCLUSIVE), fit-to-sounds, and the enclosed-count / rebind-warning labels
///     the Span and Shape cards print. Exposed as
///     <c>InspectorViewModel.HandShapeEditor</c>.
///     <para>
///         The inspector owns the two ties back: it routes the selection's
///         <c>SelectedHandShapes.CollectionChanged</c> into
///         <see cref="SyncHandShapeProperties" />, and it handles
///         <see cref="Synced" /> — raised after EVERY sync, including the one a
///         committed span edit runs — by re-reading the selection-derived state
///         it owns (the selection-issue memo and the card-visibility matrix).
///     </para>
/// </summary>
public sealed partial class HandShapeEditorViewModel : ViewModelBase
{
    private readonly ChartEditorViewModel _chartEditor;
    private readonly IHistoryService _historyService;
    private readonly TransportViewModel _transport;
    private readonly ILocalizationService _localization;

    /// <summary>The first selected hand shape — what the Shape and Span cards
    /// author in states 3 and 6. Kept as a property so the view binds it
    /// without reaching into the selection service.</summary>
    [ObservableProperty] private HandShape? _selectedHandShape;

    /// <summary>
    ///     The two whole-CHART counts the Span and Shape cards print, folded
    ///     ONCE per sync instead of once per bound reader.
    ///     <para>
    ///         Computed in their getters, both would be read by three bound
    ///         properties: <see cref="HandShapeEnclosedLabel" /> pulls
    ///         <see cref="HandShapeEnclosedCount" /> (a full <c>PlacedNotes</c> +
    ///         <c>PlacedChords</c> walk), and <see cref="HandShapeRebindsChords" />
    ///         and <see cref="HandShapeRebindWarning" /> each pull
    ///         <see cref="HandShapeRebindsUntemplatedChords" /> (a full
    ///         <c>PlacedChords</c> walk with a <c>DescribesChordNotes</c> per
    ///         chord). Every <c>OnPropertyChanged</c> re-evaluates every bound
    ///         getter, so ONE hand-shape selection change would run three whole-level
    ///         walks — on a 20,000-sound level, between the click and the frame.
    ///     </para>
    ///     <para>
    ///         Folding here is behaviour-identical because
    ///         <see cref="SyncHandShapeProperties" /> is the ONLY thing that
    ///         raises any of the five, so a value cached at the raise is exactly
    ///         as fresh as one computed at the read.
    ///     </para>
    /// </summary>
    private int _handShapeEnclosedCount;

    private int _handShapeRebindsUntemplatedChords;

    /// <summary>Set while <see cref="SyncHandShapeProperties" /> writes the two
    /// span boxes, so the write is not mistaken for a typed commit.</summary>
    private bool _isSyncing;

    // ---- the hand shape's span (§4.3) --------------------------------------
    //
    // Closes "no edge drag, no numeric length; the wheel moves only the end; the
    // start cannot move independently" WITHOUT adding a single timeline gesture.

    [ObservableProperty] private string _handShapeStartText = string.Empty;
    [ObservableProperty] private string _handShapeEndText = string.Empty;

    public HandShapeEditorViewModel(
        ChartEditorViewModel chartEditor,
        IHistoryService historyService,
        TransportViewModel transport,
        ILocalizationService localization)
    {
        _chartEditor = chartEditor;
        _historyService = historyService;
        _transport = transport;
        _localization = localization;
    }

    /// <summary>Raised at the end of every <see cref="SyncHandShapeProperties" />.
    /// The inspector subscribes it to its own selection-derived notifications.</summary>
    public event Action? Synced;

    internal void SyncHandShapeProperties()
    {
        SelectedHandShape = _chartEditor.Selection.SelectedHandShapes.FirstOrDefault();
        _handShapeEnclosedCount = ComputeHandShapeEnclosedCount();
        _handShapeRebindsUntemplatedChords = ComputeHandShapeRebindsUntemplatedChords();
        var wasSyncing = _isSyncing;
        _isSyncing = true;
        HandShapeStartText = SelectedHandShape is { } start
            ? FractionalLengthFormat.Format(start.Position.DoubleValue) ?? "0"
            : string.Empty;
        HandShapeEndText = SelectedHandShape is { } end
            ? FractionalLengthFormat.Format(end.EndPosition.DoubleValue) ?? "0"
            : string.Empty;
        _isSyncing = wasSyncing;
        OnPropertyChanged(nameof(HandShapeSpanLabel));
        OnPropertyChanged(nameof(HandShapeEnclosedCount));
        OnPropertyChanged(nameof(HandShapeEnclosedLabel));
        OnPropertyChanged(nameof(HandShapeRebindsUntemplatedChords));
        OnPropertyChanged(nameof(HandShapeRebindsChords));
        OnPropertyChanged(nameof(HandShapeRebindWarning));
        SyncGrip();
        Synced?.Invoke();
    }

    /// <summary>"bars 42.1 – 43.3 (exclusive) · 6 beats". EndPosition is
    /// EXCLUSIVE (Core invariant E.4) and the label says so, because the Span
    /// card lets the user type into it.</summary>
    public string HandShapeSpanLabel
    {
        get
        {
            if (SelectedHandShape is not { } hs)
            {
                return string.Empty;
            }

            var beats = (hs.EndPosition - hs.Position).DoubleValue;
            return _localization.Format("Inspector_Span_Label",
                hs.Position.BeatId + 1, hs.EndPosition.BeatId + 1, beats.ToString("0.##"));
        }
    }

    /// <summary>How many sounds the selected shape spans (end EXCLUSIVE, the
    /// same reading <c>HandShape.Covers</c> uses).</summary>
    public int HandShapeEnclosedCount => _handShapeEnclosedCount;

    private int ComputeHandShapeEnclosedCount()
    {
        if (SelectedHandShape is not { } hs)
        {
            return 0;
        }

        bool InRange(FractionalPosition p) => p >= hs.Position && p < hs.EndPosition;
        return _chartEditor.PlacedNotes.Count(n => InRange(n.Position))
               + _chartEditor.PlacedChords.Count(c => InRange(c.Position));
    }

    /// <summary>
    ///     Chords inside the selected shape that are NOT bound to its template
    ///     and would be REBOUND and reshaped by a shape edit
    ///     (<c>ChordApplyScope.EnclosedUnbound</c>). Stated BEFORE the edit on the
    ///     Shape card rather than discovered after it.
    /// </summary>
    public int HandShapeRebindsUntemplatedChords => _handShapeRebindsUntemplatedChords;

    private int ComputeHandShapeRebindsUntemplatedChords()
    {
        if (SelectedHandShape is not { } hs)
        {
            return 0;
        }

        // `ChordApplyScope.EnclosedUnbound` rebinds an enclosed chord only if
        // it already SOUNDS the shape's grip — a chord of a different shape
        // under the same bar is deliberately left alone. Omitting that test
        // counted every enclosed chord, so a bar over a riff of mixed shapes
        // warned about rebinding sounds no edit would ever touch.
        var shape = _chartEditor.ActiveArrangement?.ChordTemplates
            .FirstOrDefault(t => t.Id == hs.TemplateId);
        return CountRebindableChords(_chartEditor.PlacedChords, hs, shape);
    }

    /// <summary>
    ///     The rule behind <see cref="HandShapeRebindsUntemplatedChords" />, pure
    ///     so it can be tested without the inspector's DI graph. It must stay
    ///     the same three tests <c>ChordApplyScope.EnclosedUnbound</c> applies:
    ///     enclosed (end EXCLUSIVE), not already bound to the bar's template,
    ///     and already SOUNDING the bar's grip.
    /// </summary>
    internal static int CountRebindableChords(IEnumerable<Chord> placedChords, HandShape hs, ChordTemplate? shape)
    {
        // A dangling TemplateId declares no grip, so nothing can match it and a
        // shape edit has nothing to project onto the enclosed chords.
        if (shape == null)
        {
            return 0;
        }

        return placedChords.Count(c =>
            c.TemplateId != hs.TemplateId &&
            c.Position >= hs.Position && c.Position < hs.EndPosition &&
            shape.DescribesChordNotes(c.ChordNotes));
    }

    /// <summary>Whether to show the rebind warning at all — the count is an
    /// <c>int</c> and Avalonia will not convert one to <c>IsVisible</c>.</summary>
    public bool HandShapeRebindsChords => HandShapeRebindsUntemplatedChords > 0;

    /// <summary>
    ///     Pre-formatted, and it must stay that way. Avalonia's
    ///     <c>StringFormat</c> takes a <see cref="string" />, but
    ///     <c>{localization:Loc}</c> is a markup extension that returns a BINDING
    ///     (so the text follows a live language switch) — handing one to
    ///     <c>StringFormat</c> throws <see cref="InvalidCastException" /> while
    ///     the view is being constructed. `ViewLocator` swallows that into a
    ///     "Not Found: … (TargetInvocationException)" placeholder, so the symptom
    ///     is the WHOLE inspector pane disappearing, with nothing naming the
    ///     line. Format here, exactly as the selection summary and the issue
    ///     summary already do.
    /// </summary>
    public string HandShapeRebindWarning =>
        _localization.Plural("Inspector_Shape_RebindsWarningOne",
            "Inspector_Shape_RebindsWarning", HandShapeRebindsUntemplatedChords);

    /// <inheritdoc cref="HandShapeRebindWarning" />
    public string HandShapeEnclosedLabel =>
        _localization.Plural("Inspector_Span_EnclosesOne", "Inspector_Span_Encloses",
            HandShapeEnclosedCount);

    // Both boxes bind with UpdateSourceTrigger=LostFocus, so these fire once per
    // commit, not per keystroke.
    partial void OnHandShapeStartTextChanged(string value)
    {
        _ = value;
        CommitHandShapeSpan(startEdited: true);
    }

    partial void OnHandShapeEndTextChanged(string value)
    {
        _ = value;
        CommitHandShapeSpan(startEdited: false);
    }

    /// <summary>Commit the typed span. EndPosition is EXCLUSIVE (Core invariant
    /// E.4) and an end at or before the start is refused rather than clamped —
    /// a zero-width shape is not a shorter shape, it is a broken one.</summary>
    private void CommitHandShapeSpan(bool startEdited)
    {
        if (_isSyncing || SelectedHandShape is not { } hs)
        {
            return;
        }

        if (ResolveHandShapeSpan(hs.Position, hs.EndPosition,
                startEdited ? HandShapeStartText : HandShapeEndText, startEdited) is not { } span)
        {
            SyncHandShapeProperties();
            return;
        }

        var (start, end) = span;

        _historyService.Do(new SetHandShapeSpanAction(hs, hs.Position, hs.EndPosition, start, end));
        SyncHandShapeProperties();
    }

    /// <summary>
    ///     The span one committed box produces: the edited endpoint parsed, the
    ///     OTHER one taken from the model. Null when the text is garbage or the
    ///     result would not be a forward span.
    ///     <para>
    ///         Reading the untouched box back was a silent MOVE. Both boxes are
    ///         printed through <see cref="FractionalLengthFormat.Format" />,
    ///         which spells a value of one beat or more with <c>"0.##"</c> — so a
    ///         shape sitting on a triplet at beat 137⅓ prints "137.33", and
    ///         committing an edit to the END re-parsed that and wrote the START
    ///         back 1/300 of a beat off. The endpoint the user did not type is
    ///         never re-derived from its own rounded text.
    ///     </para>
    ///     <para>Pure, so it is testable without any services.</para>
    /// </summary>
    internal static (FractionalPosition Start, FractionalPosition End)? ResolveHandShapeSpan(
        FractionalPosition currentStart, FractionalPosition currentEnd, string editedText, bool startEdited)
    {
        if (!FractionalLengthFormat.TryParseBeats(editedText, out var numerator, out var denominator))
        {
            return null;
        }

        var edited = new FractionalPosition(new Fraction(numerator, denominator));
        var start = startEdited ? edited : currentStart;
        var end = startEdited ? currentEnd : edited;
        return end <= start ? null : (start, end);
    }

    /// <summary>Snap the shape to the sounds it encloses: start at the first,
    /// end just past the last (EXCLUSIVE, so the last sound is inside).</summary>
    [RelayCommand]
    private void FitHandShapeToSounds()
    {
        if (SelectedHandShape is not { } hs)
        {
            return;
        }

        bool InRange(FractionalPosition p) => p >= hs.Position && p < hs.EndPosition;

        var positions = _chartEditor.PlacedNotes.Where(n => InRange(n.Position)).Select(n => n.Position)
            .Concat(_chartEditor.PlacedChords.Where(c => InRange(c.Position)).Select(c => c.Position))
            .ToList();
        if (positions.Count == 0)
        {
            return;
        }

        var start = positions.Min();
        // EXCLUSIVE end: one grid division past the last sound, so it is inside.
        var end = positions.Max() + new Fraction(1, Math.Max(1, _transport.GridDivisor));
        if (end <= start)
        {
            return;
        }

        _historyService.Do(new SetHandShapeSpanAction(hs, hs.Position, hs.EndPosition, start, end));
        SyncHandShapeProperties();
    }

    // ---- the grip's ghost gems and pin -------------------------------

    /// <summary>One row per string the selected shape's grip holds: its fret and a "ghost"
    /// box — held, never played (<see cref="ChordTemplate.GhostMask" />). Empty without a
    /// templated hand shape.</summary>
    public ObservableCollection<HandShapeGhostRow> GhostRows { get; } = [];

    public bool HasGhostRows => GhostRows.Count > 0;

    /// <summary>The grip is AUTHORED: no automatic flow replaces or re-fingers it
    /// (<see cref="ChordTemplate.IsPinned" />). A grip holding ghost gems is pinned whatever
    /// this says, so the box is checked and disabled then.</summary>
    [ObservableProperty] private bool _isGripPinned;

    [ObservableProperty] private bool _canUnpinGrip;

    private ChordTemplate? GripOf(HandShape? shape) =>
        shape == null || _chartEditor.ActiveArrangement is not { } arrangement
            ? null
            : arrangement.ChordTemplates.FirstOrDefault(t => t.Id == shape.TemplateId);

    private void SyncGrip()
    {
        var grip = GripOf(SelectedHandShape);
        var wasSyncing = _isSyncing;
        _isSyncing = true;
        GhostRows.Clear();
        if (grip != null)
        {
            var tuning = _chartEditor.ActiveArrangement?.Tuning;
            var count = tuning is { Length: > 0 } ? tuning.Length : _chartEditor.ActiveArrangement?.StringCount ?? 6;
            var format = _localization["Inspector_StringLabelFmt"];
            foreach (var (stringIndex, fret) in grip.Frets.OrderByDescending(f => f.Key))
            {
                var label = _localization.Format("Inspector_GhostGem_Row",
                    GuitarStringLabel.Format(stringIndex, count, tuning, format), fret);
                GhostRows.Add(new HandShapeGhostRow(stringIndex, fret, label, grip.IsGhost(stringIndex), OnGhostRowToggled));
            }
        }

        IsGripPinned = grip?.IsPinned ?? false;
        CanUnpinGrip = grip is { GhostMask: 0 };
        _isSyncing = wasSyncing;
        OnPropertyChanged(nameof(HasGhostRows));
    }

    private void OnGhostRowToggled(HandShapeGhostRow row, bool ghost)
    {
        if (_isSyncing || SelectedHandShape is not { } shape) return;
        if (_chartEditor.ActiveArrangement is not { } arrangement || _chartEditor.ActiveLevel is not { } level) return;
        var plan = GhostGems.SetGhost(arrangement, level, shape, row.StringIndex, ghost);
        if (plan.Action is { } action) _historyService.Do(action);
        _chartEditor.RequestRedraw();
        SyncHandShapeProperties();
    }

    partial void OnIsGripPinnedChanged(bool value)
    {
        if (_isSyncing || GripOf(SelectedHandShape) is not { } grip || grip.Pinned == value) return;
        _historyService.Do(new SetTemplatePinnedAction(grip, value));
        SyncHandShapeProperties();
    }
}

/// <summary>One string of a hand shape's grip in the inspector's ghost-gem list.</summary>
public sealed class HandShapeGhostRow(
    int stringIndex, int fret, string label, bool isGhost, Action<HandShapeGhostRow, bool> toggled) : ObservableObject
{
    private bool _isGhost = isGhost;

    public int StringIndex { get; } = stringIndex;

    public int Fret { get; } = fret;

    public string Label { get; } = label;

    public bool IsGhost
    {
        get => _isGhost;
        set
        {
            if (!SetProperty(ref _isGhost, value)) return;
            toggled(this, value);
        }
    }

    public override string ToString() => Label;
}
