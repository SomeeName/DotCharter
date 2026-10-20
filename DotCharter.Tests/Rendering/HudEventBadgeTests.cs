using DotCharter.Rendering.Data;
using DotCharter.Rendering.Data.Render;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     The 3D HUD's section and phrase badges are INDEPENDENT.
///     <para>
///         `HudComponent` wrapped both draws in
///         <c>if (currentPhrase.HasValue &amp;&amp; Phrase != "END")</c>, so a
///         chart carrying sections but no phrase events showed no section name at
///         all, and every chart lost its section badge the instant the
///         terminating `END` phrase went by. The 2D timeline never did that —
///         `EventLayer.DrawPinnedLabels` walks for the latest section and the
///         latest phrase behind two separate gates — so the two renderers
///         disagreed about a label the same snapshot fed both.
///     </para>
/// </summary>
public sealed class HudEventBadgeTests
{
    [Fact]
    public void A_chart_with_sections_and_no_phrases_still_names_its_section()
    {
        var (section, phrase) = Resolve(1_000, Section(0, "Intro"), Section(500, "Verse 1"));

        Assert.Equal("Verse 1", section);
        Assert.Equal("", phrase);
    }

    [Fact]
    public void The_section_survives_the_END_phrase()
    {
        var (section, phrase) = Resolve(1_000,
            Section(0, "Outro"), Phrase(900, HudEventBadges.TerminatorPhrase));

        Assert.Equal("Outro", section);
        Assert.Equal("", phrase); // END terminates the chart; it is not a phrase NAME
    }

    [Fact]
    public void A_chart_with_phrases_and_no_sections_still_names_its_phrase()
    {
        var (section, phrase) = Resolve(1_000, Phrase(0, "riff1"));

        Assert.Equal("", section);
        Assert.Equal("riff1", phrase);
    }

    [Fact]
    public void A_section_and_a_phrase_with_the_SAME_name_both_show()
    {
        // Owner's standing rule: deduplicating same-named section/phrase labels
        // was proposed and REJECTED. They are two different pieces of chart
        // metadata that happen to agree.
        var (section, phrase) = Resolve(1_000, Both(0, "Solo", "Solo"));

        Assert.Equal("Solo", section);
        Assert.Equal("Solo", phrase);
    }

    [Fact]
    public void Each_badge_is_the_latest_of_its_own_kind_at_or_before_the_playhead()
    {
        var (section, phrase) = Resolve(1_500,
            Section(0, "Intro"),
            Phrase(200, "riff1"),
            Section(1_000, "Verse 1"),
            Phrase(2_000, "riff2"),     // after the playhead — must not win
            Section(2_000, "Chorus"));  // ditto

        Assert.Equal("Verse 1", section);
        Assert.Equal("riff1", phrase);
    }

    [Fact]
    public void Nothing_before_the_playhead_means_no_badges()
    {
        var (section, phrase) = Resolve(100, Both(500, "Intro", "riff1"));

        Assert.Equal("", section);
        Assert.Equal("", phrase);
    }

    [Fact]
    public void The_walk_honours_the_live_count_not_the_arrays_length()
    {
        // The snapshot's arrays are GC-reused and longer than the live window,
        // so stale trailing entries must not be read.
        var points = new[] { Section(0, "Intro"), Section(500, "STALE") };

        var (section, _) = HudEventBadges.Resolve(points, 1, 1_000);

        Assert.Equal("Intro", section);
    }

    // ---- fixtures -----------------------------------------------------------

    private static (string section, string phrase) Resolve(double timeMs, params RenderEventPoint[] points)
        => HudEventBadges.Resolve(points, points.Length, timeMs);

    private static RenderEventPoint Section(double t, string name)
        => new() { Time = t, Section = name, Phrase = "" };

    private static RenderEventPoint Phrase(double t, string name)
        => new() { Time = t, Section = "", Phrase = name };

    private static RenderEventPoint Both(double t, string section, string phrase)
        => new() { Time = t, Section = section, Phrase = phrase };
}
