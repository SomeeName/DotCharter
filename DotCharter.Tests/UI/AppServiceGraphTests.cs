using DotCharter.UI;
using DotCharter.UI.ViewModels.Shell;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.UI;

/// <summary>
///     Nothing built the DI container. Every missing registration, every
///     constructor parameter that lost its service, every accidental captive
///     dependency surfaced only at LAUNCH — and only on the code path that
///     happened to resolve the broken node.
///     <para>
///         <c>App.ConfigureServices</c> is static and internal precisely so this
///         can build the real graph. <c>ValidateOnBuild</c> constructs a call
///         site for every registration without instantiating anything, so this
///         needs no Avalonia app, no window, no GPU and no audio device.
///     </para>
/// </summary>
public sealed class AppServiceGraphTests
{
    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        App.ConfigureServices(services, new JsonConfigurationService());
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
    }

    [Fact]
    public void The_whole_graph_resolves()
    {
        // Throws an AggregateException naming every unresolvable registration.
        using var provider = BuildProvider();

        Assert.NotNull(provider);
    }

    [Fact]
    public void The_shell_view_model_is_registered()
    {
        var services = new ServiceCollection();
        App.ConfigureServices(services, new JsonConfigurationService());

        Assert.Contains(services, d => d.ServiceType == typeof(MainWindowViewModel));
    }

    /// <summary>
    ///     A transient <see cref="IDisposable" /> resolved from the ROOT scope is
    ///     tracked by the container for the life of the process and can never be
    ///     collected, so the transient SET has to stay a decision rather than an
    ///     accident.
    ///     <para>
    ///         <b>This test did not test its own name until 2026-08-29.</b> It
    ///         said "exactly one transient exists today and it is not
    ///         IDisposable; this pins that", and then asserted only
    ///         <c>Assert.All(transients, not IDisposable)</c> — which a second,
    ///         tenth or hundredth transient passes, and which an EMPTY set passes
    ///         too (<c>Assert.All</c> over nothing). Both halves of the comment
    ///         were false when checked: there are SEVEN transient descriptors, and
    ///         one of them is disposable. It never bit because the projection was
    ///         <c>d.ServiceType</c> — the four editor tools register as
    ///         <c>IEditorTool</c>, which is not <see cref="IDisposable" /> however
    ///         disposable the implementation behind it is.
    ///     </para>
    ///     <para>
    ///         The disposable transient it found — <c>HighwayInteractionTool</c>,
    ///         recorded here the same day as a KNOWN DEFECT — is FIXED:
    ///         <c>TimelineInteractionFactory.CreateToolScope</c> resolves the tool
    ///         set inside an <c>IServiceScope</c> and
    ///         <c>TimelineInteractionManager.Dispose</c> drops it, so the root no
    ///         longer tracks anything. The carve-out below is therefore an
    ///         assertion about who OWNS a scope, not a tolerated leak, and
    ///         <c>UI/Input/EditorToolLifetimeTests</c> proves the release with a
    ///         <c>WeakReference</c> over 100 create/dispose cycles.
    ///     </para>
    /// </summary>
    [Fact]
    public void Nothing_but_the_diagnostics_view_model_is_transient()
    {
        var services = new ServiceCollection();
        App.ConfigureServices(services, new JsonConfigurationService());

        var transients = services
            .Where(d => d.Lifetime == ServiceLifetime.Transient)
            // Keyed descriptors keep their implementation on a DIFFERENT pair of
            // properties, and reading the unkeyed ones just yields null — which
            // is how the two keyed IChartSerializer transients were about to be
            // pinned under their INTERFACE name.
            .Select(d => d.IsKeyedService
                ? d.KeyedImplementationType ?? d.KeyedImplementationInstance?.GetType() ?? d.ServiceType
                : d.ImplementationType ?? d.ImplementationInstance?.GetType() ?? d.ServiceType)
            .ToArray();

        // The count the test is named for, pinned by NAME so the failure says
        // which one arrived. EIGHT, not the one the old comment claimed: four
        // editor tools (an IEnumerable contract — the timeline resolves a FRESH
        // tool set per manager because they carry per-gesture state, see
        // TimelineInteractionFactory.CreateManager), the two KEYED chart
        // serializers, the diagnostics view model, and the rig footer's
        // guitar-jack picker (one per rig SCREEN — Play Mode and the chart
        // editor each build their own AmpFxRigViewModel; not IDisposable, so
        // the root tracks nothing).
        Assert.Equal(
            [
                "DiagnosticsViewModel", "EventInteractionTool", "FretAnchorInteractionTool",
                "HandShapeInteractionTool", "HighwayInteractionTool", "LiveInputChannelViewModel",
                "RscpChartSerializer", "XmlChartSerializer"
            ],
            transients.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());

        // Transient disposables that are ONLY ever resolved inside a scope their
        // OWNER disposes, so the root never tracks them. Each entry names the
        // owner. This is not a tolerated-leak list: do not add a name without a
        // test that the scope is really dropped — the WeakReference kind, since
        // disposing a tracked instance does NOT untrack it and a Dispose() alone
        // proves nothing.
        string[] scopeOwnedTransients =
        [
            // TimelineInteractionFactory.CreateToolScope →
            // TimelineInteractionManager.Dispose, pinned by
            // UI/Input/EditorToolLifetimeTests.
            "HighwayInteractionTool"
        ];

        foreach (var t in transients)
        {
            if (!typeof(IDisposable).IsAssignableFrom(t)) continue;

            Assert.True(scopeOwnedTransients.Contains(t.Name),
                $"{t.Name} is a transient IDisposable — the root scope tracks it forever and never " +
                "releases it. Resolve it from a scope its owner disposes, or drop IDisposable.");
        }
    }

    /// <summary>
    ///     A singleton that implements <see cref="IAsyncDisposable" /> and NOT
    ///     <see cref="IDisposable" /> POISONS the whole container teardown.
    ///     <c>ServiceProvider.Dispose()</c> walks its disposables in REVERSE
    ///     creation order and THROWS
    ///     <c>InvalidOperationException("… implements IAsyncDisposable. Use
    ///     DisposeAsync to dispose the container.")</c> the moment it reaches
    ///     one — so everything created BEFORE it silently never gets disposed,
    ///     and <c>App.RunCleanShutdown</c>'s per-step guard swallows the throw
    ///     so nothing says so.
    ///     <para>
    ///         <c>MultiplayerViewModel</c> shipped exactly that shape: registered
    ///         as a singleton with only a <c>DisposeAsync</c> (which had zero
    ///         callers), created early, and it took the SQLite psarc index's WAL,
    ///         the LAN sockets, the stem separators' GPU handles,
    ///         <c>StatusBarViewModel</c>, <c>MainWindowViewModel</c>,
    ///         <c>ProjectSession</c> and <c>Fretboard3DEngine</c> down with it.
    ///         <c>CleanShutdownSequenceTests.AnAsyncOnlyDisposableSingleton_…</c>
    ///         pins the framework behaviour this lint exists for.
    ///     </para>
    ///     <para>
    ///         Blind spot, stated: a factory registration whose SERVICE type is an
    ///         interface hides its implementation type from reflection, so this
    ///         sees the contract instead. It catches every registration whose
    ///         concrete type is knowable — which is every view model, and those
    ///         are the ones that acquire an <c>await</c>-shaped teardown.
    ///     </para>
    /// </summary>
    [Fact]
    public void No_registered_type_is_async_disposable_only()
    {
        var services = new ServiceCollection();
        App.ConfigureServices(services, new JsonConfigurationService());

        var offenders = services
            .Select(d => d.ImplementationType ?? d.ImplementationInstance?.GetType() ?? d.ServiceType)
            .Distinct()
            .Where(t => typeof(IAsyncDisposable).IsAssignableFrom(t) && !typeof(IDisposable).IsAssignableFrom(t))
            .Select(t => t.FullName ?? t.Name)
            .ToArray();

        Assert.True(offenders.Length == 0,
            "These registered types implement IAsyncDisposable without IDisposable, so " +
            "ServiceProvider.Dispose() throws at them and TRUNCATES the disposal chain: " +
            string.Join(", ", offenders));
    }

    [Fact]
    public void Every_service_type_is_registered_exactly_once_unless_it_is_a_collection()
    {
        // A duplicate single-instance registration silently wins by LAST-one,
        // which is how a second DpapiOnlineTokenStore or a stray re-registration
        // hides. IEnumerable-injected contracts (the arrangement rules, the
        // power tools) are deliberately multi-registered — those are allowed.
        var services = new ServiceCollection();
        App.ConfigureServices(services, new JsonConfigurationService());

        // Grouped by type AND key: two KEYED registrations under different keys
        // (IChartSerializer "rscp" / "xml") are resolved by key and never compete.
        var duplicates = services
            .GroupBy(d => (d.ServiceType, d.ServiceKey))
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key.ServiceType.FullName}{(g.Key.ServiceKey is { } k ? $" [{k}]" : "")} x{g.Count()}")
            .Order(StringComparer.Ordinal)
            .ToArray();

        // Multi-registration is legal; the point is that the set is KNOWN.
        // If this list changes, decide whether the new duplicate is an
        // IEnumerable contract or an accident — and only then edit the list.
        // (Until 2026-09-25 this asserted that each "Name xN" string contained
        // an "x", which every one does, so it passed for ANY duplicate.)
        Assert.True(KnownMultiRegistrations.SequenceEqual(duplicates, StringComparer.Ordinal),
            "Multi-registered service types changed. Expected: " +
            string.Join(" | ", KnownMultiRegistrations) + " — Actual: " +
            string.Join(" | ", duplicates));
    }

    // Each is injected ONLY as a collection (IEnumerable / IReadOnlyList /
    // GetServices), checked 2026-09-25. The counts are part of the contract: a
    // new settings tab, rule, tool or library package source (#886) bumps one on purpose.
    // The library has a second package source only when the optional plugin is in the
    // build (#889); a public build registers the .dcsong source alone.
    private static readonly string[] KnownMultiRegistrations =
    [
        "DotCharter.Presentation.Screens.Settings.ISettingsChangeSource x13",
        "DotCharter.Presentation.Screens.Settings.ISettingsTab x13",
        "DotCharter.Services.Editing.Validation.IArrangementRule x16",
        .. DotCharter.Services.Plugins.PluginLoader.Declared(typeof(DotCharter.UI.App).Assembly).Count == 0
            ? Array.Empty<string>()
            : ["DotCharter.Services.Library.ILibraryPackageSource x2"],
        "DotCharter.UI.Input.Tools.IEditorTool x4",
    ];
}
