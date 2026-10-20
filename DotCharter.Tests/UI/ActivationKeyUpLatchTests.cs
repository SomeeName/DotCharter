using Avalonia.Input;
using DotCharter.UI.Views;

namespace DotCharter.Tests.UI;

/// <summary>
///     The window tunnel claims a global chord on the KeyDOWN, but a focused
///     Button/ToggleButton activates on the Space KEY-UP, so the tunnel also has
///     to swallow the matching release. That latch was a bare <c>Key?</c>
///     cleared ONLY by the matching KeyUp — so every way of losing the release
///     left it armed, and the next unrelated Space release was eaten instead.
///     <para>
///         Pure (<see cref="Key" /> is a plain enum), so unlike <c>MainWindow</c>'s
///         own routing tests — which need the whole app booted and therefore live
///         in <c>*UiPerfSmokeTests</c> — these run in the default gate. Same
///         reason <c>GlobalChordGate</c> exists.
///     </para>
/// </summary>
public sealed class ActivationKeyUpLatchTests
{
    [Fact]
    public void An_unarmed_latch_swallows_nothing()
    {
        var latch = new ActivationKeyUpLatch();

        Assert.False(latch.IsArmed);
        Assert.False(latch.ShouldSwallow(Key.Space));
    }

    [Fact]
    public void The_matching_release_is_swallowed_exactly_once()
    {
        var latch = new ActivationKeyUpLatch();
        latch.Arm(Key.Space);

        Assert.True(latch.ShouldSwallow(Key.Space));
        Assert.False(latch.ShouldSwallow(Key.Space));
        Assert.False(latch.IsArmed);
    }

    [Fact]
    public void A_release_for_another_key_leaves_the_arm_alone()
    {
        // Chord releases interleave: Ctrl+Space releases Space, then Ctrl.
        var latch = new ActivationKeyUpLatch();
        latch.Arm(Key.Space);

        Assert.False(latch.ShouldSwallow(Key.LeftCtrl));
        Assert.True(latch.IsArmed);
        Assert.True(latch.ShouldSwallow(Key.Space));
    }

    [Fact]
    public void Losing_focus_disarms_the_latch()
    {
        // Press Space to start playback while a ribbon toggle has focus, the
        // play action opens a dialog / the native Vulkan child window takes
        // Win32 focus / the user alt-tabs — the release goes elsewhere. Without
        // this, the NEXT Space release reaching the window (a legitimate button
        // activation on the Settings page) was swallowed and the button did not
        // click.
        var latch = new ActivationKeyUpLatch();
        latch.Arm(Key.Space);

        latch.Clear();

        Assert.False(latch.IsArmed);
        Assert.False(latch.ShouldSwallow(Key.Space));
    }

    [Fact]
    public void A_keydown_for_a_different_key_drops_a_stale_arm()
    {
        // Second escape hatch, for focus losses that never raise Deactivated:
        // a different key reaching the tunnel proves the owed release is gone.
        var latch = new ActivationKeyUpLatch();
        latch.Arm(Key.Space);

        latch.NoteKeyDown(Key.A);

        Assert.False(latch.IsArmed);
        Assert.False(latch.ShouldSwallow(Key.Space));
    }

    [Fact]
    public void A_keydown_for_the_same_key_keeps_the_arm()
    {
        // Key repeat while held must not disarm the pending swallow.
        var latch = new ActivationKeyUpLatch();
        latch.Arm(Key.Space);

        latch.NoteKeyDown(Key.Space);

        Assert.True(latch.IsArmed);
        Assert.True(latch.ShouldSwallow(Key.Space));
    }

    [Fact]
    public void Re_arming_replaces_the_pending_key()
    {
        var latch = new ActivationKeyUpLatch();
        latch.Arm(Key.Space);
        latch.Arm(Key.F11);

        Assert.False(latch.ShouldSwallow(Key.Space));
        Assert.True(latch.ShouldSwallow(Key.F11));
    }
}
