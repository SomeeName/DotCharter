using System.Text.RegularExpressions;

namespace DotCharter.Tests.UI;

/// <summary>
///     The Tempo flyout's two practice controls: playback speed and song pitch.
///     <para>
///         A markup lint rather than a layout test, because flyout content is not
///         materialized until the flyout opens — there is no visual tree to
///         measure without driving a real popup, which deadlocks
///         <c>Avalonia.Headless</c>. What is pinned here is exactly what the owner
///         asked for and what a later "tidy-up" would quietly undo: whole
///         semitones over −12..+12, displayed without decimals.
///     </para>
/// </summary>
public sealed class ToolbarTempoFlyoutTests
{
    /// <summary>
    ///     The Tempo popover's markup. It moved out of <c>ToolbarView.axaml</c>
    ///     into its own VIEW in 0.54 — every popover did, so each can be
    ///     constructed, hosted twice and rendered for comparison against
    ///     <c>Play Menus.dc.html</c>. The rows this file pins are unchanged.
    /// </summary>
    private static string Toolbar() =>
        UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Shell", "Popovers",
            "TempoPopoverView.axaml")));

    /// <summary>The whole point of the row: a pitch a guitarist can actually
    /// name. A fractional transpose is not a musical request, and
    /// <c>Transport.PitchShift</c> is a <c>double</c>, so without the snap the
    /// readout drifts to values like "+3.37 st".</summary>
    [Fact]
    public void TheSongPitchSlider_IsWholeSemitonesFromMinus12ToPlus12()
    {
        var slider = SliderBoundTo(Toolbar(), "Transport.PitchShift");

        Assert.Contains("Minimum=\"-12\"", slider);
        Assert.Contains("Maximum=\"12\"", slider);
        Assert.Contains("TickFrequency=\"1\"", slider);
        Assert.Contains("IsSnapToTickEnabled=\"True\"", slider);
    }

    /// <summary>
    ///     "-12 to +12 … no comma": the readout is an integer with an explicit
    ///     sign, never <c>0.00</c>. The three-part format is
    ///     positive;negative;zero, so 0 reads "0 st" and not "+0 st".
    /// </summary>
    [Fact]
    public void TheSongPitchReadout_ShowsSignedWholeSemitones()
    {
        var markup = Toolbar();

        Assert.Contains(@"Text=""{Binding Transport.PitchShift, StringFormat=\{0:+0;-0;0\} st}""", markup);
    }

    /// <summary>
    ///     One engine value, two places to reach it — the flyout and the Mixer
    ///     inspector must not drift into two different ranges for the same
    ///     property, which is how a control ends up able to request a state the
    ///     other one says is impossible.
    /// </summary>
    [Fact]
    public void TheFlyoutAndTheMixerAgreeOnTheSongPitchRange()
    {
        var mixer = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Editor",
            "Inspector", "MixerInspectorView.axaml")));

        foreach (var attribute in new[] { "Minimum=\"-12\"", "Maximum=\"12\"", "TickFrequency=\"1\"" })
        {
            Assert.Contains(attribute, SliderBoundTo(Toolbar(), "Transport.PitchShift"));
            Assert.Contains(attribute, SliderBoundTo(mixer, "Transport.PitchShift"));
        }
    }

    /// <summary>The `&lt;Slider …&gt;` element whose Value binds
    /// <paramref name="binding" />, as raw markup.</summary>
    private static string SliderBoundTo(string markup, string binding)
    {
        var matches = Regex.Matches(markup, @"<Slider\b[^>]*?/?>", RegexOptions.Singleline)
            .Select(m => m.Value)
            .Where(s => s.Contains($"Value=\"{{Binding {binding}}}\"", StringComparison.Ordinal))
            .ToList();

        Assert.True(matches.Count == 1,
            $"expected exactly one Slider bound to {binding}, found {matches.Count}");
        return matches[0];
    }
}
