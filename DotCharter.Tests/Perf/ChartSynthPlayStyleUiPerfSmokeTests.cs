using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.VisualTree;
using DotCharter.UI.ViewModels.Editor.Inspector;
using DotCharter.UI.Views.Editor.Inspector;

namespace DotCharter.Tests.Perf;

/// <summary>
///     Mixer ▸ MIDI synth ▸ Playing style (#160, <see cref="ChartSynthPlayStyleView" />):
///     constructed, attached and laid out at the inspector's narrow width over a
///     real <see cref="ProjectState" /> and <see cref="HistoryService" />. Every row
///     measures and stays inside the view; the gap pickers carry the five note
///     values; unticking a hold reaches the PROJECT through the control and disables
///     its picker; the Bass segment swaps the rows to the bass rules; with no project
///     the rows give way to the line saying why. The Mixer card's host is checked by
///     <c>InspectorConstructionUiPerfSmokeTests</c>.
/// </summary>
[Collection("HeadlessUI")]
public sealed class ChartSynthPlayStyleUiPerfSmokeTests
{
    private static readonly string[] Rows =
    [
        "ChartSynthStyleHeading", "ChartSynthStyleGuitar", "ChartSynthStyleBass", "ChartSynthStyleShapesRing",
        "ChartSynthStyleHoldNotes", "ChartSynthStyleNoteGap", "ChartSynthStyleHoldChords", "ChartSynthStyleChordGap",
        "ChartSynthStyleReset",
    ];

    [Fact]
    public Task TheRows_LayOut_AndEditTheProject() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var state = new ProjectState();
            var project = new ChartProject();
            state.ActiveProject = project;
            using var vm = new ChartSynthPlayStyleViewModel(state, new HistoryService(new FakeAppLogger()), FakeLocalization.English());
            var view = new ChartSynthPlayStyleView { DataContext = vm };
            var window = Show(view, 300, 400);
            try
            {
                AssertMeasured(Find(view, "ChartSynthPlayStyleRoot"), "the playing style");
                foreach (var name in Rows)
                {
                    AssertMeasured(Find(view, name), name);
                    AssertInside(Find(view, name), view, name);
                }
                Assert.False(Find(view, "ChartSynthStyleNoProject").IsEffectivelyVisible);
                foreach (var name in new[] { "ChartSynthStyleNoteGap", "ChartSynthStyleChordGap" })
                {
                    var box = Assert.IsType<ComboBox>(Find(view, name));
                    Assert.Equal(5, box.ItemCount);
                    Assert.Equal(4, Assert.IsType<ChartSynthChoice<int>>(box.SelectedItem).Value);
                }

                // Through the CONTROLS: untick the chord hold, pick 1/16 for single notes.
                Assert.IsType<CheckBox>(Find(view, "ChartSynthStyleHoldChords")).IsChecked = false;
                Assert.IsType<ComboBox>(Find(view, "ChartSynthStyleNoteGap")).SelectedItem =
                    vm.GapChoices.Single(c => c.Value == 16);
                Settle(window);
                Assert.False(project.SynthPlayStyle.Guitar.HoldChordGaps);
                Assert.Equal(16, project.SynthPlayStyle.Guitar.SingleNoteGapDivisor);
                Assert.False(Find(view, "ChartSynthStyleChordGap").IsEnabled, "the chord picker stays live with its hold off");
                Assert.True(Find(view, "ChartSynthStyleReset").IsEnabled);
                Capture(window, "chart-synth-play-style.png");

                // The Bass segment: the rows now show the bass rules, still the defaults.
                Assert.IsType<RadioButton>(Find(view, "ChartSynthStyleBass")).IsChecked = true;
                Settle(window);
                Assert.True(vm.EditingBass);
                Assert.True(Assert.IsType<CheckBox>(Find(view, "ChartSynthStyleHoldChords")).IsChecked);
                Assert.False(Find(view, "ChartSynthStyleReset").IsEnabled);

                // No project: the line saying why, not dead rows.
                state.ActiveProject = null;
                Settle(window);
                Assert.False(Find(view, "ChartSynthStyleRows").IsEffectivelyVisible);
                AssertMeasured(Find(view, "ChartSynthStyleNoProject"), "the no-project line");
            }
            finally
            {
                window.Close();
            }
        }, TestContext.Current.CancellationToken);

    private static Window Show(Control content, double width, double height)
    {
        var window = new Window
        {
            Width = width, Height = height, Content = content,
            Background = new SolidColorBrush(Color.FromRgb(0x08, 0x07, 0x0E)),
        };
        window.Show();
        Settle(window);
        return window;
    }

    private static void Settle(Window window)
    {
        for (var pass = 0; pass < 4; pass++) window.UpdateLayout();
    }

    private static Control Find(Control root, string name) =>
        root.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == name)
        ?? throw new Xunit.Sdk.XunitException($"no control named {name} — the view never realized it");

    private static void AssertMeasured(Control control, string what) =>
        Assert.True(control.Bounds is { Width: > 1, Height: > 1 },
            $"{what} laid out as {control.Bounds} — it rendered nothing");

    private static void AssertInside(Control child, Control ancestor, string what)
    {
        var topLeft = child.TranslatePoint(default, ancestor);
        var bottomRight = child.TranslatePoint(new Point(child.Bounds.Width, child.Bounds.Height), ancestor);
        Assert.True(topLeft.HasValue && bottomRight.HasValue, $"{what} is not under its host");
        Assert.True(topLeft!.Value.X >= -1 && bottomRight!.Value.X <= ancestor.Bounds.Width + 1,
            $"{what} spans x={topLeft.Value.X:F0}..{bottomRight!.Value.X:F0} in a {ancestor.Bounds.Width:F0} px host");
    }

    private static void Capture(Window window, string file)
    {
        var dir = Environment.GetEnvironmentVariable("DOTCHARTER_RIG_CAPTURE_DIR");
        if (!UiSweepRunner.CaptureEnabled || string.IsNullOrWhiteSpace(dir)) return;
        Directory.CreateDirectory(dir);
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(dir, file), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }
}
