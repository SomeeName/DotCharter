using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Core.Domain.Guitar;
using DotCharter.Core.Utilities;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Editor;
using DotCharter.Services.Editing;
using DotCharter.Services.State;

namespace DotCharter.Presentation.Screens.Editor;

/// <summary>
///     The Uses page of the Shapes panel (inspector design D): every chord and hand shape of the current shape on the
///     level on screen, in time order. A use is TICKED while the editor's selection holds it, so ticking is selecting
///     and Split uses is the shape's own Split off selection; a row's press selects that use alone and seeks to it.
///     A chord and the hand shape covering it are one grip on screen, so they tick together: a split never leaves a
///     chord on one shape and its hand shape on the other.
///     <para>
///         The rows are kept while the same elements use the shape (a refresh only re-reads the ticks), so a history
///         step that touched nothing here rebuilds nothing.
///     </para>
/// </summary>
internal sealed class ShapeUsesList(IChartEditorSurface editor, ProjectState state, ILocalizationService localization)
{
    private ChordTemplate? _template;
    private bool _stale;

    public IReadOnlyList<ShapeUseRowViewModel> Rows { get; private set; } = [];

    /// <summary>True while a tick or a row's press is changing the editor's selection.</summary>
    public bool IsDriving { get; private set; }

    /// <summary>
    ///     Whether there are uses, "2 of 6 selected", and whether Split uses is offered: the shape offers Split off
    ///     selection, some use is ticked and some use of the shape stays behind, on this level or on another
    ///     (<paramref name="usesEverywhere" />, what the split itself counts).
    /// </summary>
    public (bool HasUses, string Summary, bool CanSplit) Publish(bool shapeCanSplit, int usesEverywhere)
    {
        var ticked = Rows.Count(r => r.IsChecked);
        return (Rows.Count > 0, localization.Format("Shapes_Uses_Summary", ticked, Rows.Count),
            shapeCanSplit && ticked > 0 && ticked < Math.Max(usesEverywhere, Rows.Count));
    }

    /// <summary>Re-reads the uses of <paramref name="template" /> on the active level; true when the rows changed.</summary>
    public bool Rebuild(ChordTemplate? template)
    {
        var uses = Read(template);
        var same = ReferenceEquals(template, _template) && uses.Count == Rows.Count
                   && uses.Select(u => u.Element).SequenceEqual(Rows.Select(r => r.Element), ReferenceEqualityComparer.Instance);
        _template = template;
        if (same)
        {
            SyncChecks();
            return false;
        }

        var chords = 0;
        var shapes = 0;
        Rows = [.. uses.Select(u => new ShapeUseRowViewModel(u.Element, u.TimeMs,
            u.Element is Chord
                ? localization.Format("Shapes_Use_Chord", ++chords)
                : localization.Format("Shapes_Use_HandShape", ++shapes),
            SetChecked, Jump))];
        SyncChecks();
        return true;
    }

    /// <summary>
    ///     A selection change: the ticks follow it while the tab is <paramref name="shown" /> (a tick or a press here
    ///     syncs its own rows); hidden, they wait for <see cref="CatchUp" />. True when the summary is to be published.
    /// </summary>
    public bool FollowSelection(bool shown)
    {
        if (!shown)
        {
            _stale = true;
            return false;
        }

        if (!IsDriving) SyncChecks();
        return true;
    }

    /// <summary>The tab is back on screen: the ticks re-read once if a selection change came while it was hidden.</summary>
    public bool CatchUp()
    {
        if (!_stale) return false;
        SyncChecks();
        return true;
    }

    /// <summary>Re-reads every tick from the editor's selection.</summary>
    public void SyncChecks()
    {
        _stale = false;
        if (Rows.Count == 0) return;
        var selection = editor.Selection;
        var chords = new HashSet<Chord>(selection.SelectedChords, ReferenceEqualityComparer.Instance);
        var shapes = new HashSet<HandShape>(selection.SelectedHandShapes, ReferenceEqualityComparer.Instance);
        foreach (var row in Rows)
        {
            row.Sync(row.Element switch
            {
                Chord chord => chords.Contains(chord),
                HandShape shape => shapes.Contains(shape),
                _ => false
            });
        }
    }

    /// <summary>
    ///     Next use: the first use of <paramref name="template" /> after the playhead (wrapping) is selected alone and
    ///     sought to; the answer is what the status rail says, or null without a level.
    /// </summary>
    public (string Text, StatusSeverity Severity)? Next(ChordTemplate template)
    {
        if (state.ActiveLevel is null) return null;
        var uses = Read(template);
        if (uses.Count == 0) return (localization["ChordShapes_Status_NoUseOnLevel"], StatusSeverity.Warning);
        var now = state.CurrentTimeMs;
        var index = Math.Max(0, uses.FindIndex(u => u.TimeMs > now + 0.5));
        Select(uses[index].Element, uses[index].TimeMs);
        return (localization.Format("ChordShapes_Status_NextUse", index + 1, uses.Count), StatusSeverity.Info);
    }

    private List<(ITimedElement Element, double TimeMs)> Read(ChordTemplate? template)
    {
        if (template is null || state.ActiveLevel is not { } level) return [];
        var id = template.Id;
        var beats = editor.Beats;
        return
        [
            .. level.Chords.Where(c => c.TemplateId == id).Select(c => ((ITimedElement)c, c.GetTimeMs(beats)))
                .Concat(level.HandShapes.Where(h => h.TemplateId == id).Select(h => ((ITimedElement)h, h.GetTimeMs(beats))))
                .OrderBy(u => u.Item2)
        ];
    }

    private void SetChecked(ShapeUseRowViewModel row, bool value) => Drive(() => Tick(row, value));

    private void Jump(ShapeUseRowViewModel row) => Drive(() => Select(row.Element, row.TimeMs));

    private void Drive(Action change)
    {
        IsDriving = true;
        try
        {
            change();
        }
        finally
        {
            IsDriving = false;
        }
    }

    /// <summary>Ticks or unticks <paramref name="row" /> with every use it is one grip with (<see cref="Grip" />).</summary>
    private void Tick(ShapeUseRowViewModel row, bool value)
    {
        var grip = Grip(row);
        // The rows read the answer first, so the summary published on the selection's own change already counts it.
        foreach (var use in grip) use.Sync(value);
        var selection = editor.Selection;
        foreach (var use in grip)
        {
            switch (use.Element)
            {
                case Chord chord when value && !selection.SelectedChords.Contains(chord):
                    selection.SelectedChords.Add(chord);
                    break;
                case Chord chord when !value:
                    selection.SelectedChords.Remove(chord);
                    break;
                case HandShape shape when value && !selection.SelectedHandShapes.Contains(shape):
                    selection.SelectedHandShapes.Add(shape);
                    break;
                case HandShape shape when !value:
                    selection.SelectedHandShapes.Remove(shape);
                    break;
            }
        }

        editor.RequestRedraw();
    }

    /// <summary>
    ///     <paramref name="row" /> and every use it is one grip with: a hand shape and the chords it covers, followed
    ///     through (a chord under two hand shapes joins both).
    /// </summary>
    private List<ShapeUseRowViewModel> Grip(ShapeUseRowViewModel row)
    {
        var grip = new List<ShapeUseRowViewModel> { row };
        for (var i = 0; i < grip.Count; i++)
        {
            var element = grip[i].Element;
            foreach (var other in Rows)
                if (!grip.Contains(other) && Pairs(element, other.Element)) grip.Add(other);
        }

        return grip;
    }

    private static bool Pairs(ITimedElement a, ITimedElement b) => (a, b) switch
    {
        (Chord chord, HandShape shape) => shape.Covers(chord.Position),
        (HandShape shape, Chord chord) => shape.Covers(chord.Position),
        _ => false
    };

    /// <summary>Selects one use alone and seeks to it.</summary>
    private void Select(ITimedElement element, double timeMs)
    {
        var selection = editor.Selection;
        selection.ClearSelections();
        if (element is Chord chord)
        {
            selection.SelectedChords.Add(chord);
            selection.SelectedChord = chord;
        }
        else if (element is HandShape shape)
        {
            selection.SelectedHandShapes.Add(shape);
        }

        editor.ExecuteSeek(timeMs);
        SyncChecks();
    }
}

/// <summary>One use on the Uses page.</summary>
internal sealed class ShapeUseRowViewModel : ObservableObject, IShapeUseRow
{
    private readonly Action<ShapeUseRowViewModel, bool> _setChecked;
    private bool _isChecked;

    internal ShapeUseRowViewModel(ITimedElement element, double timeMs, string label,
        Action<ShapeUseRowViewModel, bool> setChecked, Action<ShapeUseRowViewModel> jump)
    {
        Element = element;
        TimeMs = timeMs;
        Label = label;
        TimeText = ElapsedTimeLabel.Format(timeMs, 1);
        _setChecked = setChecked;
        JumpCommand = new RelayCommand(() => jump(this));
    }

    internal ITimedElement Element { get; }

    internal double TimeMs { get; }

    public string TimeText { get; }

    public string Label { get; }

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (value != _isChecked) _setChecked(this, value);
        }
    }

    public ICommand JumpCommand { get; }

    /// <summary>The selection's answer, written back without selecting anything.</summary>
    internal void Sync(bool value) => SetProperty(ref _isChecked, value, nameof(IsChecked));
}
