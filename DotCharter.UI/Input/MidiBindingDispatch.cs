using DotCharter.Services.Infrastructure.Input;

namespace DotCharter.UI.Input;

/// <summary>
/// The editor's MIDI controller binds: a press from
///     <see cref="IMidiTriggerInput" /> runs the verb Settings ▸ Keybinds bound
///     to it, through <see cref="ChartKeybindHandler.HandleVerb" /> — the same
///     action, dialog block and usage count as the verb's chord. It routes only
///     where a key would (<c>canRoute</c>: the chart editor showing, the palette
///     closed), so a press while Settings is learning a trigger runs nothing.
/// </summary>
public sealed class MidiBindingDispatch : IDisposable
{
    private readonly IMidiTriggerInput _input;
    private readonly Func<KeybindConfig> _table;
    private readonly ChartKeybindHandler _verbs;
    private readonly Func<bool> _canRoute;

    /// <param name="input">The controller's presses, on the UI thread.</param>
    /// <param name="table">The LIVE table: a revert or an import replaces it.</param>
    /// <param name="verbs">The keybind dispatch whose verbs a press runs.</param>
    /// <param name="canRoute">Whether a key press would reach the editor's verbs right now.</param>
    public MidiBindingDispatch(IMidiTriggerInput input, Func<KeybindConfig> table, ChartKeybindHandler verbs,
        Func<bool> canRoute)
    {
        _input = input;
        _table = table;
        _verbs = verbs;
        _canRoute = canRoute;
        _input.Pressed += OnPressed;
    }

    /// <summary>Raised after a bound verb ran, so the window requests a frame the way a key press does.</summary>
    public event Action? Dispatched;

    public void Dispose() => _input.Pressed -= OnPressed;

    private void OnPressed(MidiTrigger trigger)
    {
        if (!_canRoute()) return;
        if (_table().FindMidiVerb(trigger) is not { } verb) return;
        if (_verbs.HandleVerb(verb)) Dispatched?.Invoke();
    }
}
