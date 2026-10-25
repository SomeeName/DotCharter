using System.Reflection;
using System.Text.RegularExpressions;
using Avalonia.Controls;

namespace DotCharter.Tests.UI;

/// <summary>
///     0.19 A2 — leak-detection guard. Pane views and custom controls
///     are re-created on float/redock while the services they subscribe to
///     (ProjectState, ISelectionService, the static ChartTheme event) live for
///     the process lifetime: one forgotten <c>-=</c> roots every detached
///     instance forever. A 2026-07 audit of every <c>+=</c> in
///     DotCharter.UI found zero live leaks; this test locks in the pattern
///     that made that true: <b>every class that declares its own
///     OnAttachedToVisualTree must declare its own OnDetachedFromVisualTree</b>
///     (attach-time subscriptions need a detach-time cleanup path, and a base
///     class's detach cleans up the base's attach, not a subclass's).
///     GC-collectibility asserts were tried and rejected: under
///     Avalonia.Headless, transitions/composition plumbing can root a
///     detached control with no subscription bug, making them flaky.
///     Full heap-returns-to-baseline verification needs a live app session
///     (see the manual dock checklist in docs/agents/ui/docking.md).
/// </summary>
public sealed class ViewSubscriptionLeakGuardTests
{
    /// <summary>
    ///     Controls reviewed by hand whose attach override provably takes no
    ///     subscription that outlives the control (no <c>+=</c> on a
    ///     singleton/static). Add a type here ONLY after that review, and say
    ///     why in a comment.
    /// </summary>
    private static readonly HashSet<string> ReviewedAttachOnlyControls = new(StringComparer.Ordinal)
    {
        // Reviewed 2026-09-19: attach BORROWS two references off the enclosing
        // ScreenHost (its scope provider and the screen catalog) and realizes
        // the current tab; it subscribes to nothing — no event, no service —
        // so there is no detach-side cleanup to write. The host owns the scope
        // and the tab host is a child of the host's own screen, so it is never
        // re-parented under another scope.
        "DotCharter.UI.Views.Shell.ScreenTabHost",
        // Reviewed 2026-10-23: the same borrow as ScreenTabHost, for one screen
        // placed as a section of another; it subscribes to nothing.
        "DotCharter.UI.Views.Shell.ScreenSlot"
    };

    /// <summary>
    ///     The floor that keeps the scan below from passing VACUOUSLY. This
    ///     guard is a reflective sweep with a `continue` on its first three
    ///     lines: a change to the type filter, a trimmed assembly, or a
    ///     hierarchy walk that stops early would make it inspect NOTHING and
    ///     still report success. MEASURED 2026-08 on `main` (by flooring at
    ///     9999 and reading the failure): the sweep inspects 18 controls, all of
    ///     which pair the overrides. Floored well under that so ordinary churn
    ///     does not trip it — if the real number ever drops below this, the
    ///     sweep broke, not the codebase.
    /// </summary>
    private const int MinimumControlsExpectedToOverrideAttach = 10;

    /// <summary>
    ///     The app and the rig's UI assembly — the Fire controls and the rig views
    ///     moved to <c>DotCharter.Tone.UI</c> in the 2026-09-26 split, and a sweep of
    ///     the app alone no longer sees them.
    /// </summary>
    private static readonly Assembly[] UiAssemblies =
    [
        typeof(DotCharter.UI.App).Assembly,
        typeof(DotCharter.Tone.UI.Controls.Fire.FireDrawnControl).Assembly
    ];

    [Fact]
    public void Controls_OverridingAttach_AlsoOverrideDetach()
    {
        var offenders = new List<string>();
        var scanned = new List<string>();

        foreach (var type in UiAssemblies.SelectMany(a => a.GetTypes()))
        {
            if (!typeof(Control).IsAssignableFrom(type) || type.IsAbstract) continue;

            var attachLevels = UiHierarchy(type).Where(t => DeclaresOwn(t, "OnAttachedToVisualTree")).ToList();
            if (attachLevels.Count == 0) continue;
            scanned.Add(type.FullName ?? type.Name);

            foreach (var level in attachLevels)
            {
                var name = level.FullName ?? level.Name;
                if (!ReviewedAttachOnlyControls.Contains(name) && !DeclaresOwn(level, "OnDetachedFromVisualTree"))
                    offenders.Add(name);
            }
        }

        Assert.True(scanned.Count >= MinimumControlsExpectedToOverrideAttach,
            $"This guard only inspected {scanned.Count} controls — it is supposed to see at least " +
            $"{MinimumControlsExpectedToOverrideAttach}. Something about the reflective sweep " +
            "(the Control filter, the hierarchy walk, the assembly) stopped finding its subjects, " +
            "which means the pass below is meaningless.");

        Assert.True(offenders.Count == 0,
            "These controls override OnAttachedToVisualTree without overriding " +
            "OnDetachedFromVisualTree — attach-time subscriptions on singleton " +
            "services/static events would leak every float/redock cycle. Either " +
            "add the detach-side cleanup or (after review) allowlist the type: " +
            string.Join(", ", offenders.Distinct(StringComparer.Ordinal)));
    }

    /// <summary>
    ///     A base class's detach cleans up the BASE's attach, not a subclass's: a
    ///     subclass that adds its own attach-time subscription needs its own detach.
    /// </summary>
    [Fact]
    public void AnAttachOverrideIsPairedAtItsOwnLevelOfTheHierarchy()
    {
        Assert.False(DeclaresOwn(typeof(AttachOnlySubclassProbe), "OnDetachedFromVisualTree"));
        Assert.True(DeclaresOwn(typeof(AttachOnlySubclassProbe), "OnAttachedToVisualTree"));
        Assert.True(DeclaresOwn(typeof(PairedBaseProbe), "OnDetachedFromVisualTree"));
        Assert.Equal(
            [typeof(AttachOnlySubclassProbe), typeof(PairedBaseProbe)],
            UiHierarchy(typeof(AttachOnlySubclassProbe), [typeof(PairedBaseProbe).Assembly]));
    }

    /// <summary>
    ///     The hole the reflective test above cannot see. A control can take the
    ///     same attach-time subscription WITHOUT overriding anything — wiring
    ///     <c>AttachedToVisualTree += …</c> from its constructor instead — and
    ///     the reflection sweep looks only at method overrides, so those files
    ///     were never inspected by anything. Five controls use that form today
    ///     (BendEditorControl, AmpFxRigView, VstRackView, MixPopoverView,
    ///     ToolsPopoverView); all five happen to pair, and nothing was making
    ///     them. A pane view re-created on every float/redock that only ever
    ///     subscribes leaks one handler per cycle, silently and forever.
    /// </summary>
    [Fact]
    public void SourceLint_FilesWiringAttachAlsoWireDetach()
    {
        var offenders = new List<string>();
        var scanned = 0;

        foreach (var file in UiSourceFiles())
        {
            var text = UiSourceTree.ReadAllText(file);
            if (!Regex.IsMatch(text, @"\bAttachedToVisualTree\s*\+=")) continue;
            scanned++;
            if (!Regex.IsMatch(text, @"\bDetachedFromVisualTree\s*\+="))
                offenders.Add(Path.GetFileName(file));
        }

        Assert.True(scanned >= 4,
            $"This lint found only {scanned} files wiring AttachedToVisualTree — it is meant to see " +
            "at least four. If the pattern moved, repoint the lint rather than letting it pass over " +
            "an empty set.");

        Assert.True(offenders.Count == 0,
            "These files subscribe to AttachedToVisualTree without ever wiring " +
            "DetachedFromVisualTree, so whatever the attach handler starts or subscribes to is " +
            "never undone — one leak per float/redock cycle, because pane views are RE-CREATED " +
            "on float/redock: " + string.Join(", ", offenders));
    }

    /// <summary>
    ///     The static <c>ChartTheme.ThemeChanged</c> event is
    ///     the sharpest edge of this rule: it is a STATIC event, so a subscribed
    ///     control is rooted for the process, not merely for a service's
    ///     lifetime. Every file that subscribes must also unsubscribe.
    /// </summary>
    [Fact]
    public void SourceLint_EveryChartThemeSubscriberUnsubscribes()
    {
        const string subscribe = @"ChartTheme\.\w+\s*\+=";
        const string unsubscribe = @"ChartTheme\.\w+\s*-=";

        // Reviewed exception: the composition root's own subscription is a
        // PROCESS-lifetime one by design (it captures nothing but the static
        // service provider and repaints the editor on a palette switch). App
        // outlives every control, so there is nothing for it to leak.
        var reviewed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "App.axaml.cs" };

        var offenders = new List<string>();
        var scanned = 0;

        foreach (var file in UiSourceFiles())
        {
            var text = UiSourceTree.ReadAllText(file);
            if (!Regex.IsMatch(text, subscribe)) continue;
            scanned++;
            if (reviewed.Contains(Path.GetFileName(file))) continue;
            if (!Regex.IsMatch(text, unsubscribe))
                offenders.Add(Path.GetFileName(file));
        }

        Assert.True(scanned >= 1,
            "No file was found subscribing to a ChartTheme event — this lint has lost its subject.");

        Assert.True(offenders.Count == 0,
            "These files subscribe to a STATIC ChartTheme event without a matching '-=' anywhere in " +
            "the file. A static event roots every subscriber for the process lifetime, so a pane " +
            "view re-created on float/redock leaks an instance per cycle: " +
            string.Join(", ", offenders));
    }

    private static IEnumerable<string> UiSourceFiles() =>
        UiSourceTree
            .GetFiles(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI"), "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal));

    private static List<Type> UiHierarchy(Type type) => UiHierarchy(type, UiAssemblies);

    // The type and its bases up to (excluding) the Avalonia base classes. A view
    // that inherits BOTH overrides from a base still passes: only a level that
    // declares its own attach is asked for its own detach.
    private static List<Type> UiHierarchy(Type type, IReadOnlyCollection<Assembly> assemblies)
    {
        var levels = new List<Type>();
        for (var t = type; t != null && assemblies.Contains(t.Assembly); t = t.BaseType)
            levels.Add(t);
        return levels;
    }

    private static bool DeclaresOwn(Type type, string methodName) =>
        type.GetMethod(methodName,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly) != null;

    private class PairedBaseProbe : Control
    {
        protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e) => base.OnAttachedToVisualTree(e);
        protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e) => base.OnDetachedFromVisualTree(e);
    }

    private sealed class AttachOnlySubclassProbe : PairedBaseProbe
    {
        protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e) => base.OnAttachedToVisualTree(e);
    }
}
