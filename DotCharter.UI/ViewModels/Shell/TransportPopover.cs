namespace DotCharter.UI.ViewModels.Shell;

/// <summary>
///     The transport bar's grouped popovers that a keybind can open —
///     `Play Menus.dc.html`'s <c>ALT+M</c>, <c>ALT+T</c> and <c>ALT+A</c>.
/// </summary>
/// <remarks>
///     Undo and Redo are deliberately absent. Their flyouts are HOVER previews
///     of what <c>Ctrl+Z</c> will revert; the chord in their footer already runs
///     the action itself, and a keybind that opened the preview instead would
///     make that cap name two different things.
/// </remarks>
public enum TransportPopover
{
    /// <summary>Levels, the live input meter and quick balance.</summary>
    Mix,

    /// <summary>Tuner, drop tuning, MIDI instrument and the guitar VST3 chain.</summary>
    Tools,

    /// <summary>The arrangement picker, ghosts, stacked timelines and vocals.</summary>
    Tracks,

    /// <summary>Playback speed + the digital drop-tune pedal. Joined for the
    /// toolbar OVERFLOW panel (0.59) — unlike the three above it carries no
    /// default chord, so the footer cap rule that named this enum is
    /// untouched.</summary>
    Tempo
}
