using DotCharter.Services.Ai.Chart;
using DotCharter.Services.Editing.Fingering;

namespace DotCharter.Tests.Ai.Chart;

/// <summary>
///     The apply step (#497): one undo node for the anchors, the minted templates and the
///     repointed chords and hand shapes; shared templates are never edited in place; the
///     inspector's own rules gate every chunk on a scratch copy.
/// </summary>
public sealed class AiChartApplierTests
{
    private readonly AiChartFixture _f = new();
    private readonly FingeringGenerationService _fingering = new();

    private AiChartChunk Intro => _f.Chunks()[0];
    private AiChartChunk Verse => _f.Chunks()[1];

    private AiChartChunkPlan Plan(AiChartChunk chunk, AiChartFixture.ReplyDoc reply)
    {
        Assert.True(AiChartValidator.TryPlan(reply.Decode(), chunk, reply.Job, _f.Facts, _f.Level, out var plan, out var why),
            why?.Reason);
        return plan!;
    }

    /// <summary>The verse with a new anchor at beat 20 (the run of notes up the neck) and
    /// the first chord re-fingered with a ring-finger barre.</summary>
    private AiChartChunkPlan VersePlan()
    {
        var reply = _f.EchoReply(Verse);
        reply.Anchors.Insert(1, new AiChartFixture.ReplyAnchor { At = "s14", Fret = 5, Width = 4 });
        reply.Chord("s12").Fingers = [-1, 1, 3, 3, -1, -1];
        return Plan(Verse, reply);
    }

    [Fact]
    public void A_plan_is_exactly_its_anchors_and_a_repointed_chord_and_the_shared_template_is_untouched()
    {
        var shared = _f.TemplateById(AiChartFixture.PowerC);
        var sharedFingers = new Dictionary<int, int>(shared.Fingers);
        var introAnchors = _f.Level.Anchors.Where(a => a.Position < AiChartFixture.At(16)).ToList();

        var action = AiChartApplier.Build(_f.Arrangement, _f.Level, [VersePlan()], _fingering, out var summary);
        action!.Execute();

        Assert.Equal(1, summary.ChordsRefingered);
        Assert.Equal(1, summary.TemplatesMinted);
        Assert.Equal(0, summary.NamesChanged);
        // The verse's anchors are exactly the reply's; the intro's are the same instances.
        Assert.Equal(
            [(0, 5), (8, 3), (16, 3), (20, 5), (24, 1)],
            _f.Level.Anchors.OrderBy(a => a.Position).Select(a => (a.Position.BeatId, a.Fret)));
        Assert.All(introAnchors, a => Assert.Contains(a, _f.Level.Anchors));

        // The chord moved to a NEW template; the shared one is the same object, same fingers,
        // and every other C5 chord still points at it.
        var minted = _f.Arrangement.ChordTemplates.Single(t => t.Id == _f.Level.Chords[4].TemplateId);
        Assert.NotSame(shared, minted);
        Assert.Equal(new Dictionary<int, int> { [1] = 1, [2] = 3, [3] = 3 }, minted.Fingers);
        Assert.Equal(shared.Frets, minted.Frets);
        Assert.Equal("C5", minted.Name);
        Assert.Same(shared, _f.TemplateById(AiChartFixture.PowerC));
        Assert.Equal(sharedFingers, shared.Fingers);
        Assert.All(new[] { 0, 1, 2, 5, 7 }, i => Assert.Equal(AiChartFixture.PowerC, _f.Level.Chords[i].TemplateId));
    }

    /// <summary>#785: a PINNED template is never re-fingered by the AI pass — the chord stays on
    /// it; the test above is the same plan unpinned, where the chord moves.</summary>
    [Fact]
    public void An_AI_fingering_never_moves_a_chord_off_a_pinned_template()
    {
        var shared = _f.TemplateById(AiChartFixture.PowerC);
        var plan = VersePlan();
        shared.Pinned = true;

        var action = AiChartApplier.Build(_f.Arrangement, _f.Level, [plan], _fingering, out var summary);
        action?.Execute();

        Assert.Equal(0, summary.ChordsRefingered);
        Assert.Equal(0, summary.TemplatesMinted);
        Assert.Equal(AiChartFixture.PowerC, _f.Level.Chords[4].TemplateId);
    }

    [Fact]
    public void One_ctrl_z_restores_anchors_templates_and_chords_and_redo_reapplies()
    {
        var history = new HistoryService(new FakeAppLogger());
        var anchorsBefore = _f.Level.Anchors.Select(a => (a.Position, a.Fret, a.Width)).OrderBy(a => a.Position).ToList();
        var templatesBefore = _f.Arrangement.ChordTemplates.ToList();
        var chordIdsBefore = _f.Level.Chords.Select(c => c.TemplateId).ToList();
        var shapesBefore = _f.Level.HandShapes.Select(h => h.TemplateId).ToList();

        var introReply = _f.EchoReply(Intro);
        foreach (var id in new[] { "s8", "s9", "s10" }) introReply.Chord(id).Fingers = [-1, 1, 3, 3, -1, -1];
        var action = AiChartApplier.Build(_f.Arrangement, _f.Level, [Plan(Intro, introReply), VersePlan()], _fingering,
            out var summary);

        Assert.True(history.Do(action!));
        Assert.Equal(4, summary.ChordsRefingered);
        // Four chords re-fingered the same way converge on ONE minted template, and the
        // hand shape over the three intro chords follows them.
        Assert.Equal(1, summary.TemplatesMinted);
        Assert.Equal(1, summary.HandShapesRepointed);
        Assert.NotEqual(shapesBefore, _f.Level.HandShapes.Select(h => h.TemplateId).ToList());

        history.Undo();
        Assert.False(history.CanUndo);
        Assert.Equal(anchorsBefore, _f.Level.Anchors.Select(a => (a.Position, a.Fret, a.Width)).OrderBy(a => a.Position));
        Assert.Equal(templatesBefore.Count, _f.Arrangement.ChordTemplates.Count);
        Assert.All(templatesBefore, t => Assert.Contains(_f.Arrangement.ChordTemplates, x => ReferenceEquals(x, t)));
        Assert.Equal(chordIdsBefore, _f.Level.Chords.Select(c => c.TemplateId));
        Assert.Equal(shapesBefore, _f.Level.HandShapes.Select(h => h.TemplateId));

        history.Redo();
        Assert.Equal(4, _f.Level.Chords.Count(c => c.TemplateId == 3));
    }

    [Fact]
    public void A_fingering_that_already_exists_is_reused_not_minted()
    {
        var existing = new ChordTemplate
        {
            Id = 7, Name = "C5", Frets = new() { [1] = 3, [2] = 5, [3] = 5 }, Fingers = new() { [1] = 1, [2] = 3, [3] = 3 }
        };
        _f.Arrangement.ChordTemplates.Add(existing);

        var action = AiChartApplier.Build(_f.Arrangement, _f.Level, [VersePlan()], _fingering, out var summary);
        action!.Execute();

        Assert.Equal(0, summary.TemplatesMinted);
        Assert.Equal(7, _f.Level.Chords[4].TemplateId);
        Assert.Equal(4, _f.Arrangement.ChordTemplates.Count);
    }

    [Fact]
    public void A_plan_whose_chord_was_edited_meanwhile_is_dropped_whole()
    {
        var plan = VersePlan();
        _f.Level.Chords[4].TemplateId = AiChartFixture.PowerG;

        var action = AiChartApplier.Build(_f.Arrangement, _f.Level, [plan], _fingering, out var summary);

        Assert.Null(action);
        Assert.Equal(1, summary.StaleChunks);
    }

    [Fact]
    public void The_rule_gate_rejects_a_chunk_that_introduces_an_inspector_finding_and_leaves_the_scratch_alone()
    {
        // Index finger on fret 5 under an anchor on fret 3: E04 (and L08) — the inspector's
        // own rules say no, so the chunk goes.
        var reply = _f.EchoReply(Verse);
        reply.Chord("s12").Fingers = [-1, 2, 1, 1, -1, -1];
        var plan = Plan(Verse, reply);
        var gate = new AiChartRuleGate(_f.Arrangement, _f.Level, _f.Beats, _fingering, AiChartJob.FhpAndFingering, null);
        var scratchIds = gate.Scratch.Levels[0].Chords.Select(c => c.TemplateId).ToList();

        Assert.False(gate.TryAccept(plan, out var rejection));
        Assert.Equal(AiChartRejectionCodes.Rules, rejection!.Code);
        Assert.Contains("E04", rejection.Reason, StringComparison.Ordinal);
        Assert.Equal(scratchIds, gate.Scratch.Levels[0].Chords.Select(c => c.TemplateId));
        Assert.Equal(3, gate.Scratch.ChordTemplates.Count);

        // …and a good plan passes, even though the chart already had findings there
        // (the notes up the neck outside the anchor) that the reply did not cause.
        Assert.True(gate.TryAccept(VersePlan(), out rejection), rejection?.Reason);
        // The live chart was never touched by either.
        Assert.Equal(AiChartFixture.PowerC, _f.Level.Chords[4].TemplateId);
        Assert.Equal(4, _f.Level.Anchors.Count);
    }
}
