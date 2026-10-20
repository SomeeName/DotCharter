using DotCharter.UI.Controls;
using DotCharter.UI.Editing.Tracks;

namespace DotCharter.Tests.UI;

/// <summary>
///     <b>The owner's v0.44.0 report: a Drop C# CDLC opened in the chart editor
///     with "E A D G B E" down the string gutter.</b>
///     <para>
///         The PSARC was innocent — probed on the real artifact
///         (<c>Galneryus - Brutal Spiral of Emotions_p.psarc</c>), whose SNG
///         metadata and manifest both declare <c>-3,-1,-1,-1,-1,-1</c>, and which
///         imports as <c>C# G# C# F# Bb Eb</c> end to end. What lied was the
///         gutter's REPAINT gate: <c>ChartRenderer</c> published its layout only
///         when the layout SHAPE changed, and swapping any six-string chart for
///         any other six-string chart moves no lane. The event never fired, the
///         gutter never invalidated, and it kept the string names of whatever was
///         open before — for a cold start, the default E-standard project.
///     </para>
///     <para>
///         Pure and platform-free (<see cref="TimelineGutterLayout" /> is a
///         struct, <see cref="GutterPublishGate" /> is a plain object), so it
///         runs in the default gate.
///     </para>
/// </summary>
public sealed class GutterPublishGateTests
{
    /// <summary>The real vector from the psarc above: Eb standard with the low
    /// string dropped to C# — Rocksmith's <c>E_FLAT_DROP_D_FLAT</c>.</summary>
    private static readonly int[] DropCSharp = [-3, -1, -1, -1, -1, -1];

    private static readonly int[] EStandard = [0, 0, 0, 0, 0, 0];

    private static TimelineGutterLayout Layout(int stringCount = 6)
        => TimelineGutterLayout.For(720, default, stringCount, false, 44);

    private static Arrangement Guitar(int[] steps, string tuningType = "CUSTOM")
    {
        var arrangement = new Arrangement
        {
            ArrangementType = ArrangementType.Lead,
            StringCount = steps.Length,
            TuningType = tuningType,
            Tuning = TuningUtilities.GetTuningNames(steps.Length, steps)
        };
        foreach (var step in steps) arrangement.TuningPitches.Add(new TuningOffset(step));
        return arrangement;
    }

    /// <summary>
    ///     <b>The bug, exactly.</b> Same six lanes, different tuning: the gate
    ///     must republish, or the gutter is never told the names changed.
    /// </summary>
    [Fact]
    public void SameLanes_NewTuning_Republishes()
    {
        var gate = new GutterPublishGate();
        var standard = Guitar(EStandard, "E_STANDARD");
        var dropCSharp = Guitar(DropCSharp);

        Assert.True(gate.ShouldPublish(Layout(), standard), "the first frame must publish");
        Assert.False(gate.ShouldPublish(Layout(), standard), "an unchanged frame must not republish");

        // The load. Identical layout — six lanes, same height, same ribbon.
        Assert.True(gate.ShouldPublish(Layout(), dropCSharp),
            "a same-shape tuning change did not republish: the gutter keeps the previous song's string names");
        Assert.False(gate.ShouldPublish(Layout(), dropCSharp));
    }

    /// <summary>
    ///     An OFFSETS-only change still republishes. <c>TuningCheckTargets</c>
    ///     reads <c>TuningPitches</c> before the note letters, so a stamp built
    ///     from the letters alone would miss this.
    /// </summary>
    [Fact]
    public void OffsetsOnlyChange_Republishes()
    {
        var gate = new GutterPublishGate();
        var arrangement = Guitar(EStandard, "E_STANDARD");
        Assert.True(gate.ShouldPublish(Layout(), arrangement));

        // The letters are deliberately left alone — only the offsets move.
        arrangement.TuningPitches[0].Step = -2;
        Assert.True(gate.ShouldPublish(Layout(), arrangement),
            "an in-place offsets edit did not republish");
    }

    /// <summary>Capo, string count and the type NAME all rename lanes too.</summary>
    [Theory]
    [InlineData("capo")]
    [InlineData("stringCount")]
    [InlineData("tuningType")]
    [InlineData("arrangementType")]
    public void EveryOtherGutterVisibleFact_Republishes(string change)
    {
        var gate = new GutterPublishGate();
        var arrangement = Guitar(EStandard, "E_STANDARD");
        Assert.True(gate.ShouldPublish(Layout(), arrangement));

        switch (change)
        {
            case "capo": arrangement.Capo = 3; break;
            case "stringCount": arrangement.StringCount = 7; break;
            case "tuningType": arrangement.TuningType = "DADGAD"; break;
            case "arrangementType": arrangement.ArrangementType = ArrangementType.Bass; break;
        }

        Assert.True(gate.ShouldPublish(Layout(), arrangement), $"a changed {change} did not republish");
    }

    /// <summary>
    ///     The per-frame gate still holds — this is a 60 Hz path and the whole
    ///     reason the event was gated in the first place.
    /// </summary>
    [Fact]
    public void AnIdenticalFrame_NeverRepublishes()
    {
        var gate = new GutterPublishGate();
        var arrangement = Guitar(DropCSharp);
        Assert.True(gate.ShouldPublish(Layout(), arrangement));
        for (var frame = 0; frame < 120; frame++)
            Assert.False(gate.ShouldPublish(Layout(), arrangement), $"frame {frame} republished");
    }

    /// <summary>A real shape change still publishes, tuning or no tuning.</summary>
    [Fact]
    public void AShapeChange_StillRepublishes()
    {
        var gate = new GutterPublishGate();
        var arrangement = Guitar(DropCSharp);
        Assert.True(gate.ShouldPublish(Layout(), arrangement));
        Assert.True(gate.ShouldPublish(Layout(4), arrangement), "a lane-count change did not republish");
    }

    /// <summary>A null arrangement (no project yet) publishes once and then rests.</summary>
    [Fact]
    public void NoArrangement_PublishesOnceThenRests()
    {
        var gate = new GutterPublishGate();
        Assert.True(gate.ShouldPublish(Layout(), null));
        Assert.False(gate.ShouldPublish(Layout(), null));
        Assert.True(gate.ShouldPublish(Layout(), Guitar(DropCSharp)),
            "the first real arrangement did not republish");
    }

    // ── the second half of the gate lives in the gutter ──────────────────────

    /// <summary>
    ///     The gutter re-checks the tuning when the renderer publishes, so its
    ///     own stamp has to move for the same facts. It did NOT include
    ///     <c>TuningPitches</c> until this change.
    /// </summary>
    [Fact]
    public void TheGuttersOwnStamp_MovesWithTheOffsets()
    {
        var standard = Guitar(EStandard, "E_STANDARD");
        var dropCSharp = Guitar(DropCSharp);
        Assert.NotEqual(
            TimelineGutterControl.TuningStampFor(standard),
            TimelineGutterControl.TuningStampFor(dropCSharp));

        var offsetsOnly = Guitar(EStandard, "E_STANDARD");
        var before = TimelineGutterControl.TuningStampFor(offsetsOnly);
        offsetsOnly.TuningPitches[0].Step = -2;
        Assert.NotEqual(before, TimelineGutterControl.TuningStampFor(offsetsOnly));
    }

    /// <summary>
    ///     …and the names the gutter would print for the real artifact. Sharps,
    ///     octave stripped, low string first.
    /// </summary>
    [Fact]
    public void TheRealCdlcTuning_NamesEveryLane()
    {
        Assert.Equal(
            ["C#", "G#", "C#", "F#", "A#", "D#"],
            TimelineGutterControl.StringNamesFor(Guitar(DropCSharp), 6));
    }

    /// <summary>
    ///     Anti-vacuity lint: the renderer must actually go through the gate.
    ///     Reverting <c>PublishGutterLayout</c> to its shape-only comparison
    ///     compiles perfectly and reopens the report, and no behavioural test
    ///     that stays out of the headless app session can see it.
    /// </summary>
    [Fact]
    public void TheRenderer_PublishesThroughTheGate()
    {
        var renderer = string.Concat(
            UiSourceTree.EnumerateFiles(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Controls")), "ChartRenderer*.cs")
                .Select(UiSourceTree.ReadAllText));

        Assert.Contains("_gutterGate.ShouldPublish(", renderer, StringComparison.Ordinal);
        Assert.DoesNotContain("_hasGutterLayout", renderer, StringComparison.Ordinal);
        Assert.DoesNotContain("GutterLayout.SameShapeAs(layout)", renderer, StringComparison.Ordinal);
    }
}
