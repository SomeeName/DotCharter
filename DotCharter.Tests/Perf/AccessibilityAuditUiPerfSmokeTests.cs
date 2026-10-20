using System.Globalization;
using System.Text;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.VisualTree;
using DotCharter.Presentation.Fakes.Library;
using DotCharter.Presentation.Fakes.PlayMode;
using DotCharter.UI.Views.Library;
using DotCharter.UI.Views.PlayMode.Redesign;

namespace DotCharter.Tests.Perf;

/// <summary>
///     <b>The screen-reader audit (#311)</b> — the closest automatable equivalent of driving the app with
///     Narrator or NVDA. <see cref="UiSweepRunner" /> builds every view on the app's real view-model graph,
///     realizes its hidden screens, and hands each to <see cref="AccessibilityAudit" />, which walks the
///     AUTOMATION tree UIA sees. This class reports per screen and holds a RATCHET: the count of unnamed
///     interactive controls per surface may only go down.
///     <para>
///         The report is written to the test output and to <c>TestResults/a11y-audit.md</c> next to the test
///         DLL. Markup-level naming rules stay in <c>UI/AccessibleNameTests</c> (default gate); this is the
///         runtime half, which also sees templated rows, code-built controls and panel-derived names.
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class AccessibilityAuditUiPerfSmokeTests(ITestOutputHelper output)
{
    /// <summary>
    ///     The task the issue names — open a song, play it, edit a chord, export — screen by screen. Each must
    ///     be audited AND expose interactive controls, or the audit passed by looking at nothing.
    /// </summary>
    internal static readonly string[] TaskPath =
    [
        "LibraryView", "SongSelectView", "SongSelectScreenView", "PauseMenuView", "ToolbarView", "InspectorView",
        "NotesInspectorView", "ChordTemplateEditorView", "RocksmithExportDialogView", "DcSongExportDialogView"
    ];

    /// <summary>
    ///     The ratchet's baseline: unnamed interactive controls per swept surface, measured on this branch.
    ///     <b>Only ever LOWER an entry</b> — when a fix names a control, the test fails until the entry comes
    ///     down with it; a surface missing here is allowed none. Same shape as
    ///     <c>Infrastructure/SourceFileSizeBudgetTests</c>' list.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, int> UnnamedBaseline = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        // (the gear browser's tile-pair rows left this list in #857: the row is Raw, each tile a named Button)
        ["AmpFxRigView"] = 0,
        ["PlayModeView"] = 0,
        // Named in the markup by a binding to DATA the sweep's fixture leaves empty (the tempo-map verb's label,
        // a lyrics parameter's display name, an expression pedal's position, the next setlist song). Named in
        // the product; they stay here until the sweep seeds those view models.
        ["TempoMapOperationDialogView"] = 2,
        ["RigModuleEditorView"] = 1,
        ["RigPedalDetailsView"] = 1,
        ["RigExpressionView"] = 1,
        ["ResultsScreenView"] = 1,
    };

    [Fact]
    public async Task EverySurface_IsWalkedThroughItsAutomationTree_AndTheTaskPathIsCovered()
    {
        UiSweepReport report = null!;
        await HeadlessSessionFixture.Dispatch(() => report = UiSweepRunner.Run(), CancellationToken.None);

        var markdown = Markdown(report);
        output.WriteLine(markdown);
        Directory.CreateDirectory(UiSweepRunner.ArtifactDirectory);
        File.WriteAllText(Path.Combine(UiSweepRunner.ArtifactDirectory, "a11y-audit.md"), markdown);

        var audited = report.Results.Where(r => r.Accessibility != null)
            .ToDictionary(r => r.Surface, r => r.Accessibility!, StringComparer.Ordinal);
        var thin = TaskPath.Where(s => !audited.TryGetValue(s, out var a) || a.Interactive == 0).ToList();
        Assert.True(thin.Count == 0,
            "these task-path screens were not audited or exposed no interactive control to UIA: " +
            string.Join(", ", thin));
    }

    [Fact]
    public async Task UnnamedInteractiveControls_PerSurface_OnlyEverGoDown()
    {
        UiSweepReport report = null!;
        await HeadlessSessionFixture.Dispatch(() => report = UiSweepRunner.Run(), CancellationToken.None);

        var problems = new List<string>();
        foreach (var result in report.Results.Where(r => r.Accessibility != null))
        {
            var actual = result.Accessibility!.Count(AccessibilityAudit.Unnamed);
            var allowed = UnnamedBaseline.GetValueOrDefault(result.Surface);
            if (actual > allowed)
            {
                problems.Add($"{result.Surface}: {actual} unnamed interactive control(s), baseline {allowed} — " +
                             "name them (AutomationProperties.Name off a localized key):\n      " +
                             string.Join("\n      ", result.Accessibility.Findings
                                 .Where(f => f.Kind == AccessibilityAudit.Unnamed)
                                 .Select(f => $"{f.Screen}: {f.ControlType} {f.Where}")));
            }
            else if (actual < allowed)
            {
                problems.Add($"{result.Surface}: {actual} unnamed, baseline {allowed} — ratchet it: set the " +
                             $"UnnamedBaseline entry to {actual}");
            }
        }

        // The other two findings reached zero in #311 and stay there: a decorative image says so
        // (AccessibilityView="Raw"), and an interactive control is focusable or sits in something that is.
        problems.AddRange(report.Results.Where(r => r.Accessibility != null)
            .SelectMany(r => r.Accessibility!.Findings)
            .Where(f => f.Kind is AccessibilityAudit.Unreachable or AccessibilityAudit.UnnamedImage)
            .Select(f => $"{f.Screen}: {f.Kind} {f.ControlType} {f.Where} — none allowed"));

        var swept = report.Results.Select(r => r.Surface).ToHashSet(StringComparer.Ordinal);
        problems.AddRange(UnnamedBaseline.Keys.Where(k => !swept.Contains(k))
            .Select(k => $"{k}: baseline entry for a surface the sweep no longer builds — delete it"));

        Assert.True(problems.Count == 0, string.Join("\n  ", problems));
    }

    /// <summary>
    ///     "Open a song": the Library and Song Select lists, with ROWS. The sweep builds both on the real view
    ///     models, whose song lists are empty in a fresh profile — so the step a reader most needs, picking a
    ///     song, is graded here on the gallery's fakes. Every row must be a named list item.
    /// </summary>
    [Fact]
    public async Task SongRows_InLibraryAndSongSelect_AreNamedListItems()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            AssertRowsNamed(new LibraryView { DataContext = FakeLibraryScreen.Default() }, "LibraryRows");
            AssertRowsNamed(new SongSelectScreenView { DataContext = FakeSongSelectScreen.Variants[0].Create() },
                "SongSelectRedesignList");
        }, TestContext.Current.CancellationToken);
    }

    private void AssertRowsNamed(Control view, string listName)
    {
        var window = new Window { Width = 1400, Height = 900, Content = view };
        window.Show();
        window.UpdateLayout();
        try
        {
            var list = view.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == listName);
            var rows = list.GetVisualDescendants().OfType<ListBoxItem>().ToList();
            Assert.True(rows.Count > 0, $"{listName} realized no rows — the check would pass on nothing");

            var audit = AccessibilityAudit.Audit(view.GetType().Name, view, []);
            output.WriteLine($"{view.GetType().Name}: {rows.Count} rows, {audit.Interactive} interactive, " +
                             $"first row announced as \"{ControlAutomationPeer.CreatePeerForElement(rows[0]).GetName()}\"");
            Assert.True(audit.Findings.Count == 0,
                string.Join("\n", audit.Findings.Select(f => $"{f.Screen}: {f.Kind} {f.ControlType} {f.Where}")));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The auditor itself, on a tree whose answer is known — a guard nobody has seen fail is not one.</summary>
    [Fact]
    public async Task Auditor_FlagsUnnamedUnreachableAndUnnamedImages_AndNothingElse()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var named = new Button { Content = "Play" };
            var unnamed = new Button { Content = new PathIcon() };
            var unreachable = new Button { Content = "Ghost", Focusable = false };
            var decorative = new Image { Width = 8, Height = 8, Source = Pixel() };
            AutomationProperties.SetAccessibilityView(decorative, AccessibilityView.Raw);
            var image = new Image { Width = 8, Height = 8, Source = Pixel() };
            var window = new Window
            {
                Content = new StackPanel { Children = { named, unnamed, unreachable, decorative, image } }
            };
            window.Show();
            window.UpdateLayout();

            var result = AccessibilityAudit.Audit("Probe", (Control)window.Content!, []);
            window.Close();

            Assert.Equal(3, result.Interactive);
            Assert.Equal(1, result.Count(AccessibilityAudit.Unnamed));
            Assert.Equal(1, result.Count(AccessibilityAudit.Unreachable));
            Assert.Contains("Ghost", NameOf(result, AccessibilityAudit.Unreachable), StringComparison.Ordinal);
            Assert.Equal(1, result.Count(AccessibilityAudit.UnnamedImage));
        }, TestContext.Current.CancellationToken);

        static string NameOf(AccessibilityAuditResult r, string kind) => r.Findings.Single(f => f.Kind == kind).Where;
    }

    private static WriteableBitmap Pixel() =>
        new(new Avalonia.PixelSize(1, 1), new Avalonia.Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);

    /// <summary>The per-screen table the PR body carries, task path first.</summary>
    internal static string Markdown(UiSweepReport report)
    {
        var sb = new StringBuilder();
        var results = report.Results.Where(r => r.Accessibility != null).ToList();
        var order = TaskPath.Select((s, i) => (s, i)).ToDictionary(p => p.s, p => p.i, StringComparer.Ordinal);
        var screens = results.SelectMany(r => r.Accessibility!.InteractiveByScreen.Keys
                .Concat(r.Accessibility.Findings.Select(f => f.Screen))
                .Distinct(StringComparer.Ordinal)
                .Select(screen => (r.Surface, Screen: screen, Audit: r.Accessibility)))
            .OrderBy(x => order.GetValueOrDefault(x.Surface, int.MaxValue))
            .ThenBy(x => x.Screen, StringComparer.Ordinal)
            .ToList();

        sb.AppendLine(CultureInfo.InvariantCulture,
            $"| Screen | Task path | Interactive | Unnamed | Keyboard-unreachable | Unnamed images |");
        sb.AppendLine("|---|---|---:|---:|---:|---:|");
        foreach (var (surface, screen, audit) in screens)
        {
            var findings = audit.Findings.Where(f => f.Screen == screen).ToList();
            var count = (string kind) => findings.Count(f => f.Kind == kind);
            var any = count(AccessibilityAudit.Unnamed) + count(AccessibilityAudit.Unreachable) +
                      count(AccessibilityAudit.UnnamedImage);
            if (any == 0 && !order.ContainsKey(surface)) continue;
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"| `{screen}` | {(order.ContainsKey(surface) ? "yes" : "")} | " +
                $"{audit.InteractiveByScreen.GetValueOrDefault(screen)} | {count(AccessibilityAudit.Unnamed)} | " +
                $"{count(AccessibilityAudit.Unreachable)} | {count(AccessibilityAudit.UnnamedImage)} |");
        }

        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"Totals over {results.Count} surfaces: {results.Sum(r => r.Accessibility!.Interactive)} interactive, " +
            $"{results.Sum(r => r.Accessibility!.Count(AccessibilityAudit.Unnamed))} unnamed, " +
            $"{results.Sum(r => r.Accessibility!.Count(AccessibilityAudit.Unreachable))} keyboard-unreachable, " +
            $"{results.Sum(r => r.Accessibility!.Count(AccessibilityAudit.UnnamedImage))} unnamed images.");
        sb.AppendLine();
        sb.AppendLine("Findings:");
        foreach (var f in results.SelectMany(r => r.Accessibility!.Findings))
            sb.AppendLine(CultureInfo.InvariantCulture, $"- `{f.Screen}` {f.Kind} {f.ControlType}: {f.Where}");
        sb.AppendLine();
        sb.AppendLine("Ratchet entries:");
        foreach (var r in results.Where(r => r.Accessibility!.Count(AccessibilityAudit.Unnamed) > 0))
        {
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"        [\"{r.Surface}\"] = {r.Accessibility!.Count(AccessibilityAudit.Unnamed)},");
        }
        return sb.ToString();
    }
}
