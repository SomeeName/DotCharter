using DotCharter.Services.Infrastructure.Input;

namespace DotCharter.UI.ViewModels.PlayMode;

/// <summary>What a Get Ready key press does.</summary>
public enum GetReadyKeyAction
{
    None,

    /// <summary>Start the run — <c>ConfirmTuningCommand</c>.</summary>
    Start,

    /// <summary>Open the full tuner — <c>OpenTunerCommand</c>, which the shell
    /// answers by opening the tuner dialog.</summary>
    Tuner,

    /// <summary>Leave the screen. Handled by the SHELL, not by the view: the app
    /// has exactly one Escape chain (<c>MainWindowViewModel.HandleEscapeKey</c>)
    /// and a second consumer here would navigate back twice.</summary>
    Back
}

/// <summary>One printed key hint, and what it really does.</summary>
public sealed record GetReadyKeyHint(ScreenKey Key, GetReadyKeyAction Action, string LabelKey);

/// <summary>
///     The Get Ready footer's key hints — as DATA, so the strip that is PRINTED
///     and the handler that RUNS are the same list.
///
///     <para>A printed shortcut that does nothing is worse than no shortcut at
///     all, and the failure is silent in both directions: a hint whose handler
///     was never written, and a handler whose hint was never drawn. Both halves
///     read <see cref="Hints" />, and the guard walks it.</para>
/// </summary>
public static class GetReadyKeys
{
    public static IReadOnlyList<GetReadyKeyHint> Hints { get; } =
    [
        new(ScreenKey.Enter, GetReadyKeyAction.Start, "PlayMode_GetReady_Hint_Start"),
        new(ScreenKey.T, GetReadyKeyAction.Tuner, "PlayMode_GetReady_Hint_Tuner"),
        new(ScreenKey.Escape, GetReadyKeyAction.Back, "PlayMode_GetReady_Hint_Back")
    ];

    /// <summary>
    ///     What this key press means on the Get Ready screen.
    ///     <paramref name="textInputFocused" /> stands the whole set down: the
    ///     screen carries a combo box and a slider, and a bare "T" has to stay
    ///     typeable wherever typing is what the control is for.
    /// </summary>
    public static GetReadyKeyAction Resolve(ScreenKey key, bool textInputFocused)
    {
        if (textInputFocused) return GetReadyKeyAction.None;
        foreach (var hint in Hints)
        {
            if (hint.Key == key)
                return hint.Action;
        }

        return GetReadyKeyAction.None;
    }

    /// <summary>Does the VIEW claim this action, or does the shell? Escape
    /// belongs to the shell's one Escape chain; everything else is the
    /// screen's.</summary>
    public static bool IsClaimedByTheScreen(GetReadyKeyAction action) =>
        action is GetReadyKeyAction.Start or GetReadyKeyAction.Tuner;
}
