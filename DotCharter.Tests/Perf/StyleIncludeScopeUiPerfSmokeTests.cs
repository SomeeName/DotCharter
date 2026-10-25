using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.UI;
using DotCharter.UI.Views;
using DotCharter.UI.Views.Shell;
using DotCharter.UI.ViewModels.Shell;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     Where the view grammars reach on the REAL prewarmed shell (#412).
///     <para>
///         A style is attached to every control its selector's TYPE matches inside the
///         scope that declares it — a value frame, an activator and a
///         <c>DynamicResource</c> expression per setter, whether or not the control ever
///         wears the class. Measured on the prewarmed shell (24,500 controls): the
///         settings grammar included by the shell AND by each tab attached every rule
///         twice to every control in every tab (33,543 duplicate frames), and the
///         editor's workspace grammar at window scope put ~54,000 frames on Play Mode,
///         Settings and every prewarmed dialog. Together ~64 MB of managed heap.
///     </para>
///     <para>
///         The walk follows each control's styling parents (<see cref="IStyleHost.StylingParent" />)
///         and reads the <see cref="StyleInclude" />s in their <c>Styles</c>, which is what
///         Avalonia itself walks when it attaches styles.
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class StyleIncludeScopeUiPerfSmokeTests
{
    private const string WorkspaceGrammar = "avares://DotCharter/Views/Editor/EditorWorkspaceStyles.axaml";
    private const string SettingsGrammar = "avares://DotCharter/Views/Settings/SettingsStyles.axaml";

    /// <summary>The shell's non-editor surfaces, which no workspace rule may reach.</summary>
    private static readonly string[] NonEditorHosts = ["PlayModeHost", "SongBrowserHost", "SettingsHost", "HomeHost", "DialogContainer"];

    [Fact]
    public async Task NoControl_InheritsAGrammarTwice_AndTheWorkspaceGrammarStaysInTheEditor()
    {
        var ct = TestContext.Current.CancellationToken;
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var services = App.Services ?? throw new InvalidOperationException("the headless app has no services");
            var window = new MainWindow { DataContext = services.GetRequiredService<MainWindowViewModel>(), Width = 1920, Height = 1080 };
            window.Show();
            // The prewarm realizes Settings' tabs, the dialogs and both rigs: the controls
            // whose scope is in question. 3,000 turns is a deadlock detector, not a budget.
            for (var i = 0; i < 3000 && !window.HiddenSurfacesDrained.IsCompleted; i++)
            {
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();
            }

            Assert.True(window.HiddenSurfacesDrained.IsCompleted, "the startup prewarm never drained, so the hidden surfaces were not realized");

            var elements = Realized(window);
            var twice = new List<string>();
            var workspaceOutsideEditor = new List<string>();
            int seeingSettings = 0, seeingWorkspace = 0;
            foreach (var element in elements)
            {
                var includes = InheritedIncludes(element);
                foreach (var dup in includes.GroupBy(s => s, StringComparer.Ordinal).Where(g => g.Count() > 1))
                    twice.Add($"{Describe(element)} inherits {dup.Key} {dup.Count()} times");
                if (includes.Contains(SettingsGrammar)) seeingSettings++;
                if (!includes.Contains(WorkspaceGrammar)) continue;
                seeingWorkspace++;
                if (InsideNonEditorHost(element) is { } host) workspaceOutsideEditor.Add($"{Describe(element)} under {host}");
            }

            // Anti-vacuity: the walk reaches both grammars where they belong.
            Assert.True(elements.Count > 10_000, $"only {elements.Count} controls were realized — the prewarm did not run");
            Assert.True(seeingSettings > 500, $"only {seeingSettings} controls see the settings grammar — the tabs were not realized or the walk is broken");
            Assert.True(seeingWorkspace > 10, $"only {seeingWorkspace} controls see the workspace grammar — the editor lost it");

            Assert.True(workspaceOutsideEditor.Count == 0,
                $"{workspaceOutsideEditor.Count} control(s) outside the chart editor see EditorWorkspaceStyles; its rules attach to " +
                "every Border/TextBlock there (#412). Include it on the editor element that wears it, never on the window:\n  " +
                string.Join("\n  ", workspaceOutsideEditor.Take(15)));
            Assert.True(twice.Count == 0,
                $"{twice.Count} control(s) inherit the same grammar from two scopes, so every rule is attached to them twice " +
                "(a view and a view hosting it both include it, #412). Include it at ONE level:\n  " +
                string.Join("\n  ", twice.Take(15)));
        }, ct);
    }

    /// <summary>
    ///     Frames that style RULES (not ControlThemes) of each scope attach to the
    ///     Borders, TextBlocks and panels there, measured 2026-10-08 on the prewarmed
    ///     shell after #428: PlayModeView 467, the dialog host 1,910, the settings shell 0,
    ///     the settings tabs 4,032, the app's rules on panels 154. Before it the scopes'
    ///     whole censuses were 25,812 / 36,003 / 9,479 / 37,435 frames, and the app's
    ///     StackPanel/Grid class rules ~25,000. The settings tabs reached 4,202 by 2026-10-11 as
    ///     keybind rows were added, and 3,661 once the keybind rows' duplicate rule left
    ///     SettingsStyles.axaml (#639). The dialog host reached 2,040 with the song-package
    ///     export (#707) and 1,395 once the empty state's text rule became a CHILD rule
    ///     (`Border.DlgEmpty > TextBlock`, attached to ~70 TextBlocks instead of ~700).
    ///     The strip text rule became child rules the same way (`Border.DlgStrip > TextBlock`, …):
    ///     the dialog host measured 818 with the Tone Designer's AI creator in it, reached 912
    ///     with the onboarding output pair (#263), and 583 once the card's centring rule
    ///     (`Grid.DialogScrim > Border.DialogCard`) moved into the DialogCardBorder theme.
    ///     Shrink-only: lower a ceiling when a cut lands; raising one is the regression this
    ///     exists to stop.
    /// </summary>
    private static readonly (string Scope, Func<StyledElement, bool> Controls, int Ceiling)[] RuleFrameCeilings =
    [
        ("PlayModeView", e => e is Border or TextBlock or Panel, 500),
        ("DialogContainerView", e => e is Border or TextBlock or Panel, 600),
        ("SettingsShellView", e => e is Border or TextBlock or Panel, 0),
        ("SettingsView", e => e is Border or TextBlock or Panel, 3_800),
        ("App", e => e is Panel, 200),
    ];

    /// <summary>
    ///     #428: the Border / TextBlock / Grid / StackPanel families of the Play Mode,
    ///     dialog and settings grammars and the app's Fire panel families are
    ///     ControlThemes, whose rules attach only to the controls that wear them. A
    ///     `Border.X` / `TextBlock.X` / `Grid.X` / `StackPanel.X` rule put back in one of
    ///     those scopes is attached to every control of its type there again, and this
    ///     ceiling fails with the rules that did it.
    /// </summary>
    [Fact]
    public async Task TheConvertedGrammars_StayWithinTheirRuleFrameCeilings()
    {
        var ct = TestContext.Current.CancellationToken;
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var window = PrewarmedWindow();
            var failures = new List<string>();
            var report = new System.Text.StringBuilder();
            foreach (var (scope, controls, ceiling) in RuleFrameCeilings)
            {
                var census = StyleFrameCensus.Take(window, controls);
                var rules = census.Rules.Where(r => !r.Selector.StartsWith("theme", StringComparison.Ordinal)
                                                    && InScope(r.Scope, scope)).ToList();
                var frames = rules.Sum(r => r.Frames);
                report.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"{scope}: {frames} rule frames");
                if (frames > ceiling)
                {
                    failures.Add($"{scope}: {frames} frames > {ceiling}; widest rules:\n    " + string.Join("\n    ",
                        rules.OrderByDescending(r => r.Frames).Take(8).Select(r => $"{r.Frames,6}  {r.Selector}")));
                }
            }

            TestContext.Current.TestOutputHelper?.WriteLine(report.ToString());
            // Anti-vacuity: the converted families are worn (a theme's rules attach to its
            // wearers alone), so the walk reaches each grammar's controls.
            var all = StyleFrameCensus.Take(window);
            foreach (var (rule, min) in new[] { ("^.Eyebrow", 10), ("^.DlgRow", 100), ("^.FormRow", 100), ("^.FireEmptyState", 10) })
            {
                var worn = all.Rules.Where(r => r.Selector == "theme-rule " + rule).Sum(r => r.Frames);
                Assert.True(worn >= min, $"only {worn} controls wear the theme rule {rule} — the family is not worn, or the walk is broken");
            }

            Assert.True(failures.Count == 0,
                "A style RULE is attached to every control of its type in its scope, worn or not (#428). " +
                "Make the family a ControlTheme its wearers wear (see the grammar's header):\n  " + string.Join("\n  ", failures));
        }, ct);
    }

    /// <summary>A census scope is the type (and name) of the element whose Styles hold the rule.</summary>
    private static bool InScope(string censusScope, string scope) => scope switch
    {
        "App" => censusScope == "App",
        "SettingsView" => censusScope.EndsWith("SettingsView", StringComparison.Ordinal) && !censusScope.StartsWith("SettingsShellView", StringComparison.Ordinal),
        _ => censusScope.StartsWith(scope, StringComparison.Ordinal),
    };

    /// <summary>The per-rule frame census on the prewarmed shell (#428). Writes the table to
    /// <c>DOTCHARTER_STYLE_CENSUS_OUT</c> when set (the measurement), and logs the total.</summary>
    [Fact]
    public async Task StyleFrameCensus_CountsTheShell()
    {
        var ct = TestContext.Current.CancellationToken;
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var window = PrewarmedWindow();
            var census = StyleFrameCensus.Take(window);
            var text = StyleFrameCensus.Format(census, 5000);
            if (Environment.GetEnvironmentVariable("DOTCHARTER_STYLE_CENSUS_OUT") is { Length: > 0 } path)
                File.WriteAllText(path, text);
            TestContext.Current.TestOutputHelper?.WriteLine(text);
            Assert.True(census.Controls > 10_000, $"only {census.Controls} controls were realized — the prewarm did not run");
        }, ct);
    }

    /// <summary>The effective look of every realized control, one line per control and
    /// property, written to <c>DOTCHARTER_STYLE_VALUES_OUT</c> when set: the before/after
    /// equality check of a style move (#428). Inert without the variable.</summary>
    [Fact]
    public async Task EffectiveValues_DumpForComparison()
    {
        if (Environment.GetEnvironmentVariable("DOTCHARTER_STYLE_VALUES_OUT") is not { Length: > 0 } path) return;
        var ct = TestContext.Current.CancellationToken;
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var window = PrewarmedWindow();
            using var writer = new StreamWriter(path);
            EffectiveValueDump.Write(window, writer);
        }, ct);
    }

    private static MainWindow PrewarmedWindow()
    {
        var services = App.Services ?? throw new InvalidOperationException("the headless app has no services");
        var window = new MainWindow { DataContext = services.GetRequiredService<MainWindowViewModel>(), Width = 1920, Height = 1080 };
        window.Show();
        // The prewarm realizes Settings' tabs, the dialogs and both rigs: the controls
        // whose scope is in question. 3,000 turns is a deadlock detector, not a budget.
        for (var i = 0; i < 3000 && !window.HiddenSurfacesDrained.IsCompleted; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }

        Assert.True(window.HiddenSurfacesDrained.IsCompleted, "the startup prewarm never drained, so the hidden surfaces were not realized");
        return window;
    }

    private static List<StyledElement> Realized(StyledElement root)
    {
        var seen = new HashSet<StyledElement>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<StyledElement>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var e = stack.Pop();
            if (!seen.Add(e)) continue;
            if (e is Visual v)
            {
                foreach (var c in v.GetVisualChildren())
                    if (c is StyledElement s) stack.Push(s);
            }

            foreach (var c in ((ILogical)e).LogicalChildren)
                if (c is StyledElement s) stack.Push(s);
        }

        return [.. seen];
    }

    /// <summary>The <c>StyleInclude</c> sources of every styling ancestor, the element included.
    /// Application styles are global by design and not counted.</summary>
    private static List<string> InheritedIncludes(StyledElement element)
    {
        var found = new List<string>();
        for (IStyleHost? host = element; host is not null and not Application; host = host.StylingParent)
        {
            if (host is not StyledElement { Styles: { Count: > 0 } styles }) continue;
            foreach (var item in styles)
            {
                // The XAML compiler replaces a <StyleInclude> by the included file's own
                // Styles instance, so a grammar is recognised by its selectors.
                switch (item)
                {
                    case StyleInclude { Source: { } source }:
                        found.Add(source.ToString());
                        break;
                    case Styles group when Fingerprint(group) is { Length: > 0 } print:
                        found.Add(print);
                        break;
                }
            }
        }

        return found;
    }

    /// <summary>A grammar's identity: its first selectors, with the two this test names mapped
    /// back to their files.</summary>
    private static string Fingerprint(Styles group)
    {
        var selectors = group.OfType<Style>().Select(s => s.Selector?.ToString()).Where(s => s is not null).Take(6).ToList();
        if (selectors.Contains("Border.TrackRail")) return WorkspaceGrammar;
        // the settings grammar's families are ControlThemes since #428: known by its row theme
        if (group.Resources.ContainsKey("FormRowGrid")) return SettingsGrammar;
        return selectors.Count == 0 ? "" : "styles{" + string.Join(" | ", selectors) + "}";
    }

    private static string? InsideNonEditorHost(StyledElement element)
    {
        for (var e = element as Visual; e is not null; e = e.GetVisualParent())
        {
            if (e is StyledElement { Name: { } name } && NonEditorHosts.Contains(name, StringComparer.Ordinal))
                return name;
        }

        return null;
    }

    private static string Describe(StyledElement element)
    {
        var view = (element as Visual)?.GetSelfAndVisualAncestors()
            .FirstOrDefault(a => a.GetType().Namespace?.StartsWith("DotCharter", StringComparison.Ordinal) == true);
        return $"{element.GetType().Name}{(string.IsNullOrEmpty(element.Name) ? "" : "#" + element.Name)} in {view?.GetType().Name ?? "?"}";
    }
}
