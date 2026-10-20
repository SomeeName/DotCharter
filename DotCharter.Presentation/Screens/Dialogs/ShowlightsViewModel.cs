using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Core.Domain.Events;
using DotCharter.Core.Domain.Primitives;
using DotCharter.Core.Utilities;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Services.State;
using DotCharter.Tone.Infrastructure.Configuration;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>Tools ▸ Showlights. One instance per open, which starts on the playhead.</summary>
public sealed class ShowlightsViewModel : DialogViewModel<bool>, IShowlightsScreen, ICancelableDialog
{
    private static readonly ShowlightLane[] Lanes = [ShowlightLane.Fog, ShowlightLane.Beams, ShowlightLane.Lasers];

    private readonly ChartEditDialogServices _edit;
    private readonly IReadOnlyList<string?>[] _laneTypes = new IReadOnlyList<string?>[3];
    private readonly int[] _laneIndex = new int[3];
    private IReadOnlyList<Position> _existing = [];
    private FractionalPosition _target = FractionalPosition.Zero;
    private int _existingIndex = -1;
    private string _targetPositionText = "";
    private string _targetMsText = "";
    private bool _hasShowlightsAtTarget;

    public ShowlightsViewModel(ChartEditDialogServices edit)
    {
        _edit = edit;
        FogOptions = Options(ShowlightLane.Fog, ShowlightCatalog.FogDefinitions);
        BeamsOptions = Options(ShowlightLane.Beams, ShowlightCatalog.BeamDefinitions);
        LasersOptions = Options(ShowlightLane.Lasers, ShowlightCatalog.LaserDefinitions);
        UsePlayheadCommand = new RelayCommand(UsePlayhead);
        DeleteAtPositionCommand = new RelayCommand(DeleteAtPosition);
        SaveCommand = new RelayCommand(Save);
        CancelCommand = new RelayCommand(() => TryCancel());

        RefreshExistingPositions();
        UsePlayhead();
        edit.Logger.Info(AppLogCategory.Events,
            $"Showlights dialog opened. ExistingPositions={_existing.Count}, target={Describe(_target)}.");
    }

    public string TargetPositionText
    {
        get => _targetPositionText;
        private set => SetProperty(ref _targetPositionText, value);
    }

    public string TargetMsText
    {
        get => _targetMsText;
        private set => SetProperty(ref _targetMsText, value);
    }

    public ICommand UsePlayheadCommand { get; }

    public IReadOnlyList<string> FogOptions { get; }

    public int SelectedFogIndex
    {
        get => _laneIndex[0];
        set => SetLane(0, value);
    }

    public IReadOnlyList<string> BeamsOptions { get; }

    public int SelectedBeamsIndex
    {
        get => _laneIndex[1];
        set => SetLane(1, value);
    }

    public IReadOnlyList<string> LasersOptions { get; }

    public int SelectedLasersIndex
    {
        get => _laneIndex[2];
        set => SetLane(2, value);
    }

    public bool HasExistingPositions => _existing.Count > 0;

    public IReadOnlyList<IShowlightPosition> ExistingPositions => _existing;

    public int SelectedExistingIndex
    {
        get => _existingIndex;
        set
        {
            if (!SetProperty(ref _existingIndex, value)) return;
            if (value >= 0 && value < _existing.Count) LoadPosition(_existing[value].At, seek: true);
        }
    }

    public bool HasShowlightsAtTarget
    {
        get => _hasShowlightsAtTarget;
        private set => SetProperty(ref _hasShowlightsAtTarget, value);
    }

    public ICommand DeleteAtPositionCommand { get; }

    public ICommand SaveCommand { get; }

    public ICommand CancelCommand { get; }

    public bool TryCancel()
    {
        if (!IsClosed) _edit.Logger.Info(AppLogCategory.Events, "Showlights dialog canceled.");
        Close(false);
        return true;
    }

    private IReadOnlyList<Showlight> Showlights =>
        (IReadOnlyList<Showlight>?)_edit.State.ActiveProject?.Showlights ?? [];

    private void UsePlayhead() => LoadPosition(_edit.Editor.SnapToGrid(_edit.State.CurrentTimeMs));

    private void Save()
    {
        var project = _edit.State.ActiveProject;
        if (project == null)
        {
            Close(false);
            return;
        }

        var existingKnown = project.Showlights
            .Where(s => s.Position == _target && ShowlightCatalog.TryGetLane(s.Type, s.Note, out _))
            .ToList();
        var desired = DesiredShowlights();
        if (!Differ(existingKnown, desired))
        {
            _edit.Logger.Info(AppLogCategory.Events, $"Showlights dialog closed without changes at {Describe(_target)}.");
            Close(false);
            return;
        }

        var actions = new List<IChartAction>();
        if (existingKnown.Count > 0)
        {
            actions.Add(new RemoveElementsAction<Showlight>(project.Showlights, existingKnown, s => s.Position,
                _edit.Editor.RequestRedraw));
        }

        if (desired.Count > 0)
        {
            actions.Add(new AddElementsAction<Showlight>(project.Showlights, desired.AsEnumerable().Reverse(),
                s => s.Position, _edit.Editor.RequestRedraw));
        }

        var action = actions.Count == 1 ? actions[0] : new CompositeAction(actions) { LabelKey = "History_EditShowlights" };
        if (_edit.History.Do(action))
        {
            _edit.Logger.Info(AppLogCategory.Events,
                $"Showlights saved at {Describe(_target)}. Fog='{FogOptions[_laneIndex[0]]}', Beams='{BeamsOptions[_laneIndex[1]]}', Lasers='{LasersOptions[_laneIndex[2]]}'.");
        }

        Close(true);
    }

    private void DeleteAtPosition()
    {
        var project = _edit.State.ActiveProject;
        var atTarget = project?.Showlights.Where(s => s.Position == _target).ToList() ?? [];
        if (project == null || atTarget.Count == 0)
        {
            Close(false);
            return;
        }

        if (_edit.History.Do(new RemoveElementsAction<Showlight>(project.Showlights, atTarget, s => s.Position,
                _edit.Editor.RequestRedraw)))
        {
            _edit.Logger.Info(AppLogCategory.Events,
                $"Deleted {atTarget.Count} showlight entries at {Describe(_target)}.");
        }

        Close(true);
    }

    private void SetLane(int lane, int index)
    {
        if (_laneIndex[lane] == index) return;
        _laneIndex[lane] = index;
        OnPropertyChanged(lane switch
        {
            0 => nameof(SelectedFogIndex),
            1 => nameof(SelectedBeamsIndex),
            _ => nameof(SelectedLasersIndex)
        });
    }

    private void LoadPosition(FractionalPosition position, bool seek = false)
    {
        _target = position;
        TargetPositionText = position.ToString();
        TargetMsText = FormatMs(position);
        if (seek) _edit.Editor.ExecuteSeek(position.GetPositionMs(_edit.Editor.Beats));

        var atTarget = Showlights.Where(s => s.Position == position).ToList();
        var lanes = LaneState(atTarget);
        for (var i = 0; i < Lanes.Length; i++)
            SetLane(i, lanes.TryGetValue(Lanes[i], out var type) ? Math.Max(0, IndexOfType(i, type)) : 0);
        HasShowlightsAtTarget = atTarget.Count > 0;

        // Mirrors the list selection onto the target WITHOUT seeking again.
        SetProperty(ref _existingIndex, _existing.ToList().FindIndex(p => p.At == position),
            nameof(SelectedExistingIndex));
    }

    private int IndexOfType(int lane, string type) =>
        _laneTypes[lane].ToList().FindIndex(t => t != null && t.Equals(type, StringComparison.OrdinalIgnoreCase));

    private void RefreshExistingPositions()
    {
        _existing =
        [
            .. Showlights.GroupBy(s => s.Position).OrderBy(g => g.Key)
                .Select(g => new Position(g.Key, g.Key.ToString(), Summarize(g.ToList())))
        ];
        OnPropertyChanged(nameof(ExistingPositions));
        OnPropertyChanged(nameof(HasExistingPositions));
    }

    private string Summarize(IReadOnlyList<Showlight> showlights)
    {
        var lanes = LaneState(showlights);
        var parts = Lanes.Where(lanes.ContainsKey)
            .Select(lane => $"{LaneLabel(lane)}: {ShowlightCatalog.GetDisplayName(lanes[lane])}")
            .ToList();
        var unknown = showlights
            .Where(s => !ShowlightCatalog.TryGetLane(s.Type, s.Note, out _))
            .Select(s => ShowlightCatalog.GetDisplayName(s.Type, s.Note))
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToList();
        if (unknown.Count > 0) parts.Add(string.Join(", ", unknown));
        return parts.Count > 0 ? string.Join(" · ", parts) : _edit.Localization["Dialog_Showlights_EmptyState"];
    }

    private static Dictionary<ShowlightLane, string> LaneState(IEnumerable<Showlight> showlights)
    {
        var state = new Dictionary<ShowlightLane, string>();
        foreach (var s in showlights.OrderBy(s => ShowlightCatalog.GetSortOrder(s.Type, s.Note)))
        {
            if (ShowlightCatalog.TryGetLane(s.Type, s.Note, out var lane))
                state[lane] = ShowlightCatalog.NormalizeType(s.Type, s.Note);
        }

        return state;
    }

    private List<Showlight> DesiredShowlights()
    {
        var desired = new List<Showlight>();
        for (var i = 0; i < Lanes.Length; i++)
        {
            var type = _laneIndex[i] >= 0 && _laneIndex[i] < _laneTypes[i].Count ? _laneTypes[i][_laneIndex[i]] : null;
            if (string.IsNullOrWhiteSpace(type)) continue;
            desired.Add(new Showlight { Position = _target, Type = type, Note = ShowlightCatalog.NormalizeNote(type) });
        }

        return desired;
    }

    private static bool Differ(IEnumerable<Showlight> existing, IEnumerable<Showlight> desired)
    {
        var have = LaneState(existing);
        var want = LaneState(desired);
        return have.Count != want.Count || want.Any(pair =>
            !have.TryGetValue(pair.Key, out var type) || !type.Equals(pair.Value, StringComparison.OrdinalIgnoreCase));
    }

    private IReadOnlyList<string> Options(ShowlightLane lane, IReadOnlyList<ShowlightDefinition> definitions)
    {
        _laneTypes[Array.IndexOf(Lanes, lane)] = [null, .. definitions.Select(d => d.Type)];
        return [_edit.Localization["Dialog_Showlights_None"], .. definitions.Select(d => d.DisplayName)];
    }

    private string LaneLabel(ShowlightLane lane) => lane switch
    {
        ShowlightLane.Fog => _edit.Localization["Dialog_Showlights_Fog"],
        ShowlightLane.Beams => _edit.Localization["Dialog_Showlights_Beams"],
        _ => _edit.Localization["Dialog_Showlights_Lasers"]
    };

    private string FormatMs(FractionalPosition position) => $"{position.GetPositionMs(_edit.Editor.Beats):0.###} ms";

    /// <summary>Beat and ms on one line, for the LOG, which has no columns.</summary>
    private string Describe(FractionalPosition position) => $"{position} ({FormatMs(position)})";

    private sealed record Position(FractionalPosition At, string PositionText, string Summary) : IShowlightPosition;
}
