using System.ComponentModel;
using DotCharter.Services.State;

namespace DotCharter.Services.Editing.Tracks;

/// <summary>
/// Default <see cref="ITrackContextRegistry"/>. Lazily creates one
/// <see cref="TrackEditorContext"/> per arrangement and follows
/// <see cref="ProjectState.ActiveArrangement"/> as the focused track until
/// later phases let the user focus stacked rows independently.
/// The focused context's <see cref="TrackEditorContext.Level"/> mirrors
/// <see cref="ProjectState.ActiveLevel"/> so existing tools see the same
/// data they read directly from <see cref="ProjectState"/>.
/// </summary>
public sealed class TrackContextRegistry : ITrackContextRegistry, IDisposable
{
    private readonly Dictionary<Arrangement, TrackEditorContext> _contexts = [];
    private readonly List<TrackEditorContext> _ordered = [];
    private readonly ProjectState _projectState;
    private readonly ISelectionService _selection;

    // The (arrangement, level) pair the CONTENT of the selection belongs to.
    // See DropSelectionOrphanedByTheTrackSwitch.
    private Arrangement? _selectionArrangement;
    private ChartLevel? _selectionLevel;

    public TrackContextRegistry(ProjectState projectState, ISelectionService selection)
    {
        _projectState = projectState;
        _selection = selection;
        _projectState.PropertyChanged += OnProjectStatePropertyChanged;
        SyncWithActiveArrangement();
        _selectionArrangement = _projectState.ActiveArrangement;
        _selectionLevel = _projectState.ActiveLevel;
    }

    public TrackEditorContext? FocusedTrack { get; private set; }

    public IReadOnlyList<TrackEditorContext> Tracks => _ordered;

    public event Action? FocusedTrackChanged;

    public TrackEditorContext GetOrCreate(Arrangement arrangement)
    {
        if (_contexts.TryGetValue(arrangement, out var existing)) return existing;

        var ctx = new TrackEditorContext(arrangement, _selection)
        {
            Project = _projectState.ActiveProject
        };
        _contexts[arrangement] = ctx;
        _ordered.Add(ctx);
        return ctx;
    }

    public TrackEditorContext? Get(Arrangement arrangement) =>
        _contexts.TryGetValue(arrangement, out var ctx) ? ctx : null;

    public void Focus(Arrangement arrangement)
    {
        var ctx = GetOrCreate(arrangement);
        if (ReferenceEquals(FocusedTrack, ctx)) return;
        FocusedTrack = ctx;
        SyncFocusedLevel();
        FocusedTrackChanged?.Invoke();
    }

    public void Dispose() => _projectState.PropertyChanged -= OnProjectStatePropertyChanged;

    private void OnProjectStatePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ProjectState.ActiveArrangement):
            case nameof(ProjectState.ActiveProject):
                DropContextsOutsideTheOpenProject();
                SyncProjectOnAllContexts();
                SyncWithActiveArrangement();
                DropSelectionOrphanedByTheTrackSwitch();
                break;
            case nameof(ProjectState.ActiveLevel):
                SyncFocusedLevel();
                DropSelectionOrphanedByTheTrackSwitch();
                break;
        }
    }

    /// <summary>
    ///     A content selection belongs to ONE (arrangement, level) pair; switching
    ///     either one leaves it pointing at sounds the editor is not showing,
    ///     so it is dropped here.
    ///     <para>
    ///         <see cref="TrackEditorContext.Selection" /> is the app-wide
    ///         <see cref="ISelectionService" /> singleton — every track shares it —
    ///         and a PROJECT swap
    ///         (<c>ChartEditorViewModel.ApplyProject</c>), a snapshot restore and a
    ///         vocal-phrase replacement clear it too. An arrangement switch (the
    ///         Tracks picker, <c>CycleArrangement</c>) and a LEVEL switch (the
    ///         difficulty picker, and the one <c>ProjectState.ActiveArrangement</c>
    ///         performs on its own by adopting the hardest level) clear it for the same reason, because the
    ///         survivors are not inert: <c>InspectorViewModel.ApplyNoteMutation</c>
    ///         builds a real <c>MutateNotesAction</c> over them, so every field
    ///         write from the Sound tab edited the arrangement the charter had just
    ///         navigated AWAY from while the new one was on screen; Delete pushed a
    ///         node that removed nothing and whose undo INSERTED the foreign sounds
    ///         into the level now open; and <c>ChartRenderer</c> read them into the
    ///         frame snapshot. Exactly the fault <c>SelectionService.ClearSelections</c>
    ///         documents for the project swap, one scope in.
    ///     </para>
    ///     <para>
    ///         Compared by INSTANCE, never by the notification: the arrangement
    ///         settings dialog re-raises <c>ActiveArrangement</c> without changing it
    ///         (<c>ProjectState.NotifyActiveArrangementChanged</c>), and a rename must
    ///         not cost the charter their selection. For the same reason an issue jump
    ///         is safe — <c>InspectorIssuesViewModel.JumpToIssue</c> switches the level
    ///         FIRST and selects afterwards.
    ///     </para>
    /// </summary>
    private void DropSelectionOrphanedByTheTrackSwitch()
    {
        var arrangement = _projectState.ActiveArrangement;
        var level = _projectState.ActiveLevel;
        if (ReferenceEquals(arrangement, _selectionArrangement) && ReferenceEquals(level, _selectionLevel)) return;

        _selectionArrangement = arrangement;
        _selectionLevel = level;
        _selection.ClearAll();
    }

    /// <summary>
    ///     Forget every context whose arrangement does not belong to the project that is
    ///     now open.
    ///     <para>
    ///         This registry is an app-wide SINGLETON and its dictionary is keyed by the
    ///         <see cref="Arrangement" /> INSTANCE, so without this every project the
    ///         session ever opened stayed reachable — arrangements, levels, notes, chords,
    ///         hand shapes, anchors, chord templates, event points — for the life of the
    ///         process. <see cref="SyncProjectOnAllContexts" /> hid it: it walked the dead
    ///         contexts on every switch and repointed their <c>Project</c> at the incoming
    ///         one, so they went on looking live while <c>_ordered</c> grew linearly with
    ///         opens. Pinned by <c>UI/TrackContextRegistryProjectSwitchTests</c>.
    ///     </para>
    ///     <para>
    ///         A null <see cref="ProjectState.ActiveProject" /> prunes nothing: there is no
    ///         project to judge membership against, and several call sites (tests, the
    ///         pre-project shell) drive arrangements with no project set.
    ///     </para>
    /// </summary>
    private void DropContextsOutsideTheOpenProject()
    {
        var project = _projectState.ActiveProject;
        if (project == null || _ordered.Count == 0) return;

        // Arrangement declares no Equals/GetHashCode, so this is reference identity —
        // the same comparison _contexts already keys on.
        var open = new HashSet<Arrangement>(project.Arrangements);
        if (_ordered.TrueForAll(ctx => open.Contains(ctx.Arrangement))) return;

        foreach (var ctx in _ordered)
        {
            if (!open.Contains(ctx.Arrangement))
                _contexts.Remove(ctx.Arrangement);
        }

        _ordered.RemoveAll(ctx => !open.Contains(ctx.Arrangement));

        if (FocusedTrack != null && !open.Contains(FocusedTrack.Arrangement))
        {
            // The focused track belonged to the project that just closed. Null it and
            // say so: the ActiveArrangement change that follows re-focuses into the new
            // project, but a project with NO arrangements never sends one, and a stale
            // FocusedTrack there would hand every tool the closed project's graph.
            FocusedTrack = null;
            FocusedTrackChanged?.Invoke();
        }
    }

    private void SyncProjectOnAllContexts()
    {
        var project = _projectState.ActiveProject;
        foreach (var ctx in _ordered) ctx.Project = project;
    }

    private void SyncWithActiveArrangement()
    {
        var active = _projectState.ActiveArrangement;
        if (active == null) return;
        // ActiveProject is published BEFORE ActiveArrangement (ChartEditorViewModel
        // .ApplyProject), so on the ActiveProject notification `active` is still the
        // CLOSED project's arrangement. Focusing it here would re-create the context
        // DropContextsOutsideTheOpenProject just dropped and reinstate the leak.
        var project = _projectState.ActiveProject;
        if (project != null && !project.Arrangements.Contains(active)) return;
        Focus(active);
    }

    private void SyncFocusedLevel()
    {
        if (FocusedTrack == null) return;
        if (FocusedTrack.Arrangement != _projectState.ActiveArrangement) return;
        FocusedTrack.Level = _projectState.ActiveLevel ?? FocusedTrack.Arrangement.Levels.FirstOrDefault();
    }
}
