using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using DotCharter.Tone.UI.Controls.Dsp;
using DotCharter.Tone.UI.Controls.Fire;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The tone-rig design's knob gestures beyond the drag: type a value
///     (Enter applies, Escape cancels), and the wheel turns only a FOCUSED knob
///     so an unfocused one lets the editor scroll. Real headless input on a
///     real attached control. Runs in the headless suite (own process), not in
///     the default gate.
/// </summary>
[Collection("HeadlessUI")]
public sealed class FireKnobEntryUiPerfSmokeTests
{
    private static (Window Window, FireKnob Knob) Host(bool tenScale)
    {
        var knob = new FireKnob
        {
            Label = "Drive", Minimum = 0, Maximum = 1, Value = 0.5, HardwareStyle = true, TenScale = tenScale
        };
        var canvas = new Canvas { Children = { knob } };
        Canvas.SetLeft(knob, 100);
        Canvas.SetTop(knob, 100);
        var window = new Window { Width = 600, Height = 500, Content = canvas };
        window.Show();
        window.UpdateLayout();
        return (window, knob);
    }

    [Fact]
    public async Task ATypedValue_IsReadInTheReadoutsDisplaySpace_AndEnterApplies()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var (window, knob) = Host(tenScale: true);
            try
            {
                Assert.False(knob.IsEditing);
                knob.BeginEdit();
                Assert.True(knob.IsEditing);
                window.UpdateLayout();
                var box = Assert.Single(knob.GetVisualDescendants().OfType<TextBox>());
                Assert.True(box.IsVisible && box.Bounds.Width > 1 && box.Bounds.Height > 1, "the entry box did not lay out");
                Assert.Equal("5", box.Text); // 0.5 on a ten-scale readout

                knob.EditorText = "7";
                window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
                Assert.False(knob.IsEditing);
                Assert.Equal(0.7, knob.Value, 3);
                Assert.True(RigArtworkStore.Get(RigArtwork.KnobCap) != null, "the knob cap bitmap did not decode");
            }
            finally { window.Content = null; window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task EscapeCancels_AndNonsenseChangesNothing()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var (window, knob) = Host(tenScale: false);
            try
            {
                knob.BeginEdit();
                knob.EditorText = "0.9";
                window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                Assert.False(knob.IsEditing);
                Assert.Equal(0.5, knob.Value, 3);

                knob.BeginEdit();
                knob.EditorText = "banana";
                Assert.False(knob.CommitEdit());
                Assert.Equal(0.5, knob.Value, 3);

                // Typed values clamp to the parameter's range, like every gesture.
                knob.BeginEdit();
                knob.EditorText = "42";
                Assert.True(knob.CommitEdit());
                Assert.Equal(1.0, knob.Value, 3);
            }
            finally { window.Content = null; window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task PressingTheReadout_OpensTheEditor_WhereTheDialWouldDrag()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var (window, knob) = Host(tenScale: false);
            try
            {
                // The readout is the bottom line of the cell.
                var readout = knob.TranslatePoint(new Point(knob.Bounds.Width / 2, knob.Bounds.Height - 6), window)!.Value;
                window.MouseDown(readout, MouseButton.Left);
                window.MouseUp(readout, MouseButton.Left);
                Assert.True(knob.IsEditing, "a press on the readout should start typing");
                knob.CancelEdit();

                var dial = knob.TranslatePoint(new Point(knob.Bounds.Width / 2, 60), window)!.Value;
                window.MouseDown(dial, MouseButton.Left);
                window.MouseMove(dial - new Vector(0, 85));
                window.MouseUp(dial - new Vector(0, 85), MouseButton.Left);
                Assert.False(knob.IsEditing);
                Assert.True(knob.Value > 0.5, "a press on the dial should drag");
            }
            finally { window.Content = null; window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task TheWheel_TurnsOnlyAFocusedKnob()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var (window, knob) = Host(tenScale: false);
            try
            {
                var dial = knob.TranslatePoint(new Point(knob.Bounds.Width / 2, 60), window)!.Value;
                Assert.False(knob.IsFocused);
                window.MouseWheel(dial, new Vector(0, 1));
                Assert.Equal(0.5, knob.Value, 3);

                knob.Focus();
                Assert.True(knob.IsFocused);
                window.MouseWheel(dial, new Vector(0, 1));
                Assert.True(knob.Value > 0.5, "a focused knob takes the wheel");

                var coarse = knob.Value - 0.5;
                window.MouseWheel(dial, new Vector(0, 1), RawInputModifiers.Shift);
                var fine = knob.Value - 0.5 - coarse;
                Assert.True(fine > 0 && fine < coarse, $"Shift should be the finer step ({fine} vs {coarse})");
            }
            finally { window.Content = null; window.Close(); }
        }, CancellationToken.None);
    }
}
