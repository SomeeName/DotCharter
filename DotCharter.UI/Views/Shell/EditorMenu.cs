using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Interactivity;

namespace DotCharter.UI.Views.Shell;

/// <summary>
///     The editor's menu bar host — a plain <see cref="Menu" /> in every respect
///     except one: opening a menu with the POINTER does not run through the
///     first header on the way.
/// </summary>
/// <remarks>
///     <para>
///         <b>The bug it fixes is "for some reason File flashes up".</b> Avalonia's
///         <c>Menu.Open()</c> raises <c>MenuBase.OpenedEvent</c>, and
///         <c>DefaultMenuInteractionHandler.MenuOpened</c> answers that event with
///         <c>MoveSelection(NavigationDirection.First, true)</c> — it selects and
///         focuses item 0. That is right for the KEYBOARD entry point (Alt / F10
///         open the bar with nothing selected and the user expects to land
///         somewhere), and wrong for a click, where the handler has already
///         selected the header under the pointer. The recorded transitions for one
///         click on "Tools" were:
///     </para>
///     <code>
///         Menu.SelectedIndex=5  Tools.IsSelected=True      (pointer entered)
///         Menu.IsOpen=True                                  (Menu.Open)
///         Menu.SelectedIndex=0  File.IsSelected=True        (MenuOpened → first)
///         Menu.SelectedIndex=5  File.IsSelected=False       (handler, again)
///         Tools.IsSubMenuOpen=True
///     </code>
///     <para>
///         Fluent's own <c>MenuItem</c> template paints the selected fill as a
///         LOCAL value inside the template, so it beats the app's
///         <c>MenuItem.FireMenuTop</c> background setter — the intermediate state
///         is not a no-op, it is the File header lighting up.
///     </para>
///     <para>
///         <b><see cref="StyleKeyOverride" /> is load-bearing.</b> Control themes
///         are TYPE-KEYED: without it this subclass would find no
///         <c>ControlTheme</c>, get no template, and the whole menu bar would
///         render as nothing (the trap `KeybindCaptureBox` fell into).
///         Overriding the style key makes every <c>Menu</c> rule — Fluent's
///         template, <c>Styles/Chrome.axaml</c>, <c>ShellChromeStyles.axaml</c> —
///         apply exactly as before.
///     </para>
/// </remarks>
public sealed class EditorMenu() : Menu(new EditorMenuInteractionHandler())
{
    protected override Type StyleKeyOverride => typeof(Menu);

    /// <summary>
    ///     Whether a bar-open request should be ignored. Pure, so the decision is
    ///     testable without a window.
    ///     <para>
    ///         <b>Alt is the editor's VIEW modifier</b> (Alt+wheel pans,
    ///         Alt+left-drag pans, Alt+right-click is the instant edit verb), and
    ///         Windows' Alt-activates-the-menu convention made every release of
    ///         that modifier light the File header up — the TopLevel's
    ///         <c>AccessKeyHandler</c> answers a bare Alt KeyUp with
    ///         <c>MainMenu.Open()</c>, and it registered its handlers at TopLevel
    ///         construction, so no later tunnel handler can pre-empt it ("when pressing alt the
    ///         file thing lights up").
    ///     </para>
    ///     <para>
    ///         The pointer is the discriminator: every legitimate open of the BAR
    ///         starts with the pointer on it (a click; submenu hovers go through
    ///         <c>MenuItem</c>, not here), while the access-key path fires with
    ///         the pointer wherever the user is working. Keyboard bar activation
    ///         is deliberately sacrificed — this app's keyboard surface is the
    ///         keybind registry and the command palette.
    ///     </para>
    /// </summary>
    public static bool ShouldIgnoreOpen(bool isPointerOver) => !isPointerOver;

    public override void Open()
    {
        if (ShouldIgnoreOpen(IsPointerOver)) return;
        base.Open();
    }
}

/// <summary>
///     <see cref="DefaultMenuInteractionHandler" /> minus the "always land on the
///     first header when the bar opens" step, which only makes sense when nothing
///     is selected yet.
/// </summary>
public sealed class EditorMenuInteractionHandler : DefaultMenuInteractionHandler
{
    // Avalonia 12.1.1 marks every member of this [Unstable] handler obsolete
    // in its reference assembly; there is no replacement constructor or public
    // select-first hook. Keep this compatibility adapter so keyboard/pointer
    // handling remains Avalonia's. Recheck these two API sites on upgrades.
#pragma warning disable CS0618
    public EditorMenuInteractionHandler() : base(false)
    {
    }
#pragma warning restore CS0618

    /// <summary>
    ///     Whether opening the bar should move the selection to the first header.
    ///     <para>
    ///         Pure so the decision is testable without a menu, a window or a
    ///         platform: <paramref name="selectedIndex" /> is the bar's
    ///         <c>SelectedIndex</c> at the moment
    ///         <c>Opened</c> is raised. A click has already selected the header
    ///         under the pointer (≥ 0); Alt / F10 come through with nothing
    ///         selected (−1) — <c>MenuClosed</c> clears the selection, which is
    ///         what makes that reliable.
    ///     </para>
    /// </summary>
    public static bool ShouldSelectFirstItem(int selectedIndex) => selectedIndex < 0;

#pragma warning disable CS0618, CS0672 // Same unstable API boundary as the constructor above.
    protected override void MenuOpened(object? sender, RoutedEventArgs e)
    {
        if (sender is Menu menu && !ShouldSelectFirstItem(menu.SelectedIndex)) return;
        base.MenuOpened(sender, e);
    }
#pragma warning restore CS0618, CS0672
}
