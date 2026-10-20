using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.VisualTree;
using DotCharter.Presentation.Fakes.Dialogs;
using DotCharter.UI.Views.Dialogs;
using DotCharter.UI.Views.Shell;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The Tone Designer's "from tone rig" review (<c>RigToneImportReviewPanel</c>), laid out over the
///     real dialog in the real dialog host at 1280x720: the card, its chain rows, its losses and its
///     two verbs MEASURE and sit inside the window (a view whose styles failed still arranges, at
///     zero size), and the designer's "From tone rig…" chip is on screen and bound. With no review
///     open there is no overlay at all.
/// </summary>
[Collection("HeadlessUI")]
public sealed class RigToneImportReviewUiPerfSmokeTests
{
    private const double HostWidth = 1280;
    private const double HostHeight = 720;

    [Fact]
    public async Task AnOpenReview_MeasuresItsChainLossesAndVerbs_InsideTheWindow()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var screen = FakeToneDesignerScreen.Default().WithRigImportOpen();
            var review = screen.RigImport!;
            var dialog = new ToneDesignerDialogView { DataContext = screen };

            WithLaidOutDialog(dialog, () =>
            {
                var card = Named<Border>(dialog, "RigImportCard");
                AssertMeasured(card);
                var inWindow = new Rect(0, 0, HostWidth, HostHeight);
                Assert.True(inWindow.Contains(Bounds(card, dialog)), $"the review card {Bounds(card, dialog)} leaves the {HostWidth}x{HostHeight} window");

                var rows = Named<ItemsControl>(dialog, "RigImportRows").GetVisualDescendants().OfType<CheckBox>().ToList();
                Assert.Equal(review.Rows.Count, rows.Count);
                foreach (var box in rows)
                {
                    AssertMeasured(box);
                    Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(box)), "a row's tick has no accessible name");
                }

                Assert.Equal(review.Rows.Count(r => r.CanToggle), rows.Count(b => b.IsEffectivelyEnabled));
                var losses = Named<ItemsControl>(dialog, "RigImportLosses").GetVisualDescendants().OfType<TextBlock>().ToList();
                Assert.Equal(review.Losses, losses.Select(t => t.Text));
                foreach (var loss in losses) AssertMeasured(loss);

                foreach (var verb in new[] { "RigImportAccept", "RigImportCancel", "RigImportAsNew", "RigImportReplace" })
                {
                    var control = Named<Control>(dialog, verb);
                    AssertMeasured(control);
                    Assert.True(Bounds(control, dialog).Bottom <= Bounds(card, dialog).Bottom + 0.5, $"{verb} spills out of the review card");
                }

                Assert.False(Named<Border>(dialog, "RigImportRefused").IsEffectivelyVisible);
                Assert.False(string.IsNullOrWhiteSpace(Named<TextBlock>(dialog, "RigImportSummary").Text));
            });
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task WithNoReview_ThereIsNoOverlay_AndTheChipIsBound()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var screen = FakeToneDesignerScreen.Default();
            var dialog = new ToneDesignerDialogView { DataContext = screen };

            WithLaidOutDialog(dialog, () =>
            {
                Assert.DoesNotContain(dialog.GetVisualDescendants(), v => v is Control { Name: "RigImportOverlay" });
                var chip = Named<Button>(dialog, "ImportFromToneRigChip");
                AssertMeasured(chip);
                Assert.Same(screen.ImportFromToneRigCommand, chip.Command);
                Assert.NotNull(ToolTip.GetTip(chip));
            });
        }, TestContext.Current.CancellationToken);
    }

    private static void AssertMeasured(Control control)
        => Assert.True(control.IsEffectivelyVisible && control.Bounds.Width > 1 && control.Bounds.Height > 1,
            $"{control.Name ?? control.GetType().Name} laid out {control.Bounds} — it rendered nothing");

    private static Rect Bounds(Control control, Visual root)
        => new(control.TranslatePoint(new Point(0, 0), root) ?? default, control.Bounds.Size);

    private static T Named<T>(Control root, string name) where T : Control
    {
        var control = root.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name);
        Assert.True(control is not null, $"the Tone Designer has no {name}");
        return control!;
    }

    /// <summary>Hosted in a real <see cref="DialogContainerView" />, where the dialog grammar's styles live.</summary>
    private static void WithLaidOutDialog(Control dialog, Action assert)
    {
        var host = new DialogContainerView();
        host.DialogPanel.Children.Clear();
        host.DialogPanel.Children.Add(dialog);
        var window = new Window { Width = HostWidth, Height = HostHeight, Content = host };
        window.Show();
        try
        {
            host.Measure(new Size(HostWidth, HostHeight));
            host.Arrange(new Rect(0, 0, HostWidth, HostHeight));
            assert();
        }
        finally
        {
            window.Close();
        }
    }
}
