using DotCharter.UI.Views;

namespace DotCharter.Tests.UI;

/// <summary>
///     Regression tests for UI audit 2026-07-25 finding 6.1: four chords —
///     <c>Ctrl+Shift+P</c>, <c>Escape</c>, <c>F11</c>/<c>Alt+Enter</c> and
///     <c>Ctrl+Space</c> — were claimed by <c>MainWindow.GlobalKeyDownPreview</c>
///     without ever consulting <c>CanRouteGlobalKeybinds()</c>, contradicting
///     <c>AGENTS.md</c>'s "both global routes stand down while a TextBox … or a
///     KeybindCaptureBox has focus".
///
///     The gating rules now live in <see cref="GlobalChordGate" /> as pure
///     predicates precisely so they can be asserted here — <c>MainWindow</c>
///     itself needs the whole app booted, which puts its tests outside the
///     default gate (see <c>PlayPauseSpaceKeyRoutingUiPerfSmokeTests</c>).
/// </summary>
public sealed class GlobalChordGateTests
{
    private static GlobalChordGate.Context Editor(
        bool text = false, bool capture = false, bool palette = false,
        bool dialog = false, bool editor = true, bool searchBoxEscape = false)
        => new(TextInputFocused: text, KeybindCaptureFocused: capture,
            PaletteOpen: palette, HasOpenDialog: dialog, ChartEditorVisible: editor,
            SearchBoxClaimsEscape: searchBoxEscape);

    // ── Rebinding: a focused KeybindCaptureBox must see every chord ──────────
    // The window tunnel runs before the capture box's own tunnel handler
    // (root → leaf) and that handler bails on e.Handled, so anything the tunnel
    // claims is a chord the user can never rebind.

    [Fact]
    public void WhileRebinding_NoGlobalChordIsClaimed()
    {
        var c = Editor(capture: true);

        Assert.False(GlobalChordGate.AllowsPaletteToggle(c));
        Assert.False(GlobalChordGate.AllowsEscape(c));
        Assert.False(GlobalChordGate.AllowsFullScreenToggle(c));
        Assert.False(GlobalChordGate.AllowsLyricTap(c));
        Assert.False(GlobalChordGate.AllowsPlayPause(c));
    }

    [Fact]
    public void WhileRebinding_EvenThePaletteNavigationKeysStandDown() => Assert.False(GlobalChordGate.AllowsPaletteNavigation(Editor(capture: true, palette: true)));

    // ── Text input: typing must stay native ─────────────────────────────────

    /// <summary>Ctrl+Space in the Lyrics inspector's text box performed the
    /// lyric tap instead of typing — the branch had no gate at all, not even
    /// IsChartEditorVisible.</summary>
    [Fact]
    public void TypingInATextBox_DoesNotFireTheLyricTap()
    {
        Assert.False(GlobalChordGate.AllowsLyricTap(Editor(text: true)));
        Assert.True(GlobalChordGate.AllowsLyricTap(Editor()));
    }

    /// <summary>Alt+Enter inserts a newline in a multi-line TextBox; it used to
    /// toggle fullscreen instead.</summary>
    [Fact]
    public void TypingInATextBox_DoesNotToggleFullScreen()
    {
        Assert.False(GlobalChordGate.AllowsFullScreenToggle(Editor(text: true)));
        Assert.True(GlobalChordGate.AllowsFullScreenToggle(Editor()));
    }

    [Fact]
    public void TypingInATextBox_DoesNotOpenTheCommandPalette()
    {
        Assert.False(GlobalChordGate.AllowsPaletteToggle(Editor(text: true)));
        Assert.True(GlobalChordGate.AllowsPaletteToggle(Editor()));
    }

    // ── Deliberate exceptions ───────────────────────────────────────────────

    /// <summary>Esc is NOT gated on text input: nothing else handles it, and
    /// "Esc cancels the dialog I am typing in" is what users expect.</summary>
    [Fact]
    public void Escape_StillFiresWhileTypingInATextBox() => Assert.True(GlobalChordGate.AllowsEscape(Editor(text: true)));

    /// <summary>
    ///     …with exactly one exception: a SONG-SEARCH box that has text in it
    ///     (<c>Input/SearchBoxKeys.ClearOnEscape</c>). There Esc means "empty the
    ///     box", and only the shell can hand it over — the window tunnel runs
    ///     before any handler a view can add (root → leaf), so a view-side
    ///     handler could never out-race it.
    /// </summary>
    [Fact]
    public void Escape_StandsDownForASearchBoxThatHasSomethingToClear()
    {
        Assert.False(GlobalChordGate.AllowsEscape(Editor(text: true, searchBoxEscape: true)));

        // An EMPTY box claims nothing, so Esc still runs the one chain and leaves
        // the screen — which is what makes "Esc, Esc" always get you out.
        Assert.True(GlobalChordGate.AllowsEscape(Editor(text: true)));
    }

    /// <summary>The palette focuses its own search TextBox on open, so gating
    /// its navigation keys on text input would kill its keyboard entirely.</summary>
    [Fact]
    public void PaletteNavigation_SurvivesItsOwnSearchBoxHoldingFocus()
    {
        Assert.True(GlobalChordGate.AllowsPaletteNavigation(Editor(text: true, palette: true)));
        Assert.False(GlobalChordGate.AllowsPaletteNavigation(Editor(text: true)));
    }

    /// <summary>…and for the same reason Ctrl+Shift+P must still CLOSE an open
    /// palette even though its search box holds focus.</summary>
    [Fact]
    public void PaletteToggle_StillClosesAnOpenPalette() => Assert.True(GlobalChordGate.AllowsPaletteToggle(Editor(text: true, palette: true)));

    // ── Surface gating ──────────────────────────────────────────────────────

    [Fact]
    public void EditorOnlyChords_StandDownOutsideTheChartEditor()
    {
        var playMode = Editor(editor: false);

        Assert.False(GlobalChordGate.AllowsLyricTap(playMode));
        Assert.False(GlobalChordGate.AllowsPlayPause(playMode));
        // Esc and fullscreen are shell-wide and must still work in Play Mode.
        Assert.True(GlobalChordGate.AllowsEscape(playMode));
        Assert.True(GlobalChordGate.AllowsFullScreenToggle(playMode));
    }

    [Fact]
    public void EditorChords_StandDownWhileThePaletteIsOpen()
    {
        var open = Editor(palette: true);

        Assert.False(GlobalChordGate.AllowsLyricTap(open));
        Assert.False(GlobalChordGate.AllowsPlayPause(open));
    }

    // ── Modal dialogs (audit finding 6.2) ───────────────────────────────────

    /// <summary>Ctrl+Shift+P does not go through HandleKeyPress, where
    /// HasOpenDialog is checked, so it used to open the palette on top of any
    /// modal dialog.</summary>
    [Fact]
    public void PaletteToggle_IsBlockedByAnOpenDialog() => Assert.False(GlobalChordGate.AllowsPaletteToggle(Editor(dialog: true)));

    /// <summary>…but a palette that is somehow already open must still be
    /// closable, or the user is stuck with two overlays.</summary>
    [Fact]
    public void PaletteToggle_StillClosesAnOpenPalette_EvenWithADialogOpen() => Assert.True(GlobalChordGate.AllowsPaletteToggle(Editor(palette: true, dialog: true)));

    /// <summary>The palette's navigation block sits AHEAD of the Esc branch, so
    /// letting it run while a dialog is open swallows the dialog's
    /// Esc-to-cancel and Enter-to-confirm — an unresponsive dialog with no
    /// visible cause. Esc must reach HandleEscapeKey instead.</summary>
    [Fact]
    public void PaletteNavigation_DoesNotEatAnOpenDialogsKeys()
    {
        var both = Editor(palette: true, dialog: true, text: true);

        Assert.False(GlobalChordGate.AllowsPaletteNavigation(both));
        Assert.True(GlobalChordGate.AllowsEscape(both));
    }
}
