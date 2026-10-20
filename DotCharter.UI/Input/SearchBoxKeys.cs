using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace DotCharter.UI.Input;

/// <summary>What a key press inside a search box means.</summary>
public enum SearchBoxKeyAction
{
    /// <summary>Nothing — the box keeps the key.</summary>
    None,

    /// <summary>Empty the box and stay on the screen. Only ever returned while
    /// the box HAS text; an already-empty box lets the key fall through.</summary>
    ClearText,

    /// <summary>Hand the keyboard to the list the box filters.</summary>
    FocusList
}

/// <summary>
///     The keyboard contract shared by the app's two song-search boxes (Play
///     Mode's Song Select and the editor's song browser), as a pure resolver plus
///     one attached property — the same split as
///     <c>ViewModels/PlayMode/GetReadyKeys</c>: the rule is testable without a
///     window, and the two code-behinds hold nothing but the plumbing.
///     <para>
///         <b>Escape is the interesting one, and it needs the SHELL's
///         cooperation.</b> The app has exactly one Escape chain
///         (<c>MainWindowViewModel.HandleEscapeKey</c>) and it is claimed at the
///         WINDOW tunnel (<c>MainWindow.GlobalKeyDownPreview</c>), which — tunnel
///         routing being root → leaf — runs before any handler a view can add.
///         A view-side handler therefore cannot "win" Escape by being closer to
///         the box; the shell has to stand down first. That is what
///         <see cref="ClaimsEscape" /> is for: <c>MainWindow</c> asks it about the
///         focused element and, when a marked search box is holding TEXT, leaves
///         the key alone so the box can empty itself and stay on the screen. An
///         EMPTY box claims nothing, so Escape still navigates back — which is the
///         behaviour that makes "Esc twice leaves the screen" true.
///     </para>
///     <para>
///         Mark a box with <c>input:SearchBoxKeys.ClearOnEscape="True"</c>. The
///         marker is what keeps this scoped: every other <c>TextBox</c> in the app
///         keeps the shell's Escape exactly as before.
///     </para>
/// </summary>
public static class SearchBoxKeys
{
    /// <summary>Marks a <see cref="TextBox" /> as a search box whose Escape empties
    /// it instead of leaving the screen. Read by <see cref="ClaimsEscape" /> from
    /// the window tunnel, so the shell can yield the key to it.</summary>
    public static readonly AttachedProperty<bool> ClearOnEscapeProperty =
        AvaloniaProperty.RegisterAttached<TextBox, bool>("ClearOnEscape", typeof(SearchBoxKeys));

    public static void SetClearOnEscape(TextBox obj, bool value) => obj.SetValue(ClearOnEscapeProperty, value);

    public static bool GetClearOnEscape(TextBox obj) => obj.GetValue(ClearOnEscapeProperty);

    /// <summary>
    ///     What this press means inside a search box. Modified presses mean
    ///     nothing here — Shift+Down is a text selection and Ctrl+Down is a
    ///     caret move, and a search box is still a text box first.
    /// </summary>
    public static SearchBoxKeyAction Resolve(Key key, KeyModifiers modifiers, bool hasText)
    {
        if (modifiers != KeyModifiers.None) return SearchBoxKeyAction.None;
        return key switch
        {
            // An empty box has nothing to clear, so it does NOT claim the key:
            // the shell's Escape chain gets it and navigates back.
            Key.Escape => hasText ? SearchBoxKeyAction.ClearText : SearchBoxKeyAction.None,
            Key.Down => SearchBoxKeyAction.FocusList,
            _ => SearchBoxKeyAction.None
        };
    }

    /// <summary>
    ///     Does the focused element claim Escape for itself? The window tunnel's
    ///     Escape branch stands down when this is true (see
    ///     <c>GlobalChordGate.AllowsEscape</c>) — the two-line adapter over
    ///     <see cref="Resolve" />, so the RULE stays in one testable place.
    /// </summary>
    public static bool ClaimsEscape(object? focused) =>
        focused is TextBox box && GetClearOnEscape(box) &&
        Resolve(Key.Escape, KeyModifiers.None, !string.IsNullOrEmpty(box.Text)) == SearchBoxKeyAction.ClearText;

    /// <summary>
    ///     Which row the list should land on when the box hands the keyboard over:
    ///     whatever was already selected, else the first row. −1 means "nothing to
    ///     move to" — an empty list keeps the caret in the box rather than
    ///     focusing a list with no rows in it.
    /// </summary>
    public static int SelectionForFocusHandover(int selectedIndex, int itemCount)
    {
        if (itemCount <= 0) return -1;
        if (selectedIndex < 0 || selectedIndex >= itemCount) return 0;
        return selectedIndex;
    }

    /// <summary>Is this the "focus the search box" chord? Ctrl+F, exactly — a
    /// surface-local claim, so the chart editor keeps its own Ctrl+F
    /// (<c>KeybindConfig.Toggle3DFingering</c>), which is routed by the shell's
    /// bubble fallback and only while the editor is the visible surface.</summary>
    public static bool IsFocusSearchChord(Key key, KeyModifiers modifiers) =>
        key == Key.F && modifiers == KeyModifiers.Control;
}
