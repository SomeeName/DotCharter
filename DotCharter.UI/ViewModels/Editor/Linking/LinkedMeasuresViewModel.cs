using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Core.Linking;
using DotCharter.Services.Editing.Linking;
using DotCharter.UI.Editing.Linking;

namespace DotCharter.UI.ViewModels.Editor.Linking;

/// <summary>
///     Linked measures in the editor: the hover card over a LINKS chip, the link panel, the
///     "Linked · changed N places [Only here]" toast after an edit was copied, the Inspector's
///     "Linked · edits apply to N places" banner for the selection, and "Link identical measures".
/// </summary>
public sealed partial class LinkedMeasuresViewModel : ObservableObject
{
    private readonly ChartEditorViewModel _editor;
    private readonly ProjectState _state;
    private readonly IHistoryService _history;
    private readonly ILocalizationService _localization;
    private readonly IConfigurationService _config;
    private LinkPropagationReport? _toastReport;
    private (LinkGroup Group, LinkPlacement Placement)? _bannerPlacement;

    public LinkedMeasuresViewModel(ChartEditorViewModel editor, ProjectState state, IHistoryService history,
        ILocalizationService localization, IConfigurationService config)
    {
        _editor = editor;
        _state = state;
        _history = history;
        _localization = localization;
        _config = config;
        Panel = new LinkPanelViewModel(state, history, localization, GoTo);
        Highlight.Changed += RefreshHoverCard;
        history.LinksPropagated += OnLinksPropagated;
        history.HistoryChanged += OnHistoryChanged;
        state.PropertyChanged += OnStateChanged;
        editor.Selection.SelectedNotes.CollectionChanged += OnSelectionChanged;
        editor.Selection.SelectedChords.CollectionChanged += OnSelectionChanged;
        editor.LinkedMeasures = this;
    }

    public LinkHighlight Highlight => _editor.LinkHighlight;
    public LinkPanelViewModel Panel { get; }

    // ── hover card ──────────────────────────────────────────────────────────

    [ObservableProperty] private bool _isHoverCardVisible;
    [ObservableProperty] private string _hoverTitle = "";
    [ObservableProperty] private IBrush _hoverBrush = Brushes.Transparent;
    public ObservableCollection<string> HoverLines { get; } = [];

    private void RefreshHoverCard()
    {
        var project = _state.ActiveProject;
        var group = project?.LinkGroups.FirstOrDefault(g => g.Id == Highlight.HoveredGroupId);
        IsHoverCardVisible = group != null;
        if (project == null || group == null) return;

        var measures = MeasureGrid.Build(project.Beats);
        var length = group.Placements.Count == 0 ? 0 : LinkMeasureLabels.MeasureCount(measures, group.Placements[0]);
        var name = string.IsNullOrEmpty(group.Name) ? "" : $" · {group.Name}";
        HoverTitle = $"{_localization.Format("Link_GroupTitle", group.Letter)}{name} · " +
                     _localization.Plural("LinkPanel_MeasuresOne", "LinkPanel_MeasuresFmt", length);
        HoverBrush = new SolidColorBrush(LinkLaneModel.ColorOf(project, group));
        HoverLines.Clear();
        foreach (var arrangement in project.Arrangements)
        {
            var starts = group.Placements.Where(p => ReferenceEquals(p.Arrangement, arrangement))
                .OrderBy(p => p.Position)
                .Select(p => $"M{LinkMeasureLabels.MeasureAt(measures, p.Position)}")
                .ToList();
            if (starts.Count > 0) HoverLines.Add($"{arrangement.Name} ×{starts.Count} · {string.Join(", ", starts)}");
        }

        HoverLines.Add(_localization["LinkHover_Click"]);
    }

    // ── panel ───────────────────────────────────────────────────────────────

    /// <summary>Opens the panel on <paramref name="groupId" />, checking the placement at
    /// <paramref name="focus" /> in <paramref name="arrangement" /> (default: the one on screen).</summary>
    public void OpenPanel(string groupId, Arrangement? arrangement = null, FractionalPosition? focus = null)
    {
        Highlight.SetSelected(groupId);
        Panel.Open(groupId, arrangement ?? _editor.ActiveArrangement, focus ?? _bannerPlacement?.Placement.Position);
    }

    /// <summary>The panel closed: nothing stays emphasised.</summary>
    public void PanelClosed()
    {
        Highlight.SetSelected(null);
        Panel.Close();
    }

    private void GoTo(LinkPlacement placement)
    {
        if (placement.Arrangement is { } arrangement && !ReferenceEquals(arrangement, _editor.ActiveArrangement))
            _editor.ActiveArrangement = arrangement;
        if (_state.ActiveProject is { } project) _editor.ExecuteSeek(placement.Position.GetPositionMs(project.Beats));
    }

    // ── toast ───────────────────────────────────────────────────────────────

    [ObservableProperty] private bool _isToastVisible;
    [ObservableProperty] private string _toastLetter = "";
    [ObservableProperty] private string _toastTitle = "";
    [ObservableProperty] private string _toastText = "";
    [ObservableProperty] private IBrush _toastBrush = Brushes.Transparent;
    [ObservableProperty] private bool _canOnlyHere;

    /// <summary>Bumped on every new toast, so the view restarts its dismiss timer.</summary>
    [ObservableProperty] private int _toastSerial;

    private void OnLinksPropagated(IReadOnlyList<LinkPropagationReport> reports)
    {
        // A generator sharing its result across a group is not an edit to take back "only here".
        if (_state.ActiveProject is not { } project || reports.FirstOrDefault(r => r.Policy == LinkPolicy.Propagate) is not { } report)
            return;
        var group = project.LinkGroups.FirstOrDefault(g => g.Id == report.GroupId);
        // Every place that now holds the edit, the one it was made in included, so the count
        // matches the per-arrangement breakdown beside it.
        var places = group?.Placements.Count ?? report.Targets.Count + 1;
        var perArrangement = group == null ? "" : LinkLaneModel.PerArrangement(project, group);
        _toastReport = report;
        ShowToast(report.Letter, _localization["LinkToast_Linked"],
            _localization.Plural("LinkToast_ChangedOne", "LinkToast_ChangedFmt", places, perArrangement),
            group == null ? null : LinkLaneModel.ColorOf(project, group), onlyHere: true);
        Highlight.Flash(report.GroupId, report.Targets);
    }

    private void ShowToast(string letter, string title, string text, Color? color, bool onlyHere)
    {
        ToastLetter = letter;
        ToastTitle = title;
        ToastText = text;
        ToastBrush = color is { } c ? new SolidColorBrush(c) : Brushes.Transparent;
        CanOnlyHere = onlyHere;
        IsToastVisible = true;
        ToastSerial++;
    }

    [RelayCommand]
    private void OnlyHere()
    {
        if (_toastReport is { } report) _history.OnlyHere(report);
        DismissToast();
    }

    [RelayCommand]
    public void DismissToast()
    {
        IsToastVisible = false;
        _toastReport = null;
    }

    // ── inspector banner ────────────────────────────────────────────────────

    [ObservableProperty] private bool _isBannerVisible;
    [ObservableProperty] private string _bannerText = "";
    [ObservableProperty] private IBrush _bannerBrush = Brushes.Transparent;

    private void OnSelectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshBanner();

    /// <summary>Re-reads which placement (if any) holds the first selected note or chord.</summary>
    public void RefreshBanner()
    {
        var project = _state.ActiveProject;
        var at = FirstSelected();
        _bannerPlacement = at is { } position ? LinkLaneModel.PlacementAt(project, _editor.ActiveArrangement, position) : null;
        IsBannerVisible = _bannerPlacement != null;
        OnlyThisOneCommand.NotifyCanExecuteChanged();
        if (project == null || _bannerPlacement is not { } hit) return;
        var others = hit.Group.Placements.Count;
        BannerText = _localization.Plural("LinkBanner_AppliesOne", "LinkBanner_AppliesFmt", others,
            LinkLaneModel.PerArrangement(project, hit.Group));
        BannerBrush = new SolidColorBrush(LinkLaneModel.ColorOf(project, hit.Group));
    }

    private FractionalPosition? FirstSelected()
    {
        FractionalPosition? first = null;
        foreach (var note in _editor.Selection.SelectedNotes)
        {
            if (first == null || note.Position < first) first = note.Position;
        }

        foreach (var chord in _editor.Selection.SelectedChords)
        {
            if (first == null || chord.Position < first) first = chord.Position;
        }

        return first;
    }

    private bool CanOnlyThisOne() => _bannerPlacement != null;

    /// <summary>Unlinks the placement the selection is in, before editing it.</summary>
    [RelayCommand(CanExecute = nameof(CanOnlyThisOne))]
    private void OnlyThisOne()
    {
        if (_state.ActiveProject is not { } project || _bannerPlacement is not { } hit) return;
        if (LinkGroupEdits.Unlink(project, hit.Group.Id, [hit.Placement]) is not { } action) return;
        _history.EndGesture();
        _history.Do(action);
    }

    [RelayCommand]
    private void OpenBannerPanel()
    {
        if (_bannerPlacement is not { } hit) return;
        Highlight.RequestPanel(new LinkPanelRequest(hit.Group.Id, null, default, hit.Placement.Arrangement, hit.Placement.Position));
    }

    // ── link identical measures ─────────────────────────────────────────────

    /// <summary>
    ///     Links the selection's measures (or the measure at the playhead) with every run that
    ///     already plays the same. Reports the result in the toast.
    /// </summary>
    [RelayCommand]
    public void LinkIdentical()
    {
        if (_state.ActiveProject is not { } project || _editor.ActiveArrangement is not { } arrangement) return;
        var (start, end) = SelectionSpan(project);
        var rule = MeasureContentOptions.Detection with { Spacing = ChartSpacingEnforcer.SpacingGapOf(_config.LoadAppConfig()) };
        var plan = LinkIdenticalMeasures.Plan(project, arrangement, start, end, rule);
        _toastReport = null;
        if (plan.Action == null)
        {
            ShowToast("", _localization["LinkIdentical_Title"], _localization[$"LinkCreate_{plan.Refusal}"], null, onlyHere: false);
            return;
        }

        _history.EndGesture();
        _history.Do(plan.Action);
        var group = project.LinkGroups.LastOrDefault();
        if (group == null) return;
        ShowToast(group.Letter, _localization["LinkToast_Linked"],
            _localization.Plural("LinkIdentical_DoneOne", "LinkIdentical_DoneFmt", group.Placements.Count,
                LinkLaneModel.PerArrangement(project, group)),
            LinkLaneModel.ColorOf(project, group), onlyHere: false);
        Highlight.Flash(group.Id, [.. group.Placements]);
    }

    private (FractionalPosition First, FractionalPosition Last) SelectionSpan(ChartProject project)
    {
        FractionalPosition? first = null, last = null;
        foreach (var note in _editor.Selection.SelectedNotes) Extend(note.Position);
        foreach (var chord in _editor.Selection.SelectedChords) Extend(chord.Position);
        if (first is { } a && last is { } b) return (a, b);

        var at = TimelineMath.PositionFromMs(_editor.PlayheadMs, project.Beats);
        return (at, at);

        void Extend(FractionalPosition p)
        {
            if (first == null || p < first) first = p;
            if (last == null || p > last) last = p;
        }
    }

    // ── lifetime ────────────────────────────────────────────────────────────

    private void OnHistoryChanged()
    {
        if (Panel.GroupId != null) Panel.Refresh();
        RefreshBanner();
        if (Highlight.HoveredGroupId != null) RefreshHoverCard();
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProjectState.ActiveProject))
        {
            DismissToast();
            Highlight.Clear();
            Panel.Refresh();
        }

        if (e.PropertyName is nameof(ProjectState.ActiveProject) or nameof(ProjectState.ActiveArrangement)) RefreshBanner();
    }
}
