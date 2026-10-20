using DotCharter.Services.Ai.Chart;

namespace DotCharter.Tests.Ai.Chart;

/// <summary>
///     The model proposes, the app decides (#497): a reply may change ONLY anchors and
///     chord fingers/names. Every other difference rejects the chunk whole, with a code.
/// </summary>
public sealed class AiChartValidatorTests
{
    private readonly AiChartFixture _f = new();

    private (AiChartChunkPlan? Plan, AiChartRejection? Rejection) Judge(
        AiChartChunk chunk, AiChartFixture.ReplyDoc reply, AiChartFixture? fixture = null)
    {
        var f = fixture ?? _f;
        AiChartValidator.TryPlan(reply.Decode(), chunk, reply.Job, f.Facts, f.Level, out var plan, out var rejection);
        return (plan, rejection);
    }

    private AiChartChunk Verse => _f.Chunks()[1];

    [Fact]
    public void A_reply_that_restates_the_chart_is_a_plan_that_changes_nothing()
    {
        var (plan, rejection) = Judge(Verse, _f.EchoReply(Verse));

        Assert.Null(rejection);
        Assert.Null(plan!.Anchors);
        Assert.Empty(plan.Chords);
    }

    [Fact]
    public void A_valid_reply_becomes_exactly_its_anchor_and_fingering_changes()
    {
        var reply = _f.EchoReply(Verse);
        reply.Anchors.Insert(1, new AiChartFixture.ReplyAnchor { At = "s14", Fret = 5, Width = 4 });
        reply.Chord("s12").Fingers = [-1, 1, 3, 3, -1, -1];

        var (plan, rejection) = Judge(Verse, reply);

        Assert.Null(rejection);
        Assert.Equal(
            [(AiChartFixture.At(16), 3, 4), (AiChartFixture.At(20), 5, 4), (AiChartFixture.At(24), 1, 4)],
            plan!.Anchors!.Select(a => (a.Position, a.Fret, a.Width)));
        var change = Assert.Single(plan.Chords);
        Assert.Equal(4, change.ChordIndex);
        Assert.Equal(AiChartFixture.PowerC, change.OldTemplateId);
        Assert.Equal(new Dictionary<int, int> { [1] = 1, [2] = 3, [3] = 3 }, change.Fingers);
        Assert.Equal("C5", change.Name);
    }

    [Fact]
    public void A_changed_fret_is_rejected()
    {
        var reply = _f.EchoReply(Verse);
        reply.Chord("s12").Frets = [-1, 3, 5, 6, -1, -1];
        Assert.Equal(AiChartRejectionCodes.FretsChanged, Judge(Verse, reply).Rejection!.Code);
    }

    [Fact]
    public void A_changed_string_is_rejected()
    {
        var reply = _f.EchoReply(Verse);
        reply.Chord("s12").Frets = [3, 5, 5, -1, -1, -1];
        reply.Chord("s12").Fingers = [1, 3, 4, -1, -1, -1];
        Assert.Equal(AiChartRejectionCodes.FretsChanged, Judge(Verse, reply).Rejection!.Code);
    }

    [Fact]
    public void A_missing_or_doubled_chord_is_rejected_as_a_changed_note_count()
    {
        var missing = _f.EchoReply(Verse);
        missing.Chords.RemoveAt(1);
        Assert.Equal(AiChartRejectionCodes.ChordCount, Judge(Verse, missing).Rejection!.Code);

        var doubled = _f.EchoReply(Verse);
        doubled.Chords.Add(doubled.Chords[0]);
        Assert.Equal(AiChartRejectionCodes.ChordCount, Judge(Verse, doubled).Rejection!.Code);

        var note = _f.EchoReply(Verse);
        note.Chords.Add(new AiChartFixture.ReplyChord { Id = "s14", Ms = 10_000, Frets = [-1, -1, -1, 5, -1, -1], Fingers = [-1, -1, -1, 1, -1, -1] });
        Assert.Equal(AiChartRejectionCodes.ChordCount, Judge(Verse, note).Rejection!.Code);
    }

    [Fact]
    public void A_changed_time_is_rejected()
    {
        var reply = _f.EchoReply(Verse);
        reply.Chord("s13").Ms += 250;
        Assert.Equal(AiChartRejectionCodes.TimingChanged, Judge(Verse, reply).Rejection!.Code);
    }

    [Fact]
    public void A_finger_on_an_open_string_is_rejected_and_so_is_a_fretted_string_without_one()
    {
        var open = _f.EchoReply(Verse);
        open.Chord("s18").Fingers = [1, 2, 3, -1, -1, -1];
        Assert.Equal(AiChartRejectionCodes.FingerOnOpenString, Judge(Verse, open).Rejection!.Code);

        var missing = _f.EchoReply(Verse);
        missing.Chord("s12").Fingers = [-1, 1, 3, -1, -1, -1];
        Assert.Equal(AiChartRejectionCodes.FingerMissing, Judge(Verse, missing).Rejection!.Code);

        var invalid = _f.EchoReply(Verse);
        invalid.Chord("s12").Fingers = [-1, 1, 3, 5, -1, -1];
        Assert.Equal(AiChartRejectionCodes.FingerInvalid, Judge(Verse, invalid).Rejection!.Code);
    }

    [Fact]
    public void An_anchor_below_the_capo_is_rejected()
    {
        var capoed = new AiChartFixture(capo: 2);
        var intro = capoed.Chunks()[0];
        var reply = capoed.EchoReply(intro);
        reply.Anchors[1].Fret = 2;

        var (_, rejection) = Judge(intro, reply, capoed);
        Assert.Equal(AiChartRejectionCodes.AnchorBelowCapo, rejection!.Code);
    }

    [Fact]
    public void The_first_anchor_must_be_at_start_and_anchors_must_name_core_sounds_in_order()
    {
        var notAtStart = _f.EchoReply(Verse);
        notAtStart.Anchors[0].At = "s12";
        Assert.Equal(AiChartRejectionCodes.AnchorAtStart, Judge(Verse, notAtStart).Rejection!.Code);

        var onContext = _f.EchoReply(Verse);
        onContext.Anchors.Add(new AiChartFixture.ReplyAnchor { At = "s10", Fret = 3, Width = 4 });
        Assert.Equal(AiChartRejectionCodes.ContextSound, Judge(Verse, onContext).Rejection!.Code);

        var unknown = _f.EchoReply(Verse);
        unknown.Anchors.Add(new AiChartFixture.ReplyAnchor { At = "s99", Fret = 3, Width = 4 });
        Assert.Equal(AiChartRejectionCodes.AnchorPlacement, Judge(Verse, unknown).Rejection!.Code);

        var backwards = _f.EchoReply(Verse);
        backwards.Anchors.Add(new AiChartFixture.ReplyAnchor { At = "s14", Fret = 5, Width = 4 });
        Assert.Equal(AiChartRejectionCodes.AnchorPlacement, Judge(Verse, backwards).Rejection!.Code);

        var offNeck = _f.EchoReply(Verse);
        offNeck.Anchors[0].Fret = 22;
        Assert.Equal(AiChartRejectionCodes.AnchorRange, Judge(Verse, offNeck).Rejection!.Code);
    }

    [Fact]
    public void A_chord_from_the_context_is_rejected_never_applied()
    {
        var reply = _f.EchoReply(Verse);
        var t = _f.TemplateById(AiChartFixture.PowerG);
        reply.Chords.Add(new AiChartFixture.ReplyChord
        {
            Id = "s11", Ms = 7000, Frets = AiChartPrompt.FretArray(t, 6), Fingers = [1, 2, 3, -1, -1, -1]
        });

        Assert.Equal(AiChartRejectionCodes.ContextSound, Judge(Verse, reply).Rejection!.Code);
    }

    [Fact]
    public void The_reader_rejects_a_reply_outside_the_schema()
    {
        Assert.False(AiChartReplyReader.TryRead("""{"anchors":[],"chords":[],"note":"x"}""", AiChartJob.FhpAndFingering, out _, out var why));
        Assert.Contains("note", why, StringComparison.Ordinal);
        Assert.False(AiChartReplyReader.TryRead("""{"chords":[]}""", AiChartJob.FhpAndFingering, out _, out _));
        Assert.False(AiChartReplyReader.TryRead("""{"chords":[{"id":"s1","ms":"0","frets":[],"name":""}]}""", AiChartJob.ChordNames, out _, out _));
        Assert.False(AiChartReplyReader.TryRead("not json", AiChartJob.ChordNames, out _, out _));
        Assert.True(AiChartReplyReader.TryRead("""{"chords":[]}""", AiChartJob.ChordNames, out _, out _));
    }

    [Fact]
    public void The_name_job_changes_names_only_and_rejects_an_unusable_one()
    {
        var reply = _f.EchoReply(Verse, AiChartJob.ChordNames);
        reply.Chord("s18").Name = "Em";

        var (plan, rejection) = Judge(Verse, reply);
        Assert.Null(rejection);
        Assert.Null(plan!.Anchors);
        var change = Assert.Single(plan.Chords);
        Assert.Equal("Em", change.Name);
        Assert.Equal(_f.TemplateById(AiChartFixture.OpenEm).Fingers, change.Fingers);

        var bad = _f.EchoReply(Verse, AiChartJob.ChordNames);
        bad.Chord("s18").Name = new string('E', 40);
        Assert.Equal(AiChartRejectionCodes.NameInvalid, Judge(Verse, bad).Rejection!.Code);
    }
}
