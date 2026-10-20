using Avalonia.Controls;
using Avalonia.VisualTree;
using DotCharter.UI.Views.Editor;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The editor's DOCKED panes — the ones that sit beside the Inspector — built
///     for real, attached to a window and laid out.
///     <para>
///         They now pull in <c>InspectorStyles.axaml</c> so they share the
///         Inspector's card / field / empty-state vocabulary instead of each
///         hand-rolling its own. That include is exactly the kind of change with
///         no compile-time signal: an unresolvable <c>StaticResource</c>, a style
///         setter the headless platform cannot satisfy, or a class name that does
///         not exist all build clean and fail (or silently do nothing) at
///         runtime.
///     </para>
///     <para>
///         Named <c>*UiPerfSmokeTests</c> because it needs the real Avalonia app
///         session, which cannot share a testhost with the gate and is excluded from the
///         default gate.
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class EditorPaneConstructionUiPerfSmokeTests
{
    [Fact]
    public async Task PhraseCatalogView_buildsAndLaysOut()
    {
        await HeadlessSessionFixture.Dispatch(() => AttachAndLayout(new PhraseCatalogView()),
            CancellationToken.None);
    }

    [Fact]
    public async Task StemMixerView_buildsAndLaysOut()
    {
        await HeadlessSessionFixture.Dispatch(() => AttachAndLayout(new StemMixerView()),
            CancellationToken.None);
    }

    [Fact]
    public async Task SheetMusicPaneView_buildsAndLaysOut()
    {
        await HeadlessSessionFixture.Dispatch(() => AttachAndLayout(new SheetMusicPaneView()),
            CancellationToken.None);
    }

    /// <summary>
    ///     Attaches, lays out, and asserts the pane actually OCCUPIES its space.
    ///     A view whose styles failed to resolve still arranges — it just
    ///     collapses to nothing — so measuring is what separates "rendered" from
    ///     "rendered nothing", which is the shape every silent XAML fault in this
    ///     repo has taken.
    /// </summary>
    private static void AttachAndLayout(Control view)
    {
        var window = new Window { Width = 900, Height = 600, Content = view };
        window.Show();
        try
        {
            view.Measure(new Avalonia.Size(900, 600));
            view.Arrange(new Avalonia.Rect(0, 0, 900, 600));

            Assert.True(view.Bounds.Width > 1 && view.Bounds.Height > 1,
                $"{view.GetType().Name} laid out as {view.Bounds} — it rendered nothing");

            // The pane header is the one piece of chrome every docked pane shares,
            // and its 22 px height is what lines the fixed 3D preview up with its
            // dockable siblings. If a restyle drops or resizes it, the dock row
            // goes visibly out of step.
            var header = view.GetVisualDescendants()
                .OfType<Border>()
                .FirstOrDefault(b => b.Classes.Contains("PaneHeader"));
            if (header is not null)
            {
                Assert.True(header.Bounds.Height is > 1 and <= 32,
                    $"{view.GetType().Name}'s PaneHeader is {header.Bounds.Height:0.#} px — " +
                    "the dock chrome it aligns with is 22.");
            }
        }
        finally
        {
            window.Close();
        }
    }
}
