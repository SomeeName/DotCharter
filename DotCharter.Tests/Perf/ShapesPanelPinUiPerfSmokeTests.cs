using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.Presentation.Screens.Editor;
using DotCharter.Services.Editing.ChordShapes;
using DotCharter.Services.Editing.Selection;
using DotCharter.UI;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.Views.Editor;
using Microsoft.Extensions.DependencyInjection;
using Shapes = DotCharter.Tests.Perf.ShapesPanelConstructionUiPerfSmokeTests;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The Chord shapes tab's pins (#1019), built for real through the app's inspector over 30 seeded shapes and laid
///     out at 530 x 370: the Shape page's Pin grip and the list's Pin all grips measure non-zero inside their cards,
///     a click on Pin grip pins the current shape, and a click on Pin all grips pins every unpinned fingered shape and
///     turns the button off with its reason. Captures <c>shapes-pin-*</c> with <c>DOTCHARTER_UI_SWEEP_DUMP=1</c>.
/// </summary>
[Collection("HeadlessUI")]
public sealed class ShapesPanelPinUiPerfSmokeTests
{
    [Fact]
    public Task TheShapesTab_LaysOutItsPins_AndPinGripAndPinAllGripsPin() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var services = App.Services ?? throw new InvalidOperationException("the headless app session did not boot.");
            var state = services.GetRequiredService<ProjectState>();
            var selection = services.GetRequiredService<ISelectionService>();
            var inspector = services.GetRequiredService<InspectorViewModel>();
            var screen = Assert.IsType<ShapesPanelViewModel>(inspector.ChordShapes);
            var previous = (state.ActiveProject, state.ActiveArrangement, state.ActiveLevel, inspector.SelectedTabIndex);
            Window? window = null;
            try
            {
                var arrangement = Shapes.Seed(state, templates: 30);
                selection.ClearSelections();
                Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
                inspector.SelectedTabIndex = InspectorViewModel.ChordShapesTabIndex;
                var view = new InspectorView { DataContext = inspector };
                window = NarrowWidthLayout.NewWindow(530, 370, view);
                window.Show();
                Shapes.Settle(window);
                var tab = view.GetVisualDescendants().OfType<ShapesPanelView>().Single();
                NarrowWidthLayout.Capture(window, "shapes-pin-list");
                Shapes.ShowCard(tab, "ShapesShapeCard");
                Shapes.Settle(window);
                NarrowWidthLayout.Capture(window, "shapes-pin-shape");

                // The Shape page: Pin grip sits in the card and pins the current shape.
                var current = Assert.IsType<ShapePanelRowViewModel>(screen.CurrentRow);
                var pin = NarrowWidthLayout.Named<ToggleButton>(tab, "ShapePin");
                AssertLaidOutInside(pin, NarrowWidthLayout.Named<Control>(tab, "ShapesShapeCard"), "Pin grip");
                Assert.False(pin.IsChecked == true, "an unpinned shape shows Pin grip on");
                var pinnedBefore = arrangement.ChordTemplates.Count(t => t.Pinned);
                Shapes.Click(window, pin);
                Shapes.Settle(window);
                Assert.True(current.IsPinned, "Pin grip did not pin the current shape");
                Assert.Equal(pinnedBefore + 1, arrangement.ChordTemplates.Count(t => t.Pinned));

                // The list: Pin all grips names its count, pins the rest in one step, then is off with its reason.
                Shapes.ShowCard(tab, "ShapesListCard");
                Shapes.Settle(window);
                var all = NarrowWidthLayout.Named<Button>(tab, "ShapesPinAll");
                AssertLaidOutInside(all, NarrowWidthLayout.Named<Control>(tab, "ShapesListCard"), "Pin all grips");
                var left = GripPins.Unpinned(arrangement).Count;
                Assert.True(left > 20, $"{left} unpinned fingered shapes in the fixture");
                Assert.True(all.IsEffectivelyEnabled, "Pin all grips is off with shapes left to pin");
                Assert.Equal(screen.PinAllLabel, all.GetVisualDescendants().OfType<TextBlock>().Single().Text);
                Assert.Contains(left.ToString(System.Globalization.CultureInfo.CurrentCulture), screen.PinAllLabel, StringComparison.Ordinal);
                Shapes.Click(window, all);
                Shapes.Settle(window);
                NarrowWidthLayout.Capture(window, "shapes-pin-after");
                Assert.Empty(GripPins.Unpinned(arrangement));
                Assert.False(all.IsEffectivelyEnabled, "Pin all grips stayed on with nothing left to pin");
                Assert.True(ToolTip.GetShowOnDisabled(all), "the off button hides its reason");
                Assert.Equal(screen.PinAllTip, ToolTip.GetTip(all) as string);
            }
            finally
            {
                window?.Close();
                selection.ClearSelections();
                inspector.SelectedTabIndex = previous.SelectedTabIndex;
                state.ActiveProject = previous.ActiveProject;
                state.ActiveArrangement = previous.ActiveArrangement;
                state.ActiveLevel = previous.ActiveLevel;
            }
        }, CancellationToken.None);

    private static void AssertLaidOutInside(Control control, Control card, string what)
    {
        Assert.True(control.IsEffectivelyVisible, $"{what} is not visible");
        Assert.True(control.Bounds is { Width: > 1, Height: > 1 }, $"{what} measured {control.Bounds}");
        var origin = control.TranslatePoint(default, card)!.Value;
        Assert.True(origin.X >= 0 && origin.X + control.Bounds.Width <= card.Bounds.Width + 0.5,
            $"{what} runs past its card: x {origin.X:0.#} + {control.Bounds.Width:0.#} in {card.Bounds.Width:0.#}");
    }
}
