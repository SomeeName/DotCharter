using DotCharter.UI.Controls;
using Microsoft.Extensions.DependencyInjection;
using DotCharter.UI.Input.Tools;
using DotCharter.UI.ViewModels.Shell;

namespace DotCharter.UI.Input;

/// <summary>
/// DI seam for the timeline input glue that views would otherwise assemble by
/// resolving services from the static <c>App.Services</c> container.
/// Views can't take constructor injection (they're created from XAML), so
/// they reach this factory through <see cref="MainWindowViewModel.InteractionFactory"/>
/// instead of the service locator.
/// </summary>
public interface ITimelineInteractionFactory
{
    /// <summary>The shared history service, exposed for redraw-on-undo subscriptions.</summary>
    IHistoryService History { get; }

    /// <summary>
    /// Creates an interaction manager for a chart renderer. When
    /// <paramref name="boundArrangement"/> is given (stacked multi-track
    /// rows), the per-arrangement track context is pre-created so it is
    /// wired before the user clicks.
    /// </summary>
    TimelineInteractionManager CreateManager(
        MainWindowViewModel vm, ChartRenderer renderer, Arrangement? boundArrangement = null);

    TimelineWheelAdjuster CreateWheelAdjuster();
}

public sealed class TimelineInteractionFactory : ITimelineInteractionFactory
{
    private readonly IServiceProvider _services;
    private readonly ITrackContextRegistry _trackContexts;
    private readonly IHighwayEditingService _highwayEditing;
    private readonly IConfigurationService _configService;
    private readonly IFingeringGenerationService _fingeringService;

    public TimelineInteractionFactory(
        IServiceProvider services,
        ITrackContextRegistry trackContexts,
        IHighwayEditingService highwayEditing,
        IHistoryService history,
        IConfigurationService configService,
        IFingeringGenerationService fingeringService)
    {
        _services = services;
        _trackContexts = trackContexts;
        _highwayEditing = highwayEditing;
        History = history;
        _configService = configService;
        _fingeringService = fingeringService;
    }

    public IHistoryService History { get; }

    public TimelineInteractionManager CreateManager(
        MainWindowViewModel vm, ChartRenderer renderer, Arrangement? boundArrangement = null)
    {
        if (boundArrangement != null) _trackContexts.GetOrCreate(boundArrangement);
        var (tools, scope) = CreateToolScope();
        return new TimelineInteractionManager(
            vm, renderer, _trackContexts, tools, History, boundArrangement, scope);
    }

    /// <summary>
    ///     A FRESH tool set per manager, in its OWN container scope, handed back
    ///     with the scope that owns it.
    ///     <para>
    ///         The tools are TRANSIENT (0.59) because they carry per-gesture state
    ///         — one shared set meant every coordinator, main timeline plus each
    ///         stacked row, traded drag/marquee state through the same instances.
    ///         An injected <c>IEnumerable&lt;IEditorTool&gt;</c> would be
    ///         materialized ONCE at factory construction and shared again, which
    ///         is why this resolves per call.
    ///     </para>
    ///     <para>
    ///         <b>But resolving them from the ROOT provider leaked them.</b> A
    ///         container TRACKS every disposable it constructs so it can dispose
    ///         it, and for a transient resolved from the root that tracking list
    ///         is the root's — i.e. process lifetime.
    ///         <c>HighwayInteractionTool</c> is <c>IDisposable</c> and a manager
    ///         is built per stacked-row attach (<c>StackedTracksView</c>), so
    ///         every row of every arrangement ever stacked pinned a tool — and
    ///         the manager's own <c>Dispose</c> could not help, because disposing
    ///         a tracked instance does not UNTRACK it. A SCOPE has its own list
    ///         and drops it when disposed, which is what the manager now does.
    ///     </para>
    /// </summary>
    internal (IReadOnlyList<IEditorTool> Tools, IServiceScope Scope) CreateToolScope()
    {
        var scope = _services.CreateScope();
        return (scope.ServiceProvider.GetServices<IEditorTool>().ToList(), scope);
    }

    public TimelineWheelAdjuster CreateWheelAdjuster() =>
        new(_highwayEditing, History, _configService, _fingeringService);
}
