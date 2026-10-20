using Avalonia.Input;

namespace DotCharter.Tone.UI.Views.PlayMode;

/// <summary>What a performance key does on the rig.</summary>
public enum RigPerformanceKeyAction
{
    None,
    /// <summary>Keys 1–8 (top row or keypad): scene A–H.</summary>
    Scene,
    /// <summary>PageUp: the previous song of the active setlist.</summary>
    PreviousSong,
    /// <summary>PageDown: the next song of the active setlist.</summary>
    NextSong
}

/// <summary>
///     The rig's PERFORMANCE keys, while the rig has the keyboard: 1–8 pick scene
///     A–H and PageUp / PageDown step the active setlist. Pure (no control), so
///     the map is pinned without a window; <c>AmpFxRigView</c> applies it to a
///     key the focused control did not take, never while a text field has the
///     keys (typing "2" into a search must not switch the scene).
/// </summary>
public static class RigPerformanceKeys
{
    public static (RigPerformanceKeyAction Action, int Scene) Map(Key key, KeyModifiers modifiers, bool typing)
    {
        if (typing || modifiers != KeyModifiers.None) return (RigPerformanceKeyAction.None, -1);
        if (key is >= Key.D1 and <= Key.D8) return (RigPerformanceKeyAction.Scene, key - Key.D1);
        if (key is >= Key.NumPad1 and <= Key.NumPad8) return (RigPerformanceKeyAction.Scene, key - Key.NumPad1);
        return key switch
        {
            Key.PageUp => (RigPerformanceKeyAction.PreviousSong, -1),
            Key.PageDown => (RigPerformanceKeyAction.NextSong, -1),
            _ => (RigPerformanceKeyAction.None, -1)
        };
    }
}
