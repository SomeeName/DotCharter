using Avalonia.Input;
using DotCharter.UI.Input;
using DotCharter.UI.Input.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.UI.Input;

/// <summary>
///     P2-4: the four editor tools carry mutable per-gesture state (drag
///     targets, marquee origin, pending context menu, double-click latches),
///     and as SINGLETONS one instance of each was shared by every
///     <c>ToolCoordinator</c> — the main timeline and each stacked row traded
///     drag/marquee state through the same objects. They are TRANSIENT now,
///     resolved fresh per manager, and the manager disposes them — which makes
///     <c>HighwayInteractionTool</c>'s ctor subscription to
///     <c>HistoryChanged</c> safe.
///     <para>
///         <b>That last sentence used to end "instead of a leak per pane
///         float/redock", and it was HALF TRUE</b> (found 2026-08-29). The
///         SUBSCRIPTION was released; the OBJECT was not. A container tracks
///         every disposable it constructs so it can dispose it, and for a
///         transient resolved from the ROOT provider that list is the root's —
///         process lifetime — and disposing the instance does not untrack it.
///         The factory resolved from the root and a manager is built per
///         stacked-row attach, so every tool of every row of every arrangement
///         ever stacked stayed pinned, each holding its renderer and coordinator.
///         The factory takes a per-manager <c>IServiceScope</c> now and the
///         manager drops it in <c>Dispose</c>.
///     </para>
/// </summary>
public sealed class EditorToolLifetimeTests
{
    /// <summary>Counts subscriptions so the unsubscription is OBSERVABLE — a
    /// field-like event hides its handler list.</summary>
    private sealed class CountingHistoryService : IHistoryService
    {
        public int HistoryChangedHandlers;

        public event Action? HistoryChanged
        {
            add => HistoryChangedHandlers++;
            remove => HistoryChangedHandlers--;
        }

        public event Action<Arrangement?>? FocusRequested { add { } remove { } }
        public event Action? ActionFailed { add { } remove { } }
        public event Action<IReadOnlyList<LinkPropagationReport>>? LinksPropagated { add { } remove { } }
        public bool OnlyHere(LinkPropagationReport report) => false;
        public bool CanUndo => false;
        public bool CanRedo => false;
        public int CurrentNodeId => 0;
        public void Clear() { }
        public bool Do(IChartAction action) => false;
        public bool PushAction(IChartAction action) => false;
        public void Undo() { }
        public void Redo() { }
        public void EndGesture() { }
        public IReadOnlyList<HistoryNodeSnapshot> GetTree() => [];
        public bool NavigateTo(int nodeId) => false;
        public IReadOnlyList<HistoryStepInfo> GetUndoSteps(int max = 64) => [];
        public IReadOnlyList<HistoryStepInfo> GetRedoSteps(int max = 64) => [];
    }

    [Fact]
    public void DisposingTheHighwayTool_UnsubscribesFromHistory()
    {
        var history = new CountingHistoryService();
        var tool = new HighwayInteractionTool(null!, history, new FakeAppLogger());
        Assert.Equal(1, history.HistoryChangedHandlers);

        tool.Dispose();
        Assert.Equal(0, history.HistoryChangedHandlers);

        // Idempotent — the manager may dispose defensively.
        tool.Dispose();
        Assert.Equal(0, history.HistoryChangedHandlers);
    }

    [Fact]
    public void TheTools_AreRegisteredTransient_NeverSingleton()
    {
        var source = Read("DotCharter.UI/App.StateServices.cs");
        Assert.DoesNotContain("AddSingleton<IEditorTool", source);
        foreach (var tool in new[]
                 {
                     "EventInteractionTool", "HandShapeInteractionTool",
                     "FretAnchorInteractionTool", "HighwayInteractionTool"
                 })
        {
            Assert.Contains($"AddTransient<IEditorTool, {tool}>", source);
        }
    }

    [Fact]
    public void TheFactory_ResolvesAFreshToolSetPerManager()
    {
        // An injected IEnumerable<IEditorTool> is materialized ONCE at factory
        // construction, so transient registration alone changes nothing — the
        // factory must ask the provider per CreateManager call.
        var source = Read("DotCharter.UI/Input/TimelineInteractionFactory.cs");
        var create = source.IndexOf("public TimelineInteractionManager CreateManager", StringComparison.Ordinal);
        Assert.True(create >= 0, "CreateManager has moved — repoint this lint.");
        Assert.Contains("CreateToolScope()", source[create..]);
        Assert.Contains("GetServices<IEditorTool>()", source);
        Assert.DoesNotContain("IEnumerable<IEditorTool> _tools", source);
    }

    /// <summary>A tool set resolved from a SCOPE is released when the scope is
    /// dropped. One resolved from the ROOT provider is not — the container holds
    /// it for the life of the process, which is exactly what the factory used to
    /// do. The rooted half is the CONTROL: without it this could pass against a
    /// runtime that never rooted anything, and would be asserting nothing.</summary>
    [Fact]
    public void AToolSetFromTheFactoryScope_IsReleasedWhenTheManagerIsDisposed()
    {
        var services = new ServiceCollection();
        var history = new CountingHistoryService();
        services.AddTransient<IEditorTool, ProbeTool>();
        using var provider = services.BuildServiceProvider();
        // Only _services is exercised here; the rest of the factory's graph is
        // not reachable from CreateToolScope.
        var factory = new TimelineInteractionFactory(provider, null!, null!, history, null!, null!);

        var scoped = ResolveThroughDisposedScopes(factory, 100);
        var rooted = ResolveFromRoot(provider);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.True(rooted.IsAlive,
            "a transient IDisposable resolved from the ROOT provider was collected — the control for " +
            "this test no longer holds, so the scoped half below proves nothing.");
        Assert.All(scoped, probe => Assert.False(probe.IsAlive,
            "an editor tool survived its manager's disposal — the container is still tracking it, so " +
            "every stacked-row attach leaks one tool per tool type for the life of the process."));
    }

    /// <summary>Kept out of the caller's frame so no stack slot holds the tools
    /// alive past the disposal.</summary>
    private static List<WeakReference> ResolveThroughDisposedScopes(
        TimelineInteractionFactory factory, int managers)
    {
        var probes = new List<WeakReference>(managers);
        for (var i = 0; i < managers; i++)
        {
            var (tools, scope) = factory.CreateToolScope();
            foreach (var tool in tools) probes.Add(new WeakReference(tool));
            // Exactly what TimelineInteractionManager.Dispose does.
            foreach (var tool in tools) (tool as IDisposable)?.Dispose();
            scope.Dispose();
        }

        return probes;
    }

    private static WeakReference ResolveFromRoot(IServiceProvider provider) =>
        new(provider.GetServices<IEditorTool>().First());

    /// <summary>Disposable, like HighwayInteractionTool, and with no
    /// dependencies so the LIFETIME is the only thing under test.</summary>
    private sealed class ProbeTool : IEditorTool, IDisposable
    {
        public void Dispose() { }
        public void OnPointerPressed(PointerPressedEventArgs e, ToolContext context) { }
        public void OnPointerMoved(PointerEventArgs e, ToolContext context) { }
        public void OnPointerReleased(PointerReleasedEventArgs e, ToolContext context) { }
        public void OnPointerExited(PointerEventArgs e, ToolContext context) { }
    }

    [Fact]
    public void TheManager_DisposesItsToolSet()
    {
        var source = Read("DotCharter.UI/Input/TimelineInteractionManager.cs");
        var dispose = source.IndexOf("public void Dispose()", StringComparison.Ordinal);
        Assert.True(dispose >= 0);
        Assert.Contains("(tool as IDisposable)?.Dispose()", source[dispose..]);
    }

    private static string Read(string relative) =>
        UiSourceTree.ReadAllText(Path.Combine(TestPaths.RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar)));
}
