using System.Text.RegularExpressions;
using DotCharter.Tone.UI.ViewModels.Shell;
using DotCharter.UI.Docking;
using DotCharter.UI.ViewModels.Shell;

namespace DotCharter.Tests.UI;

/// <summary>
///     The three docking rules in <c>DotCharter.UI/AGENTS.md</c> that had no
///     guard at all until 0.58, each of which fails only at RUNTIME and only on
///     a float / pop-out — i.e. never during a build, never in the sweep, and
///     usually not until a user does it:
///
///     <list type="number">
///       <item><b><c>Dockable.Context</c> must be a VIEW MODEL, never a
///         control.</b> A control instance can have exactly one visual parent,
///         and float/redock materializes a second view — "already has a visual
///         parent", a hard crash from a drag. The property TYPE is the primary
///         guard now (<see cref="DockPaneDefinition.Context" /> is
///         <see cref="ViewModelBase" />); this class pins that it stays that
///         way and that every registered pane's Context actually resolves to a
///         view, since a VM the ViewLocator cannot match renders a
///         "Not Found: …" TextBlock in the dock instead of the pane.</item>
///       <item><b><c>Preview3DView</c> and the chart-timeline composite must
///         stay SINGLE instances.</b> A second one races the native swapchain.
///         Both pop-outs MOVE the one instance between top-levels; nothing may
///         construct another.</item>
///       <item><b>Pop-out windows are owned by the main window</b>, so they
///         close with it — and their Closed handlers must not re-parent that
///         single instance into a window that is tearing down.</item>
///     </list>
///
///     These are source lints and reflection, not layout: a default-gate test
///     may not build the real shell (see <c>DotCharter.Tests/AGENTS.md</c>), and
///     an actual float needs a <c>HostWindow</c>.
/// </summary>
public sealed class DockPaneContractTests
{
    // ------------------------------------------------------------------
    // 1. Context is a view model
    // ------------------------------------------------------------------

    /// <summary>
    ///     Pins the narrowing. <c>Context</c> was <c>object</c> — which made
    ///     "assign a control" a compile-clean mistake whose only symptom was a
    ///     crash on the first float. Widening it back would silently re-open
    ///     that, so the type is asserted rather than assumed.
    /// </summary>
    [Fact]
    public void ThePaneContextPropertyIsTypedAsAViewModel()
    {
        var context = typeof(DockPaneDefinition).GetProperty(nameof(DockPaneDefinition.Context));

        Assert.NotNull(context);
        Assert.True(context!.PropertyType == typeof(ViewModelBase),
            "DockPaneDefinition.Context must stay typed as ViewModelBase — as 'object' " +
            "(or any control-compatible type) a control instance compiles here and crashes " +
            $"on float/redock with \"already has a visual parent\". It is currently {context.PropertyType}.");
    }

    /// <summary>
    ///     Every pane the shell registers names a view-model PROPERTY on
    ///     <see cref="MainWindowViewModel" />, and that property's declared type
    ///     resolves to a real view by the ViewLocator convention. A pane whose
    ///     VM has no matching view is not a crash — it is a pane that renders
    ///     the words "Not Found: …" and nothing else.
    /// </summary>
    [Fact]
    public void EveryRegisteredPaneContextIsAViewModelThatResolvesToAView()
    {
        var contexts = RegisteredPaneContexts();

        // Non-vacuous floor: the shell registers Inspector + SheetMusic. If the
        // regex stops matching (a rename, a reformat, a move to another file)
        // this must fail rather than pass over an empty set.
        Assert.True(contexts.Count >= 2,
            "Expected at least the Inspector and SheetMusic pane contexts in MainWindow.Docking.cs; " +
            $"found {contexts.Count} ({string.Join(", ", contexts)}). If the registration moved, " +
            "repoint this lint — do not let it scan a file that no longer contains its subject.");

        foreach (var name in contexts)
        {
            var property = typeof(MainWindowViewModel).GetProperty(name);
            Assert.True(property != null,
                $"Pane Context 'vm.{name}' names no property on MainWindowViewModel.");

            Assert.True(typeof(ViewModelBase).IsAssignableFrom(property!.PropertyType),
                $"Pane Context 'vm.{name}' is a {property.PropertyType.Name}, which does not derive " +
                "from ViewModelBase — ViewLocator.Match keys on ViewModelBase, so the dock host " +
                "would never materialize a view for it.");

            var view = DotCharter.Tone.UI.ViewLocator.ResolveViewType(property.PropertyType);
            Assert.True(view != null,
                $"Pane Context 'vm.{name}' ({property.PropertyType.FullName}) resolves to no view — " +
                "the pane renders a \"Not Found: …\" placeholder. Keep the VM under a ViewModels " +
                "sub-namespace with its view under the mirrored Views one.");
        }
    }

    /// <summary>
    ///     A pane Context is read off the view model, never constructed at the
    ///     registration site. <c>Context = new SomethingView()</c> is the
    ///     original bug shape; <c>Context = new SomethingViewModel()</c> is the
    ///     quieter one — a second VM instance beside the shell's, so the pane
    ///     shows state nothing else updates.
    /// </summary>
    [Fact]
    public void NoPaneConstructsItsOwnContext()
    {
        var source = DockingSource();

        Assert.DoesNotMatch(new Regex(@"Context\s*=\s*new\b"), source);
    }

    // ------------------------------------------------------------------
    // 2. Single-instance surfaces
    // ------------------------------------------------------------------

    /// <summary>
    ///     <c>Preview3DView</c> hosts the native Vulkan child HWND. A second
    ///     instance races the swapchain, so exactly one may be DECLARED (in
    ///     MainWindow.axaml) and none may be constructed in code — the pop-out
    ///     re-parents the declared one.
    /// </summary>
    [Fact]
    public void ThereIsExactlyOnePreview3DViewInTheWholeApp()
    {
        // Element usages only: the view's own x:Class line and prose in comments
        // must not count, or the lint measures the wrong thing.
        var declarations = AxamlFiles()
            .Sum(f => Regex.Matches(UiSourceTree.ReadAllText(f), @"<\w+:Preview3DView\b").Count);

        Assert.True(declarations == 1,
            $"Preview3DView is declared {declarations} times in XAML; exactly one instance may exist " +
            "(a second one races the native swapchain). The pop-out MOVES the single instance.");

        var constructed = CsFiles()
            .Where(f => Regex.IsMatch(UiSourceTree.ReadAllText(f), @"new\s+Preview3DView\s*\("))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(constructed.Count == 0,
            "These files construct a second Preview3DView in code: " + string.Join(", ", constructed));
    }

    /// <summary>
    ///     The chart-timeline composite is the same rule one surface over: the
    ///     <c>TimelineGrid</c> subtree (with <c>MainChartRenderer</c> inside it)
    ///     is moved between top-levels by View → Float Chart Timeline.
    ///     <c>ChartRenderer</c> itself is deliberately NOT unique — the stacked
    ///     tracks view draws one per arrangement — so the named instances are
    ///     what is pinned.
    /// </summary>
    [Theory]
    [InlineData("TimelineGrid")]
    [InlineData("MainChartRenderer")]
    [InlineData("Preview3DPane")]
    public void TheMovableCompositesAreDeclaredExactlyOnce(string name)
    {
        var declarations = AxamlFiles()
            .Sum(f => Regex.Matches(UiSourceTree.ReadAllText(f), $@"x:Name=""{name}""").Count);

        Assert.True(declarations == 1,
            $"x:Name=\"{name}\" is declared {declarations} times; the pop-out moves ONE instance " +
            "between top-levels and a second would race it.");
    }

    // ------------------------------------------------------------------
    // 3. Pop-out window lifetime
    // ------------------------------------------------------------------

    /// <summary>
    ///     Both pop-outs are owned windows (<c>Show(this)</c>), so closing the
    ///     main window closes them, which runs their Closed handler — which
    ///     re-parents the single instance back into the dying main window and
    ///     restarts its render loop from <c>OnAttachedToVisualTree</c>. The
    ///     handlers must bail on the shutdown path.
    /// </summary>
    [Fact]
    public void BothPopOutCloseHandlersBailOutOnTheShutdownPath()
    {
        var source = DockingSource();

        Assert.Equal(2, Regex.Matches(source, @"if\s*\(_shuttingDown\)\s*return;").Count);
        // ...and the flag has to actually be raised before the close cascade.
        Assert.Contains("_shuttingDown = true;", source);
    }

    /// <summary>
    ///     <c>OnClosing</c> latches the flag and closes the pop-outs BEFORE
    ///     anything else — the persist steps that follow are what re-enter the
    ///     layout while the windows are still up.
    /// </summary>
    [Fact]
    public void OnClosingLatchesTheShutdownFlagBeforePersistingTheLayout()
    {
        // Scoped to OnClosing's OWN body. Read across the whole (split) shell
        // code-behind this would compare the two METHOD DEFINITIONS instead of
        // the two calls, and pass no matter what order OnClosing used them in —
        // the exact way a path-based lint goes quietly vacuous in this repo.
        var body = OnClosingBody();

        // The bare IDENTIFIER, not `Name()`: these are now passed as method
        // GROUPS to the shutdown-step helper (`TryOnClose("…", PersistDockLayout)`),
        // and a lint that only recognises a direct call site would have read that
        // as "OnClosing never calls it" — vacuously green the day someone added
        // an `Assert.True(latch >= 0)` escape hatch instead. Comments are stripped
        // by OnClosingBody so a mention in prose cannot stand in for a call.
        var latch = body.IndexOf("MarkShuttingDownAndClosePopOuts", StringComparison.Ordinal);
        var persist = body.IndexOf("PersistDockLayout", StringComparison.Ordinal);

        Assert.True(latch >= 0, "OnClosing never calls MarkShuttingDownAndClosePopOuts().");
        Assert.True(persist >= 0, "OnClosing never calls PersistDockLayout().");
        Assert.True(latch < persist,
            "The shutdown latch must be set before the layout persist — the pop-outs close with the " +
            "owner and their Closed handlers run in between.");
    }

    /// <summary>The text of <c>MainWindow.OnClosing</c> from its signature to
    /// the end of the file's method — enough to order the calls inside it.</summary>
    private static string OnClosingBody()
    {
        var source = UiSourceTree.ReadAllText(
            UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views", "MainWindow.axaml.cs")));
        var start = source.IndexOf("protected override void OnClosing", StringComparison.Ordinal);
        Assert.True(start > 0, "MainWindow.OnClosing not found in MainWindow.axaml.cs.");

        // Up to the next member declaration at class indentation.
        var end = source.IndexOf("\n    private", start, StringComparison.Ordinal);
        if (end < 0) end = source.Length;

        // Comments stripped: the method matches bare identifiers, so a name
        // mentioned in a comment would otherwise count as a call site.
        return string.Join('\n', source[start..end]
            .Split('\n')
            .Select(line => line.TrimStart().StartsWith("//", StringComparison.Ordinal) ? "" : line));
    }

    /// <summary>
    ///     View → Reset Layout brings EVERYTHING home. The dock panes come home
    ///     via <c>Rebuild()</c>, but the two pop-outs are not Dock.Avalonia
    ///     panes and Rebuild cannot see them: before 0.58, "float the timeline,
    ///     then Reset Layout" left the timeline window open over a main window
    ///     showing the popped-out placeholder — the same orphan shape the manual
    ///     checklist already tested for on the dock side.
    /// </summary>
    [Fact]
    public void ResetLayoutClosesThePopOutWindowsToo()
    {
        var source = DockingSource();
        var start = source.IndexOf("private void ResetLayoutToDefaults", StringComparison.Ordinal);
        Assert.True(start > 0, "ResetLayoutToDefaults not found in MainWindow.Docking.cs.");

        Assert.Contains("ClosePopOutWindows();", source[start..], StringComparison.Ordinal);
        AssertClosePopOutWindowsClosesBoth();
    }

    /// <summary>
    ///     Entering the gameplay presentation must bring them home too. The run
    ///     is PRESENTED in the 3D preview panel — <c>ApplyGameplayLayout</c>
    ///     collapses the timeline row and the tool dock so that panel owns the
    ///     window — so starting a run with the panel popped out counted the user
    ///     in against a main window showing the "popped out" placeholder, with
    ///     the highway drawing in a small window off to one side. The timeline
    ///     pop-out is the mirror case: the restore branch turns
    ///     <c>TimelineSlot</c> visible again unconditionally, which would
    ///     uncollapse an empty slot beside its own placeholder.
    /// </summary>
    [Fact]
    public void EnteringTheGameplayPresentationClosesThePopOutWindowsToo()
    {
        var source = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(
            TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Shell", "MainWindowShellLayout.cs")));
        var start = source.IndexOf("private void ApplyGameplayLayout", StringComparison.Ordinal);
        Assert.True(start > 0, "ApplyGameplayLayout not found in MainWindowShellLayout.cs.");

        // The ENTER branch only — the restore branch below it must not close
        // them, and reading the whole method would not tell the two apart.
        var enter = source.IndexOf(
            "if (vm.IsGameplayPresentation && !_gameplayLayoutApplied)", start, StringComparison.Ordinal);
        Assert.True(enter > start, "ApplyGameplayLayout no longer has its enter branch.");
        var exit = source.IndexOf("else if (!vm.IsGameplayPresentation", enter, StringComparison.Ordinal);
        Assert.True(exit > enter, "ApplyGameplayLayout no longer has its restore branch.");

        Assert.Contains("_window.ClosePopOutWindows();", source[enter..exit], StringComparison.Ordinal);
        AssertClosePopOutWindowsClosesBoth();
    }

    /// <summary>Both callers above go through one helper, so this is what keeps
    /// them honest: it has to close BOTH pop-outs, not just the one whose bug
    /// was reported.</summary>
    private static void AssertClosePopOutWindowsClosesBoth()
    {
        var source = DockingSource();
        var start = source.IndexOf("internal void ClosePopOutWindows", StringComparison.Ordinal);
        Assert.True(start > 0, "ClosePopOutWindows not found in MainWindow.Docking.cs.");
        var end = source.IndexOf("\n    }", start, StringComparison.Ordinal);
        Assert.True(end > start, "ClosePopOutWindows has no body.");

        var body = source[start..end];
        Assert.Contains("_preview3DWindow?.Close();", body, StringComparison.Ordinal);
        Assert.Contains("_timelineWindow?.Close();", body, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // 4. The bottom tool area is exactly "a registered pane is visible"
    // ------------------------------------------------------------------

    /// <summary>
    ///     <c>MainWindowViewModel.IsBottomToolAreaVisible</c> decides whether the
    ///     <c>BottomDock</c> DockControl gets a column and a row, so it must name
    ///     exactly the visibility flags of the panes registered in
    ///     <c>InitializeBottomDock</c> — no more.
    ///     <para>
    ///         It named three for six releases after 0.42.4 turned two of them
    ///         (Stem Mixer, Riff Library) from panes into inspector TABS. A tab
    ///         flag cannot hold the dock open, because with the Inspector pane
    ///         closed the dock has nothing to draw: turning the Riff Library on
    ///         sets BOTH flags, and closing the Inspector afterwards left an
    ///         EMPTY DockControl holding its third of the bottom row, with
    ///         <c>IsPreviewOnlyVisible</c> false so the 3D preview could not
    ///         widen into it.
    ///     </para>
    ///     <para>
    ///         Derived from the registry rather than hard-coded, so a pane added
    ///         to (or removed from) <c>InitializeBottomDock</c> fails here until
    ///         the derived property follows it — which is the drift this exact
    ///         bug was.
    ///     </para>
    /// </summary>
    [Fact]
    public void TheBottomToolAreaFlagNamesExactlyTheRegisteredBottomPanes()
    {
        var registered = BottomPaneVisibilityFlags();
        Assert.NotEmpty(registered);

        var layout = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(
            TestPaths.RepoRoot(), "DotCharter.UI", "ViewModels", "Shell", "MainWindowViewModel.Layout.cs")));
        var expression = Regex.Match(layout,
            @"public bool IsBottomToolAreaVisible\s*=>\s*(?<body>[^;]+);");
        Assert.True(expression.Success,
            "IsBottomToolAreaVisible is no longer an expression-bodied property in MainWindowViewModel.Layout.cs.");

        var named = Regex.Matches(expression.Groups["body"].Value, @"\bIs\w*Visible\b")
            .Select(m => m.Value)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(registered.OrderBy(f => f, StringComparer.Ordinal),
            named.OrderBy(f => f, StringComparer.Ordinal));
    }

    /// <summary>
    ///     …and the two INSPECTOR-TAB flags stop claiming to be up when the
    ///     Inspector pane closes. They are what the View menu draws its
    ///     checkboxes from and what the keybinds toggle, so a stale <c>true</c>
    ///     showed "Riff Library ✓" with no riff library on screen — and made the
    ///     keybind's first press a no-op, because it flipped the stale true back
    ///     to false and the shell's property switch only acts on "show it".
    /// </summary>
    [Fact]
    public void ClosingTheInspectorClearsTheInspectorTabFlags()
    {
        var layout = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(
            TestPaths.RepoRoot(), "DotCharter.UI", "ViewModels", "Shell", "MainWindowViewModel.Layout.cs")));
        var handler = Regex.Match(layout,
            @"partial void OnIsInspectorVisibleChanged\(bool value\)\s*\{(?<body>.*?)\n    \}",
            RegexOptions.Singleline);
        Assert.True(handler.Success,
            "MainWindowViewModel has no OnIsInspectorVisibleChanged handler — closing the Inspector " +
            "would leave the Mixer / Riff Library tab flags claiming to be on screen.");

        var body = handler.Groups["body"].Value;
        Assert.Contains("IsStemMixerVisible = false;", body, StringComparison.Ordinal);
        Assert.Contains("IsPhraseCatalogVisible = false;", body, StringComparison.Ordinal);
    }

    /// <summary>The <c>IsVisible = () =&gt; vm.X</c> flags of the panes registered
    /// in the BOTTOM dock only — <c>InitializeBottomDock</c> declares the top
    /// dock's list in the same method.</summary>
    private static List<string> BottomPaneVisibilityFlags()
    {
        var source = DockingSource();
        var start = source.IndexOf("var bottomPanes", StringComparison.Ordinal);
        Assert.True(start > 0, "InitializeBottomDock no longer declares 'bottomPanes'.");
        var end = source.IndexOf("var topPanes", start, StringComparison.Ordinal);
        Assert.True(end > start, "InitializeBottomDock no longer declares 'topPanes' after 'bottomPanes'.");

        return Regex.Matches(source[start..end], @"IsVisible\s*=\s*\(\)\s*=>\s*vm\.(\w+)")
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    // ------------------------------------------------------------------

    /// <summary>The <c>Context = vm.X</c> of every registered pane.</summary>
    private static List<string> RegisteredPaneContexts() =>
        Regex.Matches(DockingSource(), @"Context\s*=\s*vm\.(\w+)")
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static string DockingSource() =>
        UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views", "MainWindow.Docking.cs")));

    private static string[] AxamlFiles() =>
        UiSourceTree.GetFiles(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI"), "*.axaml", SearchOption.AllDirectories);

    private static string[] CsFiles() =>
        UiSourceTree.GetFiles(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI"), "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
            .ToArray();
}
