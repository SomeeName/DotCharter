// InspectorViewModel: the CURRENT SELECTION's field-level findings (bound by the Sound tab), the two "open the Issues tab" commands, and the IInspectorIssuesHost tie-back. The Issues tab itself is Inspector/InspectorIssuesViewModel; pre-export readiness is Inspector/ExportReadinessViewModel.

using CommunityToolkit.Mvvm.Input;
using DotCharter.UI.Controls;
using DotCharter.UI.ViewModels.Editor.Inspector;

namespace DotCharter.UI.ViewModels.Editor;

public partial class InspectorViewModel : IInspectorIssuesHost
{
    // ---- field-level validation (0.36, 04-inspector-redesign.md §9) --------
    //
    // ArrangementIssue has always carried the back-references (Note, Chord,
    // HandShape, Anchor, StringIndex); the panel simply never used them, so
    // every finding was a row in another tab instead of a marker on the field
    // that causes it. These project the CURRENT SELECTION's issues onto the
    // Sound tab. The Issues tab's 150 ms debounced recompute and
    // IssueGrouping.Build are untouched.

    /// <summary>
    ///     Issues that back-reference something in the current selection.
    ///     <para>
    ///         Materialized ONCE per notification and read by all five bound
    ///         properties; an iterator per property would be five passes of
    ///         Issues × Selection per recompute, with <c>List.Contains</c> as the
    ///         membership test, and <see cref="HasSelectionIssues" /> would re-run the
    ///         whole enumeration a second time to read one count. The selection
    ///         goes into one reference set first, so the pass is O(Issues +
    ///         Selection) rather than O(Issues × Selection).
    ///     </para>
    /// </summary>
    private IReadOnlyList<ArrangementIssue> SelectionIssues()
    {
        if (_selectionIssues != null) return _selectionIssues;

        var selected = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var note in SelectedNotes) selected.Add(note);
        foreach (var chord in SelectedChords) selected.Add(chord);
        foreach (var handShape in SelectedHandShapes) selected.Add(handShape);

        var hits = new List<ArrangementIssue>();
        if (selected.Count > 0)
        {
            foreach (var issue in Issues.Items)
            {
                var hit = (issue.Note != null && selected.Contains(issue.Note))
                          || (issue.Chord != null && selected.Contains(issue.Chord))
                          || (issue.HandShape != null && selected.Contains(issue.HandShape));
                if (hit) hits.Add(issue);
            }
        }

        return _selectionIssues = hits;
    }

    private List<ArrangementIssue>? _selectionIssues;

    /// <summary>PROBLEM count for the CURRENT SELECTION only — the header's ●2
    /// badge. It counts the folded rows the tab prints, not lint findings: a
    /// three-note chord outside its anchor is ONE problem. The FEHLER
    /// tab's global badge is unchanged.</summary>
    public int SelectionIssueCount => SelectionFieldIssues.Count;

    public bool HasSelectionIssues => SelectionIssues().Count > 0;

    /// <summary>True while at least one of the selection's issues would make the
    /// export REFUSE the package — so the badge can read danger rather than
    /// warning, and a charter cannot walk into a failed export.</summary>
    public bool SelectionBlocksExport =>
        SelectionIssues().Any(i => InspectorIssueFieldMap.BlocksExport(i.Type));

    /// <summary>One line per PROBLEM on the selection, prefixed "blocks export"
    /// when it does — findings that repeat across a chord's notes are folded
    /// into one row naming their strings (<see cref="InspectorFieldIssue.Fold" />).
    /// Rendered on the card, not in another tab.</summary>
    public IReadOnlyList<InspectorFieldIssue> SelectionFieldIssues
    {
        get
        {
            var issues = SelectionIssues();
            if (!ReferenceEquals(_foldedFrom, issues))
            {
                var tuning = _chartEditor.ActiveArrangement?.Tuning;
                var format = _localization["Inspector_StringLabelFmt"];
                _folded = InspectorFieldIssue.Fold(issues,
                    strings => GuitarStringLabel.List(strings, tuning?.Length ?? 6, tuning, format),
                    _localization);
                _foldedFrom = issues;
            }

            return _folded;
        }
    }

    // Keyed to the memoized list's IDENTITY, so whichever path drops that memo
    // drops this with it — no second clear site to forget.
    private IReadOnlyList<ArrangementIssue>? _foldedFrom;
    private IReadOnlyList<InspectorFieldIssue> _folded = [];

    /// <summary>Strings of the selected chord that carry a finding — the chord
    /// box's rail pips. A fresh array every time, as its StyledProperty
    /// contract requires.</summary>
    public IReadOnlyList<ChordBoxIssuePip> SelectionIssuePips =>
        SelectionIssues()
            .Where(i => i.StringIndex.HasValue)
            .Select(i => new ChordBoxIssuePip(i.StringIndex!.Value,
                InspectorIssueFieldMap.BlocksExport(i.Type)))
            .Distinct()
            .ToArray();

    /// <summary>Open the FEHLER tab on the selection's findings.</summary>
    [RelayCommand]
    private void ShowSelectionIssues() => SelectedTabIndex = IssuesTabIndex;

    internal const int IssuesTabIndex = 4;

    /// <summary>
    /// The Sound tab's way to the export verdict: it opens the Issues
    ///     tab, which OWNS the export check. The Sound tab's footer must not carry
    ///     a second "Check export" button that would run the check where its result
    ///     could not be seen, beside a tile that already claims "export
    ///     blockers" from the lint alone.
    /// </summary>
    [RelayCommand]
    private void ShowExportReadiness() => SelectedTabIndex = IssuesTabIndex;

    /// <summary>"4 findings" — the Sound rail's Findings badge, spoken and tipped; the count, in rows (<see cref="SelectionIssueCount" />).</summary>
    public string SelectionIssueSummary =>
        _localization.Plural("Inspector_Issue_FindingsOne", "Inspector_Issue_Findings", SelectionIssueCount);

    private void NotifySelectionIssuesChanged()
    {
        _selectionIssues = null;
        OnPropertyChanged(nameof(SelectionIssueSummary));
        OnPropertyChanged(nameof(SelectionIssueCount));
        OnPropertyChanged(nameof(HasSelectionIssues));
        OnPropertyChanged(nameof(SelectionBlocksExport));
        OnPropertyChanged(nameof(SelectionFieldIssues));
        OnPropertyChanged(nameof(SelectionIssuePips));
    }

    // ---- IInspectorIssuesHost: what the Issues tab asks of the inspector ----

    /// <summary>A fresh issue list: the selection's findings and the work queue follow it.</summary>
    void IInspectorIssuesHost.OnIssuesRecomputed()
    {
        NotifySelectionIssuesChanged();
        // The queue's counts are derived from the issue list, so they follow the
        // SAME 150 ms debounce rather than adding a timer of their own — but
        // ONLY while the Sound tab is showing. Everything the queue feeds is
        // bound by SoundInspectorView.axaml and nothing else, and a refresh
        // rebuilds the whole level's ordered sound list plus seven filter
        // passes; off-tab that was a whole-level rebuild nobody could see, per
        // committed edit. Same deferral as the Issues tab's severity groups, and
        // the queue's own Step/TriageFilter settle it for the keybinds.
        if (IsSoundTabSelected) Queue.Refresh();
        else Queue.MarkStale();
    }

    /// <summary>An issue jump's selection — the inspector talking to itself, so it
    /// must not switch the tab away from Issues mid-triage (see _programmaticSelection).</summary>
    void IInspectorIssuesHost.SelectProgrammatically(Action select)
    {
        _programmaticSelection = true;
        try
        {
            select();
        }
        finally
        {
            _programmaticSelection = false;
        }
    }
}
