using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Services.Editing.Quality;

namespace DotCharter.UI.ViewModels.Editor.Inspector;

/// <summary>A choice of the Whole project card's Arrangement filter: one arrangement, or every one (<see cref="Arrangement" /> null).</summary>
public sealed record ProjectArrangementChoice(Arrangement? Arrangement, string Name)
{
    public override string ToString() => Name;
}

/// <summary>One line of the Whole project card's findings list: <c>stamp · title · arrangement</c>.</summary>
public sealed record ProjectFindingRow(QualityFinding Finding, string Stamp, string Title, string ArrangementName)
{
    public override string ToString() => $"{Stamp} {Title} · {ArrangementName}";
}

/// <summary>
///     Inspector design D's project-wide findings on the Whole project card: an Arrangement choice (each arrangement,
///     then All arrangements), the whole-project check's findings for it, and Jump / Fix for the selected one. Jump makes
///     the finding's arrangement the active one and jumps as the findings list does; Fix applies the fix menu's default
///     fix and only for a finding of the arrangement on screen (D: "Open that arrangement to fix this finding"). The
///     per-arrangement rows above it stay as they are. Fed by <see cref="ExportReadinessViewModel" />'s report.
/// </summary>
public sealed partial class ProjectFindingsList : ObservableObject
{
    private readonly ILocalizationService _localization;
    private readonly ProjectState _projectState;
    private readonly Func<Arrangement?> _active;
    private readonly Action<Arrangement> _activate;
    private IReadOnlyList<QualityFinding> _findings = [];
    private bool _refilling;

    [ObservableProperty] private ProjectArrangementChoice? _selectedArrangement;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(JumpCommand))]
    [NotifyCanExecuteChangedFor(nameof(FixCommand))]
    [NotifyPropertyChangedFor(nameof(FixTip))]
    private ProjectFindingRow? _selected;

    public ProjectFindingsList(ILocalizationService localization, ProjectState projectState, Func<Arrangement?> active, Action<Arrangement> activate)
    {
        _localization = localization;
        _projectState = projectState;
        _active = active;
        _activate = activate;
        projectState.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(ProjectState.ActiveArrangement)) return;
            FixCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(FixTip));
        };
    }

    /// <summary>Set by the Issues tab: its jump to a rule finding (switches the level, selects the element).</summary>
    internal Action<ArrangementIssue>? JumpToIssue { get; set; }

    /// <summary>Set by the Issues tab: its row Fix (jump, then the fix menu's default fix).</summary>
    internal Action<ArrangementIssue>? FixIssue { get; set; }

    /// <summary>Set by the Issues tab: a seek for a finding with a time and no rule (an export or audio finding).</summary>
    internal Action<double>? Seek { get; set; }

    public ObservableCollection<ProjectArrangementChoice> ArrangementChoices { get; } = [];

    public BulkObservableCollection<ProjectFindingRow> Items { get; } = [];

    public bool HasItems => Items.Count > 0;

    /// <summary>Why Fix is off, or what it does.</summary>
    public string FixTip => Selected is { Finding.Arrangement: { } owner } && !ReferenceEquals(owner, _active())
        ? _localization["Inspector_Issues_ProjectFixOtherTip"]
        : _localization["Inspector_Issues_ProjectFixTip"];

    /// <summary>The check landed (or was dropped, <paramref name="findings" /> empty): the choices and the list follow it.</summary>
    internal void Show(IReadOnlyList<QualityFinding> findings, IReadOnlyList<Arrangement> arrangements)
    {
        _findings = findings;
        var keep = SelectedArrangement?.Arrangement;
        ArrangementChoices.Clear();
        foreach (var arrangement in arrangements) ArrangementChoices.Add(new ProjectArrangementChoice(arrangement, arrangement.Name));
        if (arrangements.Count > 0) ArrangementChoices.Add(new ProjectArrangementChoice(null, _localization["Inspector_Issues_AllArrangements"]));
        var wanted = keep ?? _active();
        _refilling = true;
        try
        {
            SelectedArrangement = ArrangementChoices.FirstOrDefault(c => c.Arrangement != null && ReferenceEquals(c.Arrangement, wanted))
                                  ?? ArrangementChoices.LastOrDefault();
        }
        finally { _refilling = false; }

        Fill();
    }

    partial void OnSelectedArrangementChanged(ProjectArrangementChoice? value)
    {
        if (!_refilling) Fill();
    }

    private void Fill()
    {
        var only = SelectedArrangement?.Arrangement;
        var beats = (IReadOnlyList<BeatMarker>)_projectState.Beats;
        Items.ResetWith(_findings
            .Where(f => only == null || ReferenceEquals(f.Arrangement, only))
            .Select(f => new ProjectFindingRow(f, ChartPositionLabel.Format(beats, f.TimeMs), QualityFindingText.Message(f, _localization),
                f.Arrangement?.Name ?? _localization["Inspector_Issues_SongWideRow"])));
        Selected = null;
        OnPropertyChanged(nameof(HasItems));
    }

    /// <summary>
    ///     Whether the last whole-project check found <paramref name="issue" /> stopping the export: an error of the same rule
    ///     on the same element (the export validator's verdict merged into it), the source of the Fixes card's status line.
    /// </summary>
    internal bool Blocks(ArrangementIssue issue) => _findings.Any(f => f is { Severity: QualitySeverity.Error, Rule: { } rule }
        && rule.Type == issue.Type && ReferenceEquals(rule.Note, issue.Note) && ReferenceEquals(rule.Chord, issue.Chord)
        && ReferenceEquals(rule.HandShape, issue.HandShape) && ReferenceEquals(rule.Anchor, issue.Anchor)
        && Nullable.Equals(rule.TimeMs, issue.TimeMs));

    private bool CanJump() => Selected is { Finding: var f } && (f.Rule != null || f.TimeMs.HasValue);

    [RelayCommand(CanExecute = nameof(CanJump))]
    private void Jump()
    {
        if (Selected?.Finding is not { } finding) return;
        if (finding.Arrangement is { } owner && !ReferenceEquals(owner, _active())) _activate(owner);
        if (finding.Rule is { } rule) JumpToIssue?.Invoke(rule);
        else if (finding.TimeMs is { } time) Seek?.Invoke(time);
        FixCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(FixTip));
    }

    private bool CanFix() => Selected?.Finding is { Rule: { } rule, Arrangement: { } owner } finding && ReferenceEquals(owner, _active())
        && IssueFixViewModel.CanOffer(rule, owner, finding.Level, _projectState.Beats);

    [RelayCommand(CanExecute = nameof(CanFix))]
    private void Fix()
    {
        if (Selected?.Finding.Rule is { } rule && CanFix()) FixIssue?.Invoke(rule);
    }
}
