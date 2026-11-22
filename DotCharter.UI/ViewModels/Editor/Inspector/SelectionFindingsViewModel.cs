using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DotCharter.UI.ViewModels.Editor.Inspector;

/// <summary>
///     The Note tab's Findings page (design D): the selection's findings as a list (one row per problem,
///     <see cref="InspectorFieldIssue.Fold" />), the picked one's detail ("Open this issue" on the Issues tab, "Jump to
///     sound", "Open grip editor" for a fingering finding), "Fix all" for the findings that have a default repair
///     (<see cref="IssueFixViewModel.DefaultFix" />; asked first in the footer, written as ONE undo step), and a review of
///     the selected sounds one at a time (Previous / Next sound seeks to each). The repair count is worked out once per
///     re-read and only while the page is on screen (entering the page re-reads). Exposed as
///     <c>InspectorViewModel.Findings</c>; a plain collaborator, not DI.
/// </summary>
public sealed partial class SelectionFindingsViewModel : ObservableObject, IDisposable
{
    private readonly Func<IReadOnlyList<InspectorFieldIssue>> _rows;
    private readonly Func<IReadOnlyList<ArrangementIssue>> _issues;
    private readonly ChartEditorViewModel _editor;
    private readonly IHistoryService _history;
    private readonly ILocalizationService _loc;
    private readonly Action<ArrangementIssue> _openIssue;
    private readonly SoundSectionsViewModel _sections;
    private readonly Action<string, Action> _confirm;
    private InspectorFieldIssue? _picked;
    private int _cursor;
    private int? _fixable;

    /// <summary><paramref name="rows" /> are the selection's folded rows, <paramref name="issues" /> every finding behind
    /// them; <paramref name="openIssue" /> shows one on the Issues tab; <paramref name="confirm" /> asks a question in the
    /// footer and runs the action on Confirm.</summary>
    public SelectionFindingsViewModel(Func<IReadOnlyList<InspectorFieldIssue>> rows, Func<IReadOnlyList<ArrangementIssue>> issues,
        ChartEditorViewModel editor, IHistoryService history, ILocalizationService localization, Action<ArrangementIssue> openIssue,
        SoundSectionsViewModel sections, Action<string, Action> confirm)
    {
        _rows = rows;
        _issues = issues;
        _editor = editor;
        _history = history;
        _loc = localization;
        _openIssue = openIssue;
        _sections = sections;
        _confirm = confirm;
        _editor.Selection.SelectionChanged += OnSelectionChanged;
        _sections.PropertyChanged += OnSectionsChanged;
    }

    /// <summary>How many times the repair count was worked out (a structural probe for tests).</summary>
    internal int FixablePasses { get; private set; }

    public IReadOnlyList<InspectorFieldIssue> Rows => _rows();

    /// <summary>The row the list has picked (the first while none is).</summary>
    public InspectorFieldIssue? Picked
    {
        get => _picked is { } picked && Rows.Contains(picked) ? picked : Rows.Count > 0 ? Rows[0] : null;
        set
        {
            if (value is null || Equals(_picked, value)) return;
            _picked = value;
            Reread();
        }
    }

    public bool HasPicked => Picked is not null;

    public bool HasNone => Rows.Count == 0;

    public string PickedTitle => Picked is { } row ? row.Code + " · " + row.Message : string.Empty;

    /// <summary>"Blocks the export" or "Needs attention".</summary>
    public string PickedSeverity => Picked is { BlocksExport: true } row ? row.BlocksExportLabel
        : Picked is null ? string.Empty : _loc["Inspector_Findings_NeedsAttention"];

    public bool PickedBlocksExport => Picked is { BlocksExport: true };

    public string PickedDetail => Picked?.Detail ?? string.Empty;

    public bool CanJump => Picked is { Source.HasTime: true };

    /// <summary>The selection's findings that have a default repair now: worked out once per re-read, and only while the
    /// Findings page is on screen.</summary>
    public int FixableCount => _fixable ??= _sections.IsFindings ? CountFixable() : 0;

    public string FixAllLabel => _loc.Format("Inspector_Findings_FixAll", FixableCount);

    public bool CanFixAll => FixableCount > 0;

    /// <summary>The picked finding is about a chord's fingering: "Open grip editor" goes to the Shape page.</summary>
    public bool CanOpenGrip => _sections.OffersShape && Picked?.Source is { } issue &&
                               (issue.Chord is not null || issue.HandShape is not null) && IsFingering(issue.Type);

    private static bool IsFingering(ArrangementIssueType type) => type is ArrangementIssueType.FingerMissingOnFrettedString
        or ArrangementIssueType.FingeringAnchorMismatch or ArrangementIssueType.PossiblyWrongChordFingering
        or ArrangementIssueType.FingerSetForOpenString or ArrangementIssueType.FirstFingerNotOnLowestFret
        or ArrangementIssueType.IndexFingerNotOnAnchorFret;

    private int CountFixable()
    {
        FixablePasses++;
        return _issues().Count(issue => Plan(issue) != null);
    }

    private IChartAction? Plan(ArrangementIssue issue) =>
        IssueFixViewModel.DefaultFix(issue, _editor.ActiveArrangement, _editor.ActiveLevel, _editor.Beats);

    /// <summary>The selected sounds in time order: what Previous / Next sound step through.</summary>
    private IReadOnlyList<ITimedElement> Sounds => [.. _editor.Selection.SelectedNotes.Cast<ITimedElement>()
        .Concat(_editor.Selection.SelectedChords).OrderBy(SoundQueueViewModel.PositionOf)];

    public bool CanReview => Sounds.Count > 1;

    /// <summary>The sound the review is on: "0:12.50 · Am (3 of 5)".</summary>
    public string ReviewLabel
    {
        get
        {
            var sounds = Sounds;
            if (sounds.Count == 0) return _loc["Inspector_Findings_NoSound"];
            var at = Math.Clamp(_cursor, 0, sounds.Count - 1);
            var sound = sounds[at];
            var time = _editor.Beats.Count > 0
                ? InspectorPlayheadClock.Format(SoundQueueViewModel.PositionOf(sound).GetPositionMs(_editor.Beats))
                : "—";
            return _loc.Format("Inspector_Findings_ReviewAt", time, Name(sound), at + 1, sounds.Count);
        }
    }

    private string Name(ITimedElement sound) => sound switch
    {
        Chord chord when _editor.ActiveArrangement?.FindChordTemplate(chord.TemplateId) is { Name.Length: > 0 } grip => grip.Name,
        Chord => _loc["Inspector_Findings_Chord"],
        GuitarNote note => _loc.Format("Inspector_Findings_Note", note.Fret),
        _ => string.Empty
    };

    /// <summary>Opens the picked finding on the Issues tab (its element selected, its fix offered).</summary>
    [RelayCommand]
    private void OpenPicked()
    {
        if (Picked?.Source is { } issue) _openIssue(issue);
    }

    /// <summary>Seeks the playhead to the picked finding; the selection stays.</summary>
    [RelayCommand]
    private void JumpToPicked()
    {
        if (Picked?.Source is { TimeMs: { } ms }) _editor.ExecuteSeek(ms);
    }

    /// <summary>Goes to the Shape page, where the chord's grip and fingers are edited.</summary>
    [RelayCommand]
    private void OpenGrip()
    {
        if (CanOpenGrip) _sections.ChooseCommand.Execute(SoundSection.Shape);
    }

    /// <summary>Asks "Fix N findings across M sounds?" in the footer, then applies every default repair the selection's
    /// findings have as ONE undo step, each re-planned on the live chart as it runs (an earlier repair may have settled a
    /// later finding).</summary>
    [RelayCommand]
    private void FixAll()
    {
        var issues = _issues().ToArray();
        var fixable = issues.Where(issue => Plan(issue) != null).ToArray();
        if (fixable.Length == 0) return;
        var sounds = fixable.Select(issue => (object?)issue.Chord ?? (object?)issue.Note ?? issue.HandShape).OfType<object>()
            .Distinct().Count();
        var text = fixable.Length == 1 ? _loc["Inspector_Findings_FixAllAskOne"]
            : sounds <= 1 ? _loc.Format("Inspector_Findings_FixAllAskOneSound", fixable.Length)
            : _loc.Format("Inspector_Findings_FixAllAsk", fixable.Length, sounds);
        _confirm(text, () =>
        {
            _history.Do(new FixAllAction(issues, Plan));
            Reread();
        });
    }

    [RelayCommand]
    private void PreviousSound() => Step(-1);

    [RelayCommand]
    private void NextSound() => Step(1);

    private void Step(int direction)
    {
        var sounds = Sounds;
        if (sounds.Count == 0) return;
        _cursor = ((Math.Clamp(_cursor, 0, sounds.Count - 1) + direction) % sounds.Count + sounds.Count) % sounds.Count;
        if (_editor.Beats.Count > 0) _editor.ExecuteSeek(SoundQueueViewModel.PositionOf(sounds[_cursor]).GetPositionMs(_editor.Beats));
        OnPropertyChanged(nameof(ReviewLabel));
    }

    private static readonly string[] BoundNames =
    [
        nameof(Rows), nameof(Picked), nameof(HasPicked), nameof(HasNone), nameof(PickedTitle), nameof(PickedSeverity),
        nameof(PickedBlocksExport), nameof(PickedDetail), nameof(CanJump), nameof(FixableCount), nameof(FixAllLabel),
        nameof(CanFixAll), nameof(CanOpenGrip), nameof(CanReview), nameof(ReviewLabel)
    ];

    /// <summary>The selection's findings changed: re-read every bound value, while the page is on screen (entering it
    /// re-reads).</summary>
    internal void Reread()
    {
        _fixable = null;
        if (!_sections.IsFindings) return;
        foreach (var name in BoundNames) OnPropertyChanged(name);
    }

    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        _cursor = 0;
        _picked = null;
        Reread();
    }

    private void OnSectionsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SoundSectionsViewModel.Section) && _sections.IsFindings) Reread();
    }

    public void Dispose()
    {
        _editor.Selection.SelectionChanged -= OnSelectionChanged;
        _sections.PropertyChanged -= OnSectionsChanged;
    }

    /// <summary>"Fix all" as one history step: the first run plans each repair on the live chart just before it runs; undo
    /// reverses them in reverse order, redo replays the same repairs.</summary>
    internal sealed class FixAllAction(IReadOnlyList<ArrangementIssue> issues, Func<ArrangementIssue, IChartAction?> plan) : IChartAction, ILinkPolicyAction
    {
        private List<IChartAction>? _done;

        /// <summary>Dynamic, like <see cref="CompositeAction" />: the strictest policy of the repairs that ran (the
        /// history reads it after <see cref="Execute" />); before that, the safe default of an unknown tool.</summary>
        public LinkPolicy LinkPolicy => _done is { } done ? LinkPolicies.Of(new CompositeAction(done)) : LinkPolicy.UnlinkOnDivergence;

        public void Execute()
        {
            if (_done is { } done)
            {
                foreach (var action in done) action.Execute();
                return;
            }

            _done = [];
            foreach (var issue in issues)
            {
                if (plan(issue) is not { HasEffect: true } fix) continue;
                fix.Execute();
                _done.Add(fix);
            }
        }

        public void Undo()
        {
            if (_done is null) return;
            for (var i = _done.Count - 1; i >= 0; i--) _done[i].Undo();
        }
    }
}
