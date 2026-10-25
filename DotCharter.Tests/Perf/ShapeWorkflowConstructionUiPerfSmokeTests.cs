using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.Presentation.Fakes.Editor;
using DotCharter.UI.Views.Dialogs;
using DotCharter.UI.Views.Editor;
using DotCharter.UI.Views.Shell;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The chord-shape workflow mockups (the Shapes panel, the shape editor, Clean up shapes),
///     each fake variant built, attached to a real window, laid out and measured: the panel at
///     the inspector's default size, the two dialogs inside the dialog container that styles
///     them in the app. Each asserts the parts that carry the model measured non-zero: the scope
///     cards, the song strip and the result line; the confirmation bar while a verb waits; the
///     proposal sections that hold something.
/// </summary>
[Collection("HeadlessUI")]
public sealed class ShapeWorkflowConstructionUiPerfSmokeTests
{
    private const double InspectorWidth = 600;
    private const double InspectorHeight = 470;

    public static TheoryData<string> PanelVariants => [.. FakeShapePanelScreen.Variants.Select(v => v.Name)];

    public static TheoryData<string> EditVariants => [.. FakeShapeEditScreen.Variants.Select(v => v.Name)];

    public static TheoryData<string> CleanupVariants => [.. FakeShapeCleanupScreen.Variants.Select(v => v.Name)];

    [Theory]
    [MemberData(nameof(PanelVariants))]
    public Task ThePanel_LaysOutEveryVariant(string variant) =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var screen = FakeShapePanelScreen.Variants.Single(v => v.Name == variant).Create();
            var view = new ShapesPanelView { DataContext = screen };
            WithWindow(view, InspectorWidth, InspectorHeight, () =>
            {
                Assert.True(view.Bounds is { Width: > 1, Height: > 1 }, $"{variant}: the panel laid out as {view.Bounds}");
                Assert.True(Named(view, "ShapesSearch").Bounds.Height > 1, $"{variant}: the search box did not lay out");
                if (screen.HasRows)
                {
                    var rows = view.FindControl<ItemsControl>("ShapesRows")!;
                    var realized = rows.GetRealizedContainers().Count(c => c.Bounds.Height > 1);
                    Assert.True(realized >= Math.Min(3, screen.Rows.Count), $"{variant}: only {realized} rows measured");
                }
                else
                {
                    Assert.True(Named(view, "ShapesEmpty").Bounds.Height > 1, $"{variant}: the empty text did not lay out");
                }

                if (screen.HasSelection)
                    Assert.True(Named(view, "ShapesSelectionBar").Bounds.Height > 1, $"{variant}: the bulk bar did not lay out");
                if (screen.HasPending)
                    Assert.True(Named(view, "ShapesPendingBar").Bounds.Height > 1, $"{variant}: the confirmation bar did not lay out");
                if (screen.IsChoosingMergeTarget)
                    Assert.True(Named(view, "ShapesMergeHint").Bounds.Height > 1, $"{variant}: the merge hint did not lay out");
            });
        }, CancellationToken.None);

    [Theory]
    [MemberData(nameof(EditVariants))]
    public Task TheEditor_LaysOutEveryVariant(string variant) =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var screen = FakeShapeEditScreen.Variants.Single(v => v.Name == variant).Create();
            var view = new ShapeEditDialogView { DataContext = screen };
            WithDialog(view, () =>
            {
                foreach (var name in new[] { "ShapeEditScopes", "ShapeEditStrip", "ShapeEditPreview", "ShapeEditStrings", "ShapeEditResult" })
                    Assert.True(Named(view, name).Bounds is { Width: > 1, Height: > 1 }, $"{variant}: {name} did not lay out");
                var cells = view.FindControl<ItemsControl>("ShapeEditStrip")!.GetRealizedContainers().ToList();
                Assert.Equal(screen.UseBars.Count, cells.Count);
                Assert.All(cells, c => Assert.True(c.Bounds.Width > 0, $"{variant}: a strip cell has no width"));
            });
        }, CancellationToken.None);

    [Theory]
    [MemberData(nameof(CleanupVariants))]
    public Task TheCleanup_LaysOutEveryVariant(string variant) =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var screen = FakeShapeCleanupScreen.Variants.Single(v => v.Name == variant).Create();
            var view = new ShapeCleanupDialogView { DataContext = screen };
            WithDialog(view, () =>
            {
                Assert.True(view.Bounds is { Width: > 1, Height: > 1 }, $"{variant}: the dialog laid out as {view.Bounds}");
                Assert.Equal(screen.HasDuplicates, Named(view, "ShapeCleanupDuplicates").Bounds.Height > 1);
                Assert.Equal(screen.HasNearDuplicates, Named(view, "ShapeCleanupReview").IsEffectivelyVisible);
                Assert.Equal(screen.HasUnused, Named(view, "ShapeCleanupUnused").IsEffectivelyVisible);
            });
        }, CancellationToken.None);

    private static Control Named(Control root, string name) =>
        root.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == name)
        ?? throw new InvalidOperationException($"no control named {name}");

    /// <summary>A dialog is styled by the container the app opens it in, so it is measured there.</summary>
    private static void WithDialog(Control view, Action assert)
    {
        var container = new DialogContainerView();
        container.DialogPanel.Children.Clear();
        container.DialogPanel.Children.Add(view);
        WithWindow(container, 1280, 800, () =>
        {
            Assert.True(view.Bounds is { Width: > 1, Height: > 1 }, $"the dialog laid out as {view.Bounds}");
            assert();
        });
    }

    private static void WithWindow(Control content, double width, double height, Action assert)
    {
        var window = new Window { Width = width, Height = height, Content = content };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            window.Measure(new Size(width, height));
            window.Arrange(new Rect(0, 0, width, height));
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            assert();
        }
        finally
        {
            window.Close();
        }
    }
}
