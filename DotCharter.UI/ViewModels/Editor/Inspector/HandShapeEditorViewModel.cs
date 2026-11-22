using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Tone.UI.ViewModels.Shell;

namespace DotCharter.UI.ViewModels.Editor.Inspector;

/// <summary>
///     The Shape page's hand-shape span verbs: fit-to-sounds and the enclosed-count /
///     rebind-warning labels. The span is resized on the timeline; its typed start/end
///     boxes are gone. Exposed as
///     <c>InspectorViewModel.HandShapeEditor</c>.
///     <para>
///         The inspector owns the two ties back: it routes the selection's
///         <c>SelectedHandShapes.CollectionChanged</c> into
///         <see cref="SyncHandShapeProperties" />, and it handles
///         <see cref="Synced" /> — raised after EVERY sync, including the one a
///         fit-to-sounds runs — by re-reading the selection-derived state
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
        OnPropertyChanged(nameof(HandShapeEnclosedCount));
        OnPropertyChanged(nameof(HandShapeEnclosedLabel));
        OnPropertyChanged(nameof(HandShapeRebindsUntemplatedChords));
        OnPropertyChanged(nameof(HandShapeRebindsChords));
        OnPropertyChanged(nameof(HandShapeRebindWarning));
        Synced?.Invoke();
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
        var shape = _chartEditor.ActiveArrangement?.FindChordTemplate(hs.TemplateId);
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

        _historyService.Do(new SetHandShapeSpanAction(_chartEditor.HandShapes, hs, hs.Position, hs.EndPosition, start, end));
        SyncHandShapeProperties();
    }
}
