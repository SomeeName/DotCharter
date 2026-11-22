using DotCharter.Services.Editing.MultiTrack;
using DotCharter.Services.State;

namespace DotCharter.Services.Session;

/// <summary>
///     Remembers, per project, what the owner had on screen (active arrangement,
///     ghost and stacked selections, playhead) and puts it back when the project
///     opens. Stored in <see cref="AppConfig.ProjectViews" /> keyed by the
///     project path, so there is no change to the project file format.
/// </summary>
/// <remarks>
///     Capture is armed by <see cref="Restore" /> for ONE project instance and
///     only writes while <see cref="ProjectState.ActiveProject" /> is that same
///     instance: opening another project swaps the instance first, so the clears
///     and the "first arrangement" assignment that follow cannot overwrite the
///     saved entry before its restore ran. Writes go through
///     <see cref="IConfigurationService.Update" />, whose file write is coalesced.
/// </remarks>
public sealed class ProjectViewMemory
{
    internal const int MaxEntries = 200;
    private const double PlayheadWriteStepMs = 1000;

    private readonly IConfigurationService _config;
    private readonly ProjectState _state;
    private readonly IGhostArrangementsService _ghosts;
    private readonly IStackedArrangementsService _stacks;
    private readonly Func<DateTime> _utcNow;

    private ChartProject? _armedProject;
    private string _armedKey = "";
    private double _lastWrittenPlayheadMs = double.NaN;

    public ProjectViewMemory(IConfigurationService config, ProjectState state,
        IGhostArrangementsService ghosts, IStackedArrangementsService stacks, Func<DateTime>? utcNow = null)
    {
        _config = config;
        _state = state;
        _ghosts = ghosts;
        _stacks = stacks;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _state.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ProjectState.ActiveArrangement)) Capture();
            else if (e.PropertyName == nameof(ProjectState.CurrentTimeMs)) CapturePlayhead();
        };
        _ghosts.Changed += Capture;
        _stacks.Changed += Capture;
    }

    /// <summary>The saved playhead for <paramref name="path" />, or null when there is none.</summary>
    public double? Restore(ChartProject project, string path)
    {
        _armedProject = null;
        var key = KeyFor(path);
        if (key.Length == 0) return null;

        double? playhead = null;
        if (_config.LoadAppConfig().ProjectViews.TryGetValue(key, out var saved))
        {
            var arrangements = project.Arrangements;
            var active = Resolve(arrangements, saved.ActiveArrangementName, saved.ActiveArrangementIndex);
            if (active != null) _state.ActiveArrangement = active;

            _ghosts.Clear();
            foreach (var r in saved.VisibleGhosts)
            {
                if (Resolve(arrangements, r.Name, r.Index) is { } g && !ReferenceEquals(g, _state.ActiveArrangement))
                    _ghosts.SetVisible(g, true);
            }

            _stacks.Clear();
            foreach (var r in saved.StackedArrangements)
            {
                if (Resolve(arrangements, r.Name, r.Index) is { } s && !ReferenceEquals(s, _state.ActiveArrangement))
                    _stacks.SetStacked(s, true);
            }


            playhead = saved.PlayheadMs > 0 ? saved.PlayheadMs : null;
            _lastWrittenPlayheadMs = saved.PlayheadMs;
        }
        else
        {
            _lastWrittenPlayheadMs = double.NaN;
        }

        _armedProject = project;
        _armedKey = key;
        return playhead;
    }

    private static Arrangement? Resolve(IList<Arrangement> arrangements, string name, int index)
    {
        if (name.Length == 0) return null;
        if (index >= 0 && index < arrangements.Count && arrangements[index].Name == name) return arrangements[index];
        return arrangements.FirstOrDefault(a => a.Name == name);
    }

    private bool IsArmed => _armedProject != null && ReferenceEquals(_state.ActiveProject, _armedProject)
                            && KeyFor(_state.CurrentProjectPath) == _armedKey;

    private void CapturePlayhead()
    {
        if (!IsArmed) return;
        var t = _state.CurrentTimeMs;
        if (!double.IsNaN(_lastWrittenPlayheadMs) && Math.Abs(t - _lastWrittenPlayheadMs) < PlayheadWriteStepMs) return;
        Capture();
    }

    private void Capture()
    {
        if (!IsArmed) return;
        var project = _armedProject!;
        var list = project.Arrangements;
        ArrangementRef? Ref(Arrangement? a) => a == null ? null : new ArrangementRef { Name = a.Name, Index = list.IndexOf(a) };
        var active = _state.ActiveArrangement;
        var playhead = Math.Max(0, _state.CurrentTimeMs);
        _lastWrittenPlayheadMs = playhead;
        var ghosts = _ghosts.VisibleGhosts.Select(Ref).OfType<ArrangementRef>().ToList();
        var stacks = _stacks.StackedArrangements.Select(Ref).OfType<ArrangementRef>().ToList();
        var now = _utcNow().Ticks;
        _config.Update(c =>
        {
            c.ProjectViews ??= [];
            c.ProjectViews[_armedKey] = new ProjectViewState
            {
                ActiveArrangementName = active?.Name ?? "",
                ActiveArrangementIndex = active == null ? -1 : list.IndexOf(active),
                VisibleGhosts = ghosts,
                StackedArrangements = stacks,
                PlayheadMs = playhead,
                LastUsedTicks = now,
            };
            if (c.ProjectViews.Count <= MaxEntries) return;
            foreach (var stale in c.ProjectViews.OrderBy(p => p.Value.LastUsedTicks)
                         .Take(c.ProjectViews.Count - MaxEntries).Select(p => p.Key).ToList())
            {
                c.ProjectViews.Remove(stale);
            }
        });
    }

    internal static string KeyFor(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try
        {
            return Path.GetFullPath(path).ToUpperInvariant();
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path.ToUpperInvariant();
        }
    }
}
