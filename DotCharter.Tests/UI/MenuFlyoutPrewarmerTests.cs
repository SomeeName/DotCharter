using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using DotCharter.UI.Views.Shell;

namespace DotCharter.Tests.UI;

/// <summary>
///     Pins the contract of <see cref="MenuFlyoutPrewarmer" /> and of
///     <see cref="EditorMenu" /> — the two halves of "opening a top toolbar menu
///     lags 50-100 ms, and for some reason File flashes up".
/// </summary>
/// <remarks>
///     <para>
///         <b>Structural, never timed.</b> The win is measured by
///         <c>LocalOnly_MenuFlyoutProbe</c> (headless, JIT included): first open
///         File 67-104 ms / Edit 59-82 / View 39-64 / Tools 50-60 / Tempo Map
///         28-40 / Vocals 30-69 / Help 24-29 / Arrangements 19-31, against 7-27 ms
///         once the rows are realized and 2-6 ms for every open after that. What
///         is asserted here is what can break SILENTLY: that nothing is opened to
///         achieve it, and that the shell still asks for it.
///     </para>
///     <para>
///         <b>The behavioural half is in
///         <c>Perf/MenuFlyoutPrewarmUiPerfSmokeTests</c>, not here, and that is
///         forced.</b> Giving a <c>MenuItem</c> children goes through
///         <c>ItemCollection.Add → ItemsSourceView.SetSource →
///         CollectionChangedEventManager.AddListener</c>, which calls
///         <c>Dispatcher.VerifyAccess()</c>. In an isolated run the test thread
///         happens to own the dispatcher and it passes; in the full gate another
///         test has already claimed it and every one of them throws "the calling
///         thread cannot access this object". Same trap as
///         <c>HiddenSubtreePrewarmerTests.Realize_leaves_the_IsVisible_binding_in_charge</c>:
///         pin what is order-independent here, run the rest on the dispatcher.
///     </para>
/// </remarks>
public sealed class MenuFlyoutPrewarmerTests
{
    /// <summary>
    ///     The window has no size until it is shown, and an infinite constraint
    ///     is not the layout the popup will really run — either way the prewarm
    ///     must decline rather than cache a measure that has to be redone.
    /// </summary>
    [Theory]
    [InlineData(600, 900, true)]
    [InlineData(0, 900, false)]
    [InlineData(600, 0, false)]
    [InlineData(double.NaN, 900, false)]
    [InlineData(600, double.NaN, false)]
    [InlineData(double.PositiveInfinity, 900, false)]
    [InlineData(600, double.PositiveInfinity, false)]
    public void A_constraint_is_only_worth_measuring_against_when_it_is_finite_and_real(
        double width, double height, bool expected) =>
        Assert.Equal(expected, MenuFlyoutPrewarmer.CanRealizeInto(new Size(width, height)));

    /// <summary>
    ///     <b>The prewarm must never open anything.</b> Opening a popup to warm it
    ///     is the flicker this exists to remove, it steals focus from whatever the
    ///     user is doing, and it deadlocks <c>Avalonia.Headless</c>. It may not
    ///     reach for <c>IsVisible</c> either, for the reason
    ///     <see cref="HiddenSubtreePrewarmer" /> may not: on every real surface
    ///     that property is owned by a binding.
    /// </summary>
    [Fact]
    public void The_prewarm_source_never_opens_a_menu_or_writes_visibility()
    {
        var code = string.Join('\n', UiSourceTree.ReadAllLines(UiSourceTree.Resolve(Path.Combine(
                TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Shell", "MenuFlyoutPrewarmer.cs")))
            .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        string[] forbidden =
        [
            "IsSubMenuOpen =", "IsSubMenuOpen=", "SetValue(MenuItem.IsSubMenuOpenProperty",
            "IsOpen =", "IsOpen=", ".Open()",
            "IsVisible =", "IsVisible=", "SetValue(Visual.IsVisibleProperty", "Focus("
        ];

        var offenders = forbidden.Where(w => code.Contains(w, StringComparison.Ordinal)).ToList();

        Assert.True(offenders.Count == 0,
            "MenuFlyoutPrewarmer opens a menu, moves focus or writes IsVisible (" +
            string.Join(", ", offenders) + "). It must realize rows by MEASURING them and nothing else.");
    }

    /// <summary>
    ///     The shell has to ASK for the menu prewarm, and it has to ask after the
    ///     first frame like every other prewarm. A helper nobody calls is a silent
    ///     no-op that reads as "the fix did not work".
    /// </summary>
    [Fact]
    public void The_shell_drains_the_menu_flyouts_after_its_hidden_surfaces()
    {
        var src = MainWindowCodeBehind();

        Assert.Contains("ScheduleMenuFlyoutPrewarm(() =>", src, StringComparison.Ordinal);
        Assert.Contains("MenuFlyoutPrewarmer.ScheduleDrain", src, StringComparison.Ordinal);

        // Inside a hidden-surface drain's callback, which is what puts it after
        // the first frame AND keeps DOTCHARTER_NO_SURFACE_PREWARM=1 honest: that
        // switch returns before any drain is ever scheduled. Since 0.55 the drain
        // it hangs off is the SHELL CHROME's — the dropdowns come before the
        // dialogs and screens, not after them.
        var drain = src.IndexOf("HiddenSubtreePrewarmer.ScheduleDrain", StringComparison.Ordinal);
        var call = src.IndexOf("ScheduleMenuFlyoutPrewarm(() =>", StringComparison.Ordinal);
        Assert.True(drain > 0 && call > drain,
            "the menu prewarm no longer runs from the hidden-surface drain — it would either " +
            "delay the first frame or escape the DOTCHARTER_NO_SURFACE_PREWARM A/B switch");
    }

    /// <summary>
    ///     <c>TopLevelMenus()</c> resolves the bar BY NAME, so a rename in the
    ///     markup is a silent no-op: the list comes back empty and every dropdown
    ///     quietly pays its first open again.
    /// </summary>
    [Fact]
    public void The_menu_bar_is_found_by_the_name_the_markup_declares()
    {
        var code = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(
            TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Shell", "MainMenuView.axaml.cs")));
        var markup = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(
            TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Shell", "MainMenuView.axaml")));

        Assert.Contains("FindControl<EditorMenu>(\"EditorMenuBar\")", code, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"EditorMenuBar\"", markup, StringComparison.Ordinal);
    }

    // ---- the File flash ----------------------------------------------------

    /// <summary>
    ///     <b>The menu bar must be an <see cref="EditorMenu" />.</b> With a plain
    ///     <c>Menu</c>, Avalonia's <c>DefaultMenuInteractionHandler.MenuOpened</c>
    ///     answers the bar's <c>Opened</c> event by selecting item 0, so a click
    ///     on ANY header ran through File on the way — recorded transitions for
    ///     one click on Tools were <c>Tools.IsSelected=True → File.IsSelected=True
    ///     → Tools.IsSelected=True → Tools.IsSubMenuOpen=True</c>.
    /// </summary>
    [Fact]
    public void The_bar_uses_the_menu_that_does_not_select_the_first_header()
    {
        var markup = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(
            TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Shell", "MainMenuView.axaml")));

        Assert.Contains("<shell:EditorMenu", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("<Menu ", markup, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-1, true)]   // Alt / F10: nothing selected, land on the first header
    [InlineData(0, false)]   // a click already selected the header under the pointer
    [InlineData(5, false)]
    public void The_first_header_is_selected_only_when_nothing_else_is(int selectedIndex, bool expected) =>
        Assert.Equal(expected, EditorMenuInteractionHandler.ShouldSelectFirstItem(selectedIndex));

    // ---- the Alt flash -----------------------------------------------------

    /// <summary>
    ///     <b>Releasing Alt must not light the menu bar.</b> Alt is the editor's
    ///     VIEW modifier (Alt+wheel pan, Alt+left-drag pan, Alt+right-click
    ///     instant verbs — owner, 2026-08-11), and the TopLevel's
    ///     <c>AccessKeyHandler</c> answers a bare Alt KeyUp with
    ///     <c>MainMenu.Open()</c>. Its handlers register at TopLevel construction,
    ///     before any app handler can, so the suppression lives in
    ///     <see cref="EditorMenu" />'s own <c>Open()</c>: a bar-open with the
    ///     pointer elsewhere is the access-key path and is ignored; a click on
    ///     the bar has the pointer on it and proceeds.
    /// </summary>
    [Theory]
    [InlineData(false, true)]  // access-key path: pointer elsewhere → ignored
    [InlineData(true, false)]  // click on the bar: pointer over it → opens
    public void The_bar_ignores_an_open_that_did_not_come_from_the_pointer(bool pointerOver, bool ignored) =>
        Assert.Equal(ignored, EditorMenu.ShouldIgnoreOpen(pointerOver));

    /// <summary>The override only helps if <c>Open()</c> actually consults the
    /// predicate — pin the wiring the same way the router lint pins its call
    /// site.</summary>
    [Fact]
    public void The_bars_open_routes_through_the_predicate()
    {
        var source = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(
            TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Shell", "EditorMenu.cs")));

        Assert.Contains("public override void Open()", source, StringComparison.Ordinal);
        Assert.Contains("ShouldIgnoreOpen(IsPointerOver)", source, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Control themes are TYPE-KEYED: without this override the subclass finds
    ///     no <c>ControlTheme</c>, gets no template, and the whole menu bar renders
    ///     as nothing — the fault <c>KeybindCaptureBox</c> shipped in 0.39.1, which
    ///     is invisible three ways over.
    /// </summary>
    [Fact]
    public void The_editor_menu_is_styled_as_a_plain_Menu()
    {
        var property = typeof(EditorMenu).GetProperty(
            "StyleKeyOverride", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.True(property != null, "EditorMenu no longer overrides StyleKeyOverride");

        Assert.Equal(typeof(Menu), property!.GetValue(new EditorMenu()));
    }

    /// <summary>The shell code-behind is SPLIT across <c>MainWindow.axaml.cs</c>,
    /// its <c>MainWindow.*.cs</c> partials AND the <c>MainWindow*</c> collaborators
    /// extracted out of them (<c>Views/Shell/MainWindowPrewarm.cs</c> and friends) — read them all, or a lint
    /// silently stops looking at the half it cares about.</summary>
    private static string MainWindowCodeBehind() =>
        string.Join('\n', UiSourceTree
            .GetFiles(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views")), "MainWindow*.cs",
                SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(UiSourceTree.ReadAllText));
}
