using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.Tone.UI.Controls.Fire;
using DotCharter.UI.Views.Editor;
using DotCharter.UI.Views.Editor.Inspector;

namespace DotCharter.Tests.Perf;

/// <summary>Lost captures and detached controls end edits; hidden EQ views park their clock;
/// the sheet toolbar fits a floating pane. Real attached controls and pointer input.</summary>
[Collection("HeadlessUI")]
public sealed class EditorUiFaultsUiPerfSmokeTests
{
    [Theory]
    [InlineData("knob", false)]
    [InlineData("stage", false)]
    [InlineData("side", false)]
    [InlineData("knob", true)]
    [InlineData("stage", true)]
    [InlineData("side", true)]
    public Task An_interrupted_drag_cannot_edit_on_a_later_move(string kind, bool detach) =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            Control control = kind switch
            {
                "knob" => new FireKnob { Minimum = 0, Maximum = 1, Value = 0.5 },
                "stage" => new RigMicStage { Width = 200, Height = 200 },
                _ => new RigMicSideView { Width = 300, Height = 150 }
            };
            var host = new Canvas { Children = { control } };
            Canvas.SetLeft(control, 80);
            Canvas.SetTop(control, 80);
            var window = new Window { Width = 600, Height = 450, Content = host };
            window.Show();
            try
            {
                window.UpdateLayout();
                Assert.True(control.Bounds.Width > 1 && control.Bounds.Height > 1);
                IPointer? pointer = null;
                void Grab(object? sender, PointerPressedEventArgs e) => pointer = e.Pointer;
                control.AddHandler(InputElement.PointerPressedEvent, Grab, Avalonia.Interactivity.RoutingStrategies.Bubble, true);
                window.MouseDown(new Point(100, 100), MouseButton.Left);
                Assert.Same(control, pointer!.Captured);
                if (detach)
                {
                    host.Children.Remove(control);
                    host.Children.Add(control);
                    window.UpdateLayout();
                }
                else
                {
                    pointer.Capture(null);
                }
                window.MouseUp(new Point(550, 400), MouseButton.Left);
                var before = Read(control);
                window.MouseMove(kind switch
                {
                    "knob" => new Point(100, 90),
                    "stage" => new Point(180, 180),
                    _ => new Point(280, 180)
                });
                Assert.Equal(before, Read(control));
            }
            finally { window.Close(); }
        }, CancellationToken.None);

    private static double Read(Control control) => control switch
    {
        FireKnob knob => knob.Value,
        RigMicStage stage => stage.Position,
        RigMicSideView side => side.DistanceCm,
        _ => throw new InvalidOperationException()
    };

    [Fact]
    public Task The_eq_clock_tracks_ancestor_visibility_and_reattachment() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var view = new EqModuleView();
            var host = new Border { Child = view };
            var window = new Window { Width = 600, Height = 600, Content = host };
            window.Show();
            try
            {
                window.UpdateLayout();
                Assert.True(view.Bounds.Width > 1 && view.Bounds.Height > 1);
                var timer = (DispatcherTimer)typeof(EqModuleView)
                    .GetField("_spectrumTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
                Assert.True(timer.IsEnabled);
                host.IsVisible = false;
                Assert.False(timer.IsEnabled);
                host.IsVisible = true;
                Assert.True(timer.IsEnabled);
                view.IsVisible = false;
                Assert.False(timer.IsEnabled);
                view.IsVisible = true;
                Assert.True(timer.IsEnabled);
                host.Child = null;
                Assert.False(timer.IsEnabled);
                host.Child = view;
                Assert.True(timer.IsEnabled);
            }
            finally { window.Close(); }
        }, CancellationToken.None);

    [Theory]
    [InlineData(420)]
    [InlineData(640)]
    public Task Sheet_toolbars_fit_without_overlap(double width) =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var view = new SheetMusicPaneView();
            var window = new Window { Width = width, Height = 400, Content = view };
            window.Show();
            try
            {
                NarrowWidthLayout.Settle(window);
                Assert.True(view.Bounds.Width > 1 && view.Bounds.Height > 1);
                NarrowWidthLayout.Capture(window, $"sheet-toolbar-{width}");
                var controls = view.GetVisualDescendants().OfType<Control>()
                    .Where(c => c is Button or ToggleButton or ComboBox)
                    .Where(c => c.IsEffectivelyVisible && !c.GetVisualAncestors().Any(a => a is Button or ToggleButton or ComboBox)).ToList();
                Assert.NotEmpty(controls);
                var boxes = controls.Select(c => new Rect(c.TranslatePoint(default, view)!.Value, c.Bounds.Size)).ToList();
                foreach (var box in boxes)
                    Assert.True(box.Left >= 0 && box.Right <= view.Bounds.Width + 0.5, $"button outside pane: {box}");
                for (var i = 0; i < boxes.Count; i++)
                {
                    for (var j = i + 1; j < boxes.Count; j++)
                        Assert.False(boxes[i].Intersects(boxes[j]), $"toolbar controls overlap: {boxes[i]} / {boxes[j]}");
                }
            }
            finally { window.Close(); }
        }, CancellationToken.None);
}
