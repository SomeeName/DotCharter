namespace DotCharter.Services.Editing.Targets;

/// <inheritdoc />
public sealed class EditTargetService : IEditTargetService
{
    private readonly IConfigurationService _config;
    private readonly IEditingScope _scope;
    private readonly ISelectionService _selection;

    /// <summary>Installed by the composition root; null until then, which
    /// removes the pen rung rather than guessing a position.</summary>
    private Func<FractionalPosition?>? _penPosition;

    public EditTargetService(ISelectionService selection, IEditingScope scope, IConfigurationService config)
    {
        _selection = selection;
        _scope = scope;
        _config = config;
    }

    public void SetPenPositionProvider(Func<FractionalPosition?> provider) => _penPosition = provider;

    public EditTarget Resolve() => ResolveCore(_penPosition?.Invoke());

    public EditTarget ResolveAndPromote() => Promote(ResolveCore(_penPosition?.Invoke()));

    public EditTarget ResolveAndPromoteAt(FractionalPosition penPosition) => Promote(ResolveCore(penPosition));

    private EditTarget ResolveCore(FractionalPosition? penPosition)
    {
        // Read per call so the setting takes effect without a restart — the
        // same reason IChartPenService.IsEnabled is a property, not a field.
        var impliedRungsOn = _config.LoadAppConfig().UseFastChartingInput;

        return EditTargetResolver.Resolve(
            _selection.SelectedNotes,
            _selection.SelectedChords,
            impliedRungsOn ? _selection.SelectedHandShapes : [],
            _scope.Notes,
            _scope.Chords,
            impliedRungsOn ? penPosition : null);
    }

    private EditTarget Promote(EditTarget target)
    {
        if (!target.IsImplied || target.HasNoSounds) return target;

        if (target.Source == EditTargetSource.Pen)
            // The pen REPLACES the selection: nothing was selected (that is what
            // put us on this rung), so there is nothing to preserve, and the
            // edited sound becoming the selection is what makes the next verb
            // obviously act on the same thing.
            _selection.ClearSelections();
        // A HAND SHAPE rung deliberately does NOT clear: losing the bar you
        // clicked would be a surprise, and adding its sounds is idempotent —
        // the next Resolve() takes the SELECTION rung and returns this set.

        // AddRange replaces the `if (!Contains(x)) Add(x)` loop, minus
        // its two costs: the Contains scan against a selection that may already
        // hold thousands, and the one notification per appended item — each of
        // which fans into the inspector's full re-scan of the selection.
        SelectionBulkOps.AddRange(_selection.SelectedChords, target.Chords);
        SelectionBulkOps.AddRange(_selection.SelectedNotes, target.Notes);

        return target;
    }
}
