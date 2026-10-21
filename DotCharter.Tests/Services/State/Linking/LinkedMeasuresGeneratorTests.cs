using DotCharter.Services.Ai.Chart;
using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.FretAnchors;
using DotCharter.UI.ViewModels.Panels;
using static DotCharter.Tests.Core.Linking.LinkFixture;

namespace DotCharter.Tests.Services.State.Linking;

/// <summary>
///     The generators (FHP, fingering, the AI chat, hand shapes, difficulty levels) are link-aware:
///     their result is kept ONCE per link group — the first placement they changed, in group order,
///     is written into every other placement in the same undo step — so every link survives even
///     where the generator came out differently per copy. Content outside the placements keeps
///     exactly what the generator wrote, and one undo takes back the run and its copies together.
///     <para>
///         Each fixture links bar 1 and bar 4 of Lead with bar 4 and bar 7 of Rhythm, and runs the
///         generator on Lead only, so without the share Lead's copies would differ from each other
///         (a per-bar result) or from Rhythm's (an arrangement-wide one).
///     </para>
/// </summary>
public sealed class LinkedMeasuresGeneratorTests
{
    private static LinkGroup LinkAcross(LinkHarness h) =>
        h.Link((h.Lead, 1), (h.Lead, 4), (h.Rhythm, 4), (h.Rhythm, 7));

    /// <summary>The fret a per-bar generator gives bar <paramref name="measure" />: different in every bar.</summary>
    private static int FretFor(int measure) => measure + 1;

    private static int AnchorFretAt(Arrangement arrangement, int measure) =>
        LinkHarness.Hardest(arrangement).Anchors.Single(a => a.Position == At(measure, 0)).Fret;

    [Fact]
    public void Generate_fhp_keeps_every_link_and_writes_the_first_copy_into_the_others()
    {
        var t = new LinkToolHarness(new PerBarAnchors());
        var group = LinkAcross(t.H);
        var before = t.H.Snapshot();

        t.Maintenance.GenerateFhp();

        LinkHarness.AssertLinkedAndEqual(t.H, group, 4);
        Assert.Equal(FretFor(1), AnchorFretAt(t.H.Lead, 4));
        Assert.Equal(FretFor(1), AnchorFretAt(t.H.Rhythm, 7));
        // Outside the placements the generator's own result stands.
        Assert.Equal(FretFor(2), AnchorFretAt(t.H.Lead, 2));
        Assert.Equal(FretFor(7), AnchorFretAt(t.H.Lead, 7));

        AssertOneUndoRestores(t.H, before);
    }

    [Fact]
    public void An_ai_chat_pass_keeps_every_link_and_writes_the_first_copy_into_the_others()
    {
        var h = LinkHarness.WithRhythm();
        var group = LinkAcross(h);
        var before = h.Snapshot();
        var anchors = Enumerable.Range(1, 7).Select(m => new AiAnchorPlacement(At(m, 0), FretFor(m), 4)).ToList();
        var plan = new AiChartChunkPlan(0, At(1, 0), At(8, 0), anchors, []);

        var action = AiChartApplier.Build(h.Lead, LinkHarness.Hardest(h.Lead), [plan], new FingeringGenerationService(),
            out _, checkGuards: false);
        Assert.True(h.History.Do(action!));

        LinkHarness.AssertLinkedAndEqual(h, group, 4);
        Assert.Equal(FretFor(1), AnchorFretAt(h.Lead, 4));
        Assert.Equal(FretFor(1), AnchorFretAt(h.Rhythm, 4));
        Assert.Equal(FretFor(7), AnchorFretAt(h.Lead, 7));

        AssertOneUndoRestores(h, before);
    }

    [Fact]
    public void Generate_fingerings_keeps_every_link_and_carries_the_fingering_to_the_other_arrangement()
    {
        var t = new LinkToolHarness();
        foreach (var arrangement in new[] { t.H.Lead, t.H.Rhythm }) arrangement.ChordTemplates[0].Fingers.Clear();
        var group = LinkAcross(t.H);
        var before = t.H.Snapshot();
        var panel = new ChordTemplateEditorViewModel(t.Editor, t.H.History, new FingeringGenerationService(),
            FakeLocalization.English(), new OnboardingWizardRig.InlineDispatcher(), projectState: t.H.State);

        panel.GenerateAllFingeringsCommand.Execute(null);

        LinkHarness.AssertLinkedAndEqual(t.H, group, 4);
        Assert.NotEmpty(TemplateOfChordAt(t.H.Rhythm, 7).Fingers);
        // Rhythm bar 1 is no placement: its chord keeps the unfingered template.
        Assert.Empty(TemplateOfChordAt(t.H.Rhythm, 1).Fingers);

        AssertOneUndoRestores(t.H, before);
    }

    [Fact]
    public void Generate_hand_shapes_keeps_every_link_and_carries_the_shapes_to_the_other_arrangement()
    {
        var t = new LinkToolHarness();
        foreach (var arrangement in new[] { t.H.Lead, t.H.Rhythm }) LinkHarness.Hardest(arrangement).HandShapes.Clear();
        var group = LinkAcross(t.H);
        var before = t.H.Snapshot();

        t.Maintenance.GenerateHandShapesForArrangement();

        LinkHarness.AssertLinkedAndEqual(t.H, group, 4);
        Assert.Contains(LinkHarness.Hardest(t.H.Rhythm).HandShapes, s => s.Position == At(7, 2));
        Assert.DoesNotContain(LinkHarness.Hardest(t.H.Rhythm).HandShapes, s => s.Position == At(1, 2));

        AssertOneUndoRestores(t.H, before);
    }

    /// <summary>
    ///     Links live on the hardest level only. Generate levels keeps the hardest level as it was,
    ///     so every link survives; the lower levels it builds are per arrangement and never linked.
    /// </summary>
    [Fact]
    public void Generate_levels_keeps_every_link_and_the_lower_levels_stay_per_arrangement()
    {
        var t = new LinkToolHarness();
        var group = LinkAcross(t.H);
        var before = t.H.Snapshot();

        Assert.True(t.Maintenance.GenerateLevels(3));

        Assert.Equal(3, t.H.Lead.Levels.Count);
        Assert.Single(t.H.Rhythm.Levels);
        LinkHarness.AssertLinkedAndEqual(t.H, group, 4);
        Assert.Equal(before, t.H.Snapshot()); // the hardest level, every bar, exactly as it was

        t.H.History.Undo();
        Assert.Single(t.H.Lead.Levels);
        Assert.Equal(before, t.H.Snapshot());
        LinkHarness.AssertLinkedAndEqual(t.H, t.H.Project.LinkGroups.Single(), 4);
    }

    private static ChordTemplate TemplateOfChordAt(Arrangement arrangement, int measure)
    {
        var chord = LinkHarness.Hardest(arrangement).Chords.Single(c => c.Position == At(measure, 2));
        return arrangement.ChordTemplates.Single(tpl => tpl.Id == chord.TemplateId);
    }

    private static void AssertOneUndoRestores(LinkHarness h, List<string> before)
    {
        Assert.NotEqual(before, h.Snapshot());
        h.History.Undo();
        Assert.Equal(before, h.Snapshot());
        LinkHarness.AssertLinkedAndEqual(h, h.Project.LinkGroups.Single(), 4);
    }

    /// <summary>An FHP generator whose answer differs in every bar: one anchor on each downbeat,
    /// at <see cref="FretFor" /> of its bar.</summary>
    private sealed class PerBarAnchors : IFretAnchorGenerationService
    {
        public IReadOnlyList<FretAnchor> GenerateAnchors(ChartLevel level,
            FractionalPosition? rangeStart = null, FractionalPosition? rangeEnd = null,
            IReadOnlyList<ChordTemplate>? chordTemplates = null, int capo = 0,
            bool bassArrangement = false, Func<ChordTemplate, ChordTemplate?>? freeGrip = null,
            Arrangement? tuning = null) =>
            [.. Enumerable.Range(0, 9).Select(m => new FretAnchor(At(m, 0), FretFor(m)))];
    }
}
