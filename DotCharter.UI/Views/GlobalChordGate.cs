namespace DotCharter.UI.Views;

/// <summary>
/// The gating rules for the window-tunnel keybind route
/// (<c>MainWindow.GlobalKeyDownPreview</c>), pulled out as pure predicates so
/// they are unit-testable — <c>MainWindow</c> itself cannot be constructed
/// without booting the whole app.
///
/// <c>AGENTS.md</c> states that "both global routes stand down while a
/// <c>TextBox</c> … or a <c>KeybindCaptureBox</c> has focus". The bubble
/// fallback honoured that; the tunnel did not — only the play/pause chord
/// called <c>CanRouteGlobalKeybinds()</c>, so <c>Ctrl+Shift+P</c>,
/// <c>Escape</c>, <c>F11</c>/<c>Alt+Enter</c> and <c>Ctrl+Space</c> claimed
/// their key unconditionally. Consequences (UI audit, 6.1): those
/// four chords could never be rebound because a focused
/// <c>KeybindCaptureBox</c> never saw them; <c>Ctrl+Space</c> performed the
/// lyric tap while the user typed in the Lyrics inspector; and
/// <c>Alt+Enter</c> went fullscreen instead of inserting a newline in a
/// multi-line <c>TextBox</c>.
/// </summary>
internal static class GlobalChordGate
{
    /// <summary>The focus/shell state a global chord is evaluated against.</summary>
    /// <param name="TextInputFocused">A <c>TextBox</c> (incl. the inner editors of
    /// NumericUpDown/AutoCompleteBox) owns the keyboard.</param>
    /// <param name="KeybindCaptureFocused">A <c>KeybindCaptureBox</c> is armed for
    /// a rebind.</param>
    /// <param name="PaletteOpen">The command palette overlay is showing.</param>
    /// <param name="HasOpenDialog">A modal dialog is showing.</param>
    /// <param name="ChartEditorVisible">The chart editor is the active surface.</param>
    /// <param name="LaunchScreenVisible">Play Mode's LAUNCH screen — the app's
    /// first surface — is showing.</param>
    /// <param name="ActivatableFocused">A <c>Button</c> (and therefore also a
    /// ToggleButton / RadioButton / CheckBox) owns the keyboard, so Enter is
    /// already that control's activation key.</param>
    /// <param name="SearchBoxClaimsEscape">A marked search box
    /// (<c>Input/SearchBoxKeys.ClearOnEscape</c>) owns the keyboard AND has text
    /// in it, so Escape means "empty the box", not "leave the screen".</param>
    internal readonly record struct Context(
        bool TextInputFocused,
        bool KeybindCaptureFocused,
        bool PaletteOpen,
        bool HasOpenDialog,
        bool ChartEditorVisible,
        bool LaunchScreenVisible = false,
        bool ActivatableFocused = false,
        bool SearchBoxClaimsEscape = false);

    /// <summary>
    /// True while a keybind capture box owns the keyboard. NOTHING may be
    /// claimed at the tunnel then — not even Esc, which the capture box uses as
    /// "clear this binding". The tunnel runs before the box's own tunnel
    /// handler (root → leaf) and that handler bails on <c>e.Handled</c>, so any
    /// chord the tunnel claims is a chord the user can never rebind.
    /// </summary>
    internal static bool IsRebinding(in Context c) => c.KeybindCaptureFocused;

    /// <summary>
    /// The palette's own navigation keys (Esc/Enter/Up/Down) are handled at the
    /// tunnel so they win over whatever holds focus. They must NOT be gated on
    /// text input: opening the palette focuses its search <c>TextBox</c>, so a
    /// text-input gate would kill its keyboard entirely. Rebinding still wins —
    /// and so does a modal dialog: this block sits AHEAD of the Esc branch, so
    /// letting it run while a dialog is open swallows the dialog's
    /// Esc-to-cancel and Enter-to-confirm (with Enter running whatever the
    /// palette had selected, behind the dialog).
    /// </summary>
    internal static bool AllowsPaletteNavigation(in Context c) =>
        c.PaletteOpen && !IsRebinding(c) && !c.HasOpenDialog;

    /// <summary>
    /// Ctrl+Shift+P. Blocked while a modal dialog is open: this chord does not
    /// go through <c>HandleKeyPress</c>, where <c>HasOpenDialog</c> is checked,
    /// so it would open the palette on top of (most likely behind — the
    /// dialog container is the topmost overlay) any modal dialog, which then
    /// loses its keys to the navigation block above. The user sees an
    /// unresponsive dialog with no visible cause. The text-input gate is
    /// waived while the palette is already open — the only text input that can
    /// hold focus then is the palette's own search box, and the chord must
    /// still close it.
    /// </summary>
    internal static bool AllowsPaletteToggle(in Context c) =>
        !IsRebinding(c) && (c.PaletteOpen || (!c.TextInputFocused && !c.HasOpenDialog));

    /// <summary>
    /// Escape. Deliberately NOT gated on text input in general: Esc has no native
    /// meaning inside a <c>TextBox</c> and "Esc cancels the dialog I am typing in"
    /// is the behaviour users expect — <c>HandleEscapeKey</c>'s priority chain
    /// (dialog → pause → Play Mode back → clear selection) is the only thing
    /// that handles it at all.
    /// <para>
    /// Two things stand it down. A rebind, for the reason above. And a SEARCH BOX
    /// WITH TEXT IN IT (<c>Input/SearchBoxKeys</c>): there, Esc means "empty the
    /// box", and the shell has to yield because the window tunnel runs before any
    /// handler a view can add (root → leaf) — a view-side handler could never win
    /// it otherwise. The stand-down is narrow on purpose: the box claims the key
    /// only while it has something to clear, so Esc on an EMPTY box still runs the
    /// one chain and leaves the screen.
    /// </para>
    /// </summary>
    internal static bool AllowsEscape(in Context c) => !IsRebinding(c) && !c.SearchBoxClaimsEscape;

    /// <summary>F11 / Alt+Enter. Alt+Enter inserts a newline in a multi-line
    /// TextBox, so a focused text input keeps it.</summary>
    internal static bool AllowsFullScreenToggle(in Context c) =>
        !IsRebinding(c) && !c.TextInputFocused;

    /// <summary>Ctrl+Space (lyric tap). An editor action, so it needs the editor
    /// surface — and it must not fire while the user types in the Lyrics
    /// inspector's own text box, which is exactly where it was firing.</summary>
    internal static bool AllowsLyricTap(in Context c) =>
        !IsRebinding(c) && !c.TextInputFocused && c.ChartEditorVisible && !c.PaletteOpen;

    /// <summary>The play/pause chord. Already correct before the audit; stated
    /// here so all five rules read side by side.</summary>
    internal static bool AllowsPlayPause(in Context c) =>
        !IsRebinding(c) && !c.TextInputFocused && c.ChartEditorVisible && !c.PaletteOpen;

    /// <summary>
    ///     The launch screen's own single-key shortcuts — bare <c>Enter</c>
    ///     (START) and bare <c>S</c> (SETTINGS), both printed in that screen's
    ///     footer. They are screen-scoped, so they need the launch screen to be
    ///     showing, and they are BARE letters, so a focused text input keeps
    ///     them: the screen's own new-profile and guest-name fields would
    ///     otherwise open Settings on every "s" the player types.
    ///     <para>
    ///         Esc is deliberately NOT here — it stays on the one Esc route
    ///         (<c>MainWindowViewModel.HandleEscapeKey</c>), whose priority chain
    ///         is what makes "close the open panel first, then quit" work.
    ///     </para>
    /// </summary>
    internal static bool AllowsLaunchShortcut(in Context c) =>
        !IsRebinding(c) && !c.TextInputFocused && !c.HasOpenDialog && !c.PaletteOpen &&
        c.LaunchScreenVisible;

    /// <summary>
    ///     Bare <c>Enter</c> on the launch screen = START. Everything
    ///     <see cref="AllowsLaunchShortcut" /> demands, plus: a focused BUTTON
    ///     keeps its own Enter. The screen is arrow-key navigable, so tabbing to
    ///     "Guest" or to a profile row and pressing Enter has to activate THAT
    ///     row — a tunnel that claimed Enter unconditionally would make every
    ///     control on the screen do the same thing, which is the opposite of
    ///     navigable.
    /// </summary>
    internal static bool AllowsLaunchStart(in Context c) =>
        AllowsLaunchShortcut(c) && !c.ActivatableFocused;
}
