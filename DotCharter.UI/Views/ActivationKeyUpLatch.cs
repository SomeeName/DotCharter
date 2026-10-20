using Avalonia.Input;

namespace DotCharter.UI.Views;

/// <summary>
///     Tracks the one key whose KeyUP the window tunnel must swallow.
/// </summary>
/// <remarks>
///     <para>
///         The tunnel claims a global chord on the KeyDOWN, but a focused
///         Button/ToggleButton activates on the Space KEY-UP (Avalonia's default
///         <c>ClickMode.Release</c>), so consuming only the KeyDown leaves the
///         paired KeyUp live and a clicked ribbon-row toggle flips on every
///         play/pause. The tunnel therefore arms this latch and its KeyUp
///         companion consumes the matching release.
///     </para>
///     <para>
///         It is not a bare <c>Key?</c> cleared ONLY by the matching KeyUp,
///         so any way of losing that release would leave it armed forever: press Space
///         to start playback while a ribbon toggle has focus, the play action
///         opens a dialog / the native Vulkan child window takes Win32 focus /
///         the user alt-tabs, and the KeyUp is delivered somewhere else or not
///         at all. The next time ANY Space KeyUp reached the window — e.g. the
///         user legitimately activating a focused Button on the Settings page,
///         whose KeyDown was never consumed — it would be marked handled and the
///         button would not click. One lost click, then it would self-heal. Both
///         escape hatches below close that.
///     </para>
///     <para>
///         Pure and Avalonia-instance-free (<c>Key</c> is a plain enum), so it
///         is unit-tested in the DEFAULT gate — unlike <c>MainWindow</c>'s own
///         routing tests, which need the whole app booted and therefore live in
///         <c>*UiPerfSmokeTests</c>. Same reason <c>GlobalChordGate</c> exists.
///     </para>
/// </remarks>
internal sealed class ActivationKeyUpLatch
{
    private Key? _armed;

    /// <summary>True while a release is pending. Diagnostics/tests only.</summary>
    public bool IsArmed => _armed.HasValue;

    /// <summary>The tunnel consumed <paramref name="key" />'s KeyDown as a
    /// global chord — swallow its release too.</summary>
    public void Arm(Key key) => _armed = key;

    /// <summary>
    ///     Called for every KeyDown the tunnel sees. A DIFFERENT key arriving
    ///     while armed proves the previous release was lost, so the stale arm
    ///     is dropped rather than left to swallow an unrelated later release.
    /// </summary>
    public void NoteKeyDown(Key key)
    {
        if (_armed is { } armed && armed != key) _armed = null;
    }

    /// <summary>
    ///     True when this KeyUp is the release the tunnel owes, consuming the
    ///     arm. Any other key leaves the latch alone — releases interleave
    ///     (Ctrl+Space releases Space then Ctrl).
    /// </summary>
    public bool ShouldSwallow(Key key)
    {
        if (_armed != key) return false;
        _armed = null;
        return true;
    }

    /// <summary>
    ///     Drops any pending arm. Hooked to every routed window's
    ///     <c>Deactivated</c>: once focus leaves, the release will be delivered
    ///     to another top-level (or not at all) and can never arrive here.
    /// </summary>
    public void Clear() => _armed = null;
}
