namespace DotCharter.Services.Infrastructure.Input;

/// <summary>
///     The editor's keybind dispatch table, as far as a settings screen or the
///     Pattern builder needs it: rebuild it after the chords changed, so a rebind
///     takes effect at once, and say who already owns a chord.
/// </summary>
public interface IKeybindDispatch
{
    void RefreshBinds();

    /// <summary>The label of whatever currently owns <paramref name="chord" />, or null when it is free.</summary>
    string? DescribeBinding(KeyChord chord);
}
