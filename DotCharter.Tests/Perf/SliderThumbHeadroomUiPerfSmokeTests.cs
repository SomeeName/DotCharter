using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.VisualTree;
using DotCharter.UI;
using DotCharter.UI.ViewModels.Shell;
using DotCharter.UI.Views.Shell;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     Nothing may CLIP a slider thumb's painted extent — not its drop shadow,
///     and not the circle at the size <c>:pointerover</c> and <c>:pressed</c>
///     grow it to. Every slider STYLE in the app, derived from the style files.
/// </summary>
/// <remarks>
///     <para>
///         <b>A thumb is clipped by THREE nested boxes, not one, and the first
///         version of this file could only ever see the innermost.</b> It walked
///         out from the thumb and stopped at the Slider — so it never examined
///         the Slider's own <c>ClipToBounds</c>, nor the
///         <c>DataValidationErrors</c> its template wraps the content in, both
///         of which are exactly the slider's height. Un-clipping the Thumb
///         changed nothing visible and this test went on passing. That is why it
///         is rewritten rather than extended.
///     </para>
///     <para>
///         Measured — <c>Slider.FirePopoverSlider</c> inside a real prewarmed
///         flyout, a thumb painting 38 px (14 + 2 × (3 px offset + 9 px blur))
///         through a 20 px slider:
///     </para>
///     <code>
///       [ 1] Thumb    thumb          14x14   &lt;&lt;&lt; clipped
///       [ 8] DataValidationErrors   146x20   &lt;&lt;&lt; CLIP
///       [ 9] Slider                 146x20   &lt;&lt;&lt; CLIP
///     </code>
///     <para>
///         So the walk now runs all the way out. The card and presenter clips
///         above are legitimate — a popover must clip its own 14 px radius — and
///         are excluded BY SIZE rather than by position or type: a clip wider and
///         taller than the thumb's paint cannot be cutting it, and a new host
///         therefore never needs adding to a list.
///     </para>
///     <para>
///         The CASES are derived from the style files, so a new slider class is
///         covered the day it compiles. A hand list is what let
///         <c>MixerSlider</c> — view-scoped, in <c>InspectorStyles.axaml</c> — go
///         unchecked while every row of the mixer inspector drew a partial arc.
///     </para>
/// </remarks>
[Collection("HeadlessUI")]
public sealed class SliderThumbHeadroomUiPerfSmokeTests
{
    /// <summary>The largest scale any state applies (<c>:pressed</c> is 1.25).</summary>
    private const double MaxThumbScale = 1.25;

    /// <summary>The style files that may restyle a slider.</summary>
    private static readonly string[] StyleFiles =
    [
        "DotCharter.Tone.UI/Styles/Inputs.axaml",
        "DotCharter.Tone.UI/Styles/Fire.axaml",
        "DotCharter.UI/Views/Editor/InspectorStyles.axaml"
    ];

    /// <summary>
    ///     Every <c>Slider.Foo</c> class any style file gives a look to, plus the
    ///     bare default. DERIVED — the point of this file is that nobody has to
    ///     remember to add one.
    /// </summary>
    public static TheoryData<string> SliderStyles
    {
        get
        {
            var data = new TheoryData<string> { "" };
            foreach (var name in DiscoverSliderClasses()) data.Add(name);
            return data;
        }
    }

    internal static IReadOnlyList<string> DiscoverSliderClasses()
    {
        var found = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var relative in StyleFiles)
        {
            var path = Path.Combine(TestPaths.RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar));
            // Fire.axaml is a container of `<StyleInclude>`s over `Styles/Fire/`
            // (`FirePopoverSlider` lives in the popover part), so the file has to
            // be read WITH what it merges or the derivation quietly shrinks.
            var markup = StyleFileText.ReadWithIncludes(path);
            foreach (Match m in Regex.Matches(markup, @"Slider\.([A-Za-z][A-Za-z0-9]*)"))
                found.Add(m.Groups[1].Value);
        }

        // `.Song`/`.Guitar`/`.Midi`/`.Pitch` are MODIFIERS on the popover slider,
        // not sliders of their own — they carry a track fill and nothing else.
        found.ExceptWith(["Song", "Guitar", "Midi", "Pitch"]);
        return [.. found];
    }

    [Theory]
    [MemberData(nameof(SliderStyles))]
    public Task NothingClipsAThumb_OnAnySliderStyle_HorizontalAndVertical(string styleClass) =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            foreach (var orientation in new[] { Orientation.Horizontal, Orientation.Vertical })
            {
                var slider = new Slider
                {
                    Orientation = orientation,
                    Minimum = 0,
                    Maximum = 1,
                    Value = 0.5,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center
                };

                // ONLY the long axis is given a size. Assigning the other one —
                // even to NaN — writes a LOCAL value that beats the style setter,
                // so the slider gets its natural height instead of the 20 px the
                // popover style asks for, and the tight box that does the
                // clipping never exists. The first version of this file did
                // exactly that and could not reproduce the bug it was written
                // for.
                if (orientation == Orientation.Horizontal) slider.Width = 220;
                else slider.Height = 220;
                if (styleClass.Length > 0) slider.Classes.Add(styleClass);

                var window = new Window { Width = 400, Height = 400, Content = slider };
                // `MixerSlider` lives in a VIEW-scoped grammar, so a bare window
                // matches none of its selectors — and a slider that never picked
                // up its thumb template proves nothing about that thumb.
                window.Styles.Add(new StyleInclude(new Uri("avares://DotCharter/"))
                {
                    Source = new Uri("avares://DotCharter/Views/Editor/InspectorStyles.axaml")
                });
                window.Show();
                try
                {
                    window.UpdateLayout();
                    AssertUnclipped(slider, $"{orientation} .{(styleClass.Length == 0 ? "(bare)" : styleClass)}");
                }
                finally
                {
                    window.Close();
                }
            }
        }, CancellationToken.None);

    /// <summary>
    ///     …and the sliders the app actually ships, reached through the
    ///     product's own prewarmed <c>Flyout</c> rather than newed up here.
    /// </summary>
    [Fact]
    public Task EverySliderOnTheTransportRail_PaintsItsWholeThumb() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var view = new ToolbarView
            {
                DataContext = App.Services!.GetRequiredService<MainWindowViewModel>()
            };
            var window = new Window { Width = 1600, Height = 1000, Content = view };
            window.Show();
            try
            {
                view.Measure(new Size(1600, 1000));
                view.Arrange(new Rect(0, 0, 1600, 1000));
                view.UpdateLayout();

                var targets = MenuFlyoutPrewarmer.FlyoutsUnder(view);
                foreach (var target in targets) MenuFlyoutPrewarmer.Realize(target, new Size(1600, 1000));

                var sliders = targets
                    .Select(t => t.Flyout.Content as Control)
                    .Where(c => c != null)
                    .SelectMany(c => c!.GetSelfAndVisualDescendants().OfType<Slider>())
                    .ToList();

                // Mix: Song · Guitar in · MIDI in.  Tempo: Speed · Pitch.
                Assert.True(sliders.Count >= 5,
                    $"only {sliders.Count} sliders were realized on the transport rail");

                foreach (var slider in sliders) AssertUnclipped(slider, "a transport popover slider");
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);

    /// <summary>The derivation itself has to keep finding things — an empty
    /// theory is a green test that checks nothing.</summary>
    [Fact]
    public void TheCaseList_IsDerivedFromTheStyleFiles_AndFindsTheKnownOnes()
    {
        var found = DiscoverSliderClasses();
        Assert.Contains("MixerSlider", found);
        Assert.Contains("FireSlider", found);
        Assert.Contains("FirePopoverSlider", found);
    }

    // ── the one check ───────────────────────────────────────────────────────

    private static void AssertUnclipped(Slider slider, string name)
    {
        var thumb = slider.GetVisualDescendants().OfType<Thumb>().FirstOrDefault();
        Assert.True(thumb != null, $"{name}: realized no Thumb — its template did not apply");

        var chrome = thumb!.GetVisualDescendants().OfType<Border>().FirstOrDefault();
        Assert.True(chrome != null, $"{name}: the Thumb has no Border — its template did not apply");

        var painted = PaintedExtent(chrome!);
        Assert.True(painted is { Width: > 0, Height: > 0 }, $"{name}: the thumb painted nothing");

        // ALL the way out, not just to the Slider: the two clips that were
        // actually cutting the thumb are the Slider itself and the
        // DataValidationErrors inside it.
        for (Visual? v = chrome; v != null; v = v.GetVisualParent())
        {
            if (!v.ClipToBounds) continue;

            var box = v.Bounds;
            if (box.Width + 0.5 >= painted.Width && box.Height + 0.5 >= painted.Height) continue;

            Assert.Fail(
                $"{name}: {v.GetType().Name}{Named(v)} clips to {box.Width:0.##} × {box.Height:0.##} while the " +
                $"thumb paints {painted.Width:0.##} × {painted.Height:0.##} — the circle is sliced flat and its " +
                "drop shadow is cut. Un-clip it in Styles/Inputs.axaml; do NOT shrink the thumb.");
        }
    }

    private static string Named(Visual v) => v is Control { Name: { } n } ? $"[{n}]" : "";

    /// <summary>
    ///     Everything the thumb puts on screen: its arranged box grown by the
    ///     biggest scale any state applies, and by the reach of its drop shadow
    ///     (offset + blur + spread, on every side the blur touches).
    /// </summary>
    private static Size PaintedExtent(Border chrome)
    {
        var w = chrome.Bounds.Width * MaxThumbScale;
        var h = chrome.Bounds.Height * MaxThumbScale;

        foreach (var shadow in chrome.BoxShadow)
        {
            var reachX = Math.Abs(shadow.OffsetX) + shadow.Blur + shadow.Spread;
            var reachY = Math.Abs(shadow.OffsetY) + shadow.Blur + shadow.Spread;
            w = Math.Max(w, chrome.Bounds.Width + 2 * reachX);
            h = Math.Max(h, chrome.Bounds.Height + 2 * reachY);
        }

        return new Size(w, h);
    }
}
