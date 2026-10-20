using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.UI.Views.Shell;

namespace DotCharter.Tests.Perf;

/// <summary>
///     A popover must LOOK the same on its second open as on its first.
/// </summary>
/// <remarks>
///     <para>
///         The fault this exists for shipped and reached the owner: closing a
///         flyout disposes its popup host, which detaches the presenter from the
///         visual tree — and a re-heat that then styles, templates and measures
///         that detached subtree resolves every <c>DynamicResource</c> against
///         NOTHING. The values it caches are fallbacks, and they stick, so the
///         next open drew unstyled headings, plain rows and sliders with no
///         gradient.
///     </para>
///     <para>
///         The existing re-heat guard asserted <c>IsMeasureValid</c> and
///         <c>IsArrangeValid</c> and passed the whole time. This repo's own rule
///         says why: a view whose styles failed still arranges. Layout validity
///         and correct appearance are different questions, and only the second
///         one is what the user sees.
///     </para>
/// </remarks>
[Collection("HeadlessUI")]
public sealed class PopoverRestyleUiPerfSmokeTests
{
    private const double W = 1600;
    private const double H = 1000;
    private static readonly Size Slot = new(W, H);

    [Fact]
    public Task A_reopened_popover_resolves_the_same_brushes_as_its_first_open() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var view = new ToolbarView();
            var window = new Window { Width = W, Height = H, Content = view };
            window.Show();
            view.Measure(Slot);
            view.Arrange(new Rect(0, 0, W, H));
            view.UpdateLayout();

            try
            {
                var popovers = MenuFlyoutPrewarmer.FlyoutsUnder(view);
                Assert.NotEmpty(popovers);

                foreach (var p in popovers)
                {
                    p.Flyout.ShowAt(p.Target);
                    var first = Appearance(p.Flyout.Content as Control);
                    Assert.True(first.Count > 0,
                        "a popover body produced no styled text — this probe is measuring nothing");
                    p.Flyout.Hide();

                    // The re-heat the close schedules. This is the step that
                    // used to poison the subtree.
                    Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);

                    p.Flyout.ShowAt(p.Target);
                    var second = Appearance(p.Flyout.Content as Control);
                    p.Flyout.Hide();

                    Assert.True(first.SequenceEqual(second),
                        $"a reopened popover is styled differently from its first open — " +
                        $"{first.Zip(second).Count(x => x.First != x.Second)} of {first.Count} brushes " +
                        "changed. A re-heat that styles a DETACHED subtree resolves DynamicResource " +
                        "against nothing and caches the fallback.");
                }
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);

    /// <summary>Every resolved foreground in the body, in tree order. Colours
    /// rather than brush identity: a re-resolved brush is a different instance
    /// carrying the same colour, and that is not a regression.</summary>
    private static List<string> Appearance(Control? body)
    {
        if (body is null) return [];

        return body.GetVisualDescendants()
            .OfType<TextBlock>()
            .Select(t => $"{t.Text}|{Describe(t.Foreground)}|{t.FontSize:0.##}")
            .ToList();
    }

    private static string Describe(IBrush? brush) => brush switch
    {
        ISolidColorBrush solid => solid.Color.ToString(),
        null => "null",
        _ => brush.GetType().Name
    };
}
