using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Services.Editing;
using DotCharter.Tone.Infrastructure.Configuration;
using DotCharter.Tone.Logging;
using DotCharter.Services.State;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>
///     Edit ▸ History. One instance per open: the tree is read when it opens
///     and re-read on every history change while the host holds it.
/// </summary>
public sealed class HistoryViewModel : DialogViewModel<bool>, IHistoryScreen, IDisposable
{
    private readonly IHistoryService _history;
    private readonly ILocalizationService _localization;
    private readonly IAppLogger _logger;
    private IReadOnlyList<HistoryNode> _nodes = [];
    private string _summary = "";

    public HistoryViewModel(IHistoryService history, ILocalizationService localization, IAppLogger logger)
    {
        _history = history;
        _localization = localization;
        _logger = logger;
        JumpToCommand = new RelayCommand<IHistoryNode>(JumpTo);
        CloseCommand = new RelayCommand(() => Close(true));
        Rebuild();
        _history.HistoryChanged += OnHistoryChanged;
        logger.Info(AppLogCategory.History, $"History dialog opened. Nodes={_nodes.Count}.");
    }

    public IReadOnlyList<IHistoryNode> Nodes => _nodes;

    public bool HasNodes => _nodes.Count > 0;

    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    public ICommand JumpToCommand { get; }

    public ICommand CloseCommand { get; }

    public void Dispose() => _history.HistoryChanged -= OnHistoryChanged;

    private void OnHistoryChanged()
    {
        if (!IsClosed) Rebuild();
    }

    private void JumpTo(IHistoryNode? row)
    {
        if (row is not HistoryNode node || node.IsCurrent) return;
        // A jump walks MANY undo/redo steps and any one can throw; each step
        // raises HistoryChanged, which moves the current marker with it.
        HistoryStepGuard.Run(() => _history.NavigateTo(node.Id), $"Jump to history node {node.Id}", null, _logger, _localization);
    }

    private void Rebuild()
    {
        var tree = _history.GetTree();
        var byParent = tree
            .Where(n => n.ParentId != null)
            .GroupBy(n => n.ParentId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderBy(n => n.Id).ToList());

        var rows = new List<HistoryNode>();
        var root = tree.FirstOrDefault(n => n.ParentId == null);
        if (root != null)
        {
            // Depth-first, so a branch's states group under their fork. Indent
            // grows at BRANCH POINTS only: the lane child (on the current path,
            // else the oldest) stays at its parent's depth, so a linear session
            // is a flat list rather than a staircase.
            var stack = new Stack<(HistoryNodeSnapshot Node, int Depth)>();
            stack.Push((root, 0));
            while (stack.Count > 0)
            {
                var (node, depth) = stack.Pop();
                rows.Add(new HistoryNode(node.Id,
                    string.IsNullOrEmpty(node.Description)
                        ? _localization["History_InitialState"]
                        : HistoryActionText.Describe(node.Description, key => _localization[key]),
                    node.TimestampUtc.ToLocalTime().ToString("HH:mm:ss"),
                    depth * 16, node.IsCurrent, !node.IsOnCurrentPath));

                if (!byParent.TryGetValue(node.Id, out var children)) continue;
                var lane = children.FirstOrDefault(c => c.IsOnCurrentPath) ?? children[0];
                for (var i = children.Count - 1; i >= 0; i--)
                    stack.Push((children[i], children[i] == lane ? depth : depth + 1));
            }
        }

        _nodes = rows;
        OnPropertyChanged(nameof(Nodes));
        OnPropertyChanged(nameof(HasNodes));
        Summary = DescribeTree(rows);
    }

    /// <summary>Both halves are counted strings, so both go through the two-form chooser.</summary>
    private string DescribeTree(IReadOnlyList<HistoryNode> rows)
    {
        var branches = rows.Count(n => n.IsBranch);
        var states = _localization.Plural("History_Summary_StatesOne", "History_Summary_StatesFmt", rows.Count);
        var branchText = branches == 0
            ? _localization["History_Summary_NoBranches"]
            : _localization.Plural("History_Summary_BranchesOne", "History_Summary_BranchesFmt", branches);
        return $"{states} · {branchText}";
    }

    internal sealed record HistoryNode(int Id, string Description, string Time, double Indent, bool IsCurrent,
        bool IsBranch) : IHistoryNode;
}
