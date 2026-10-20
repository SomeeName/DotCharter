// The Get Ready screen's keyboard: the key presses its hosting view publishes.
using DotCharter.Services.Infrastructure.Input;

namespace DotCharter.UI.ViewModels.PlayMode;

public partial class PlayModeViewModel
{
    /// <summary>
    ///     Takes the Get Ready screen's key presses from <paramref name="keys" />,
    ///     which the view hosting the screen publishes into. A screen hosted from
    ///     two places attaches both hosts' sources; <see cref="DetachKeySource" />
    ///     undoes one.
    /// </summary>
    public void AttachKeySource(IKeyGestureSource keys) => keys.KeyPressed += OnGetReadyKey;

    public void DetachKeySource(IKeyGestureSource keys) => keys.KeyPressed -= OnGetReadyKey;

    /// <summary>
    ///     The printed shortcuts, from the SAME list the strip is drawn from
    ///     (<see cref="GetReadyKeys" />). Escape is deliberately not claimed here:
    ///     the app has one Escape chain, in the shell, and claiming it here would
    ///     navigate back twice.
    /// </summary>
    private void OnGetReadyKey(object? sender, ScreenKeyEventArgs e)
    {
        if (e.Handled || !IsTuningCheckScreen) return;

        var action = GetReadyKeys.Resolve(e.Key, e.TextInputFocused);
        if (!GetReadyKeys.IsClaimedByTheScreen(action)) return;

        switch (action)
        {
            case GetReadyKeyAction.Start:
                if (!ConfirmTuningCommand.CanExecute(null)) return;
                ConfirmTuningCommand.Execute(null);
                break;
            case GetReadyKeyAction.Tuner:
                if (!OpenTunerCommand.CanExecute(null)) return;
                OpenTunerCommand.Execute(null);
                break;
            default:
                return;
        }

        e.Handled = true;
    }
}
