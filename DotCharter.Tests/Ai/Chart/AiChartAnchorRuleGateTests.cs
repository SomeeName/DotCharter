using System.Globalization;
using System.Text.RegularExpressions;
using DotCharter.Tone.Ai;
using DotCharter.Services.Ai.Chart;
using DotCharter.Services.Editing.Fingering;

namespace DotCharter.Tests.Ai.Chart;

/// <summary>
///     #803: the AI chart pass's own anchors broke I20 (an anchor inside a hand shape) and E06 (a phrase
///     that plays notes with no anchor of its own), and nothing caught it. The rule gate now runs
///     <c>AnchorRule</c> and <c>PhraseAnchorRule</c> for a job that moves anchors, the prompt names both
///     rules with their codes, and a Fix-with-AI pass must REMOVE the findings it targets. On a FAKE model.
///     <para>
///         The fixture (<see cref="AiChartFixture" />): the intro (beats 0-15, a C5 hand shape over beats
///         8-13, anchors at 0 and 8) and the verse (beats 16-31, anchors at 16 and 24); one beat = 500 ms.
///     </para>
/// </summary>
public sealed partial class AiChartAnchorRuleGateTests
{
    private readonly AiChartFixture _f = new();

    /// <summary>The whole fixture as ONE chunk, so the verse's phrase start lies inside it.</summary>
    private static readonly AiChartChunkOptions Merged = new(MinSounds: 64, MaxSounds: 64, ContextBefore: 3, ContextAfter: 3);

    [GeneratedRegex(@"--- chunk starts at (\d+)ms")]
    private static partial Regex ChunkStart();

    private static bool IsRetry(ToneDesignPrompt prompt) => prompt.Messages.Count == 3;

    /// <summary>A model that echoes the chunk it is asked about, edited by <paramref name="edit" /> (which
    /// also learns whether this is the retry).</summary>
    private FakeChartModel Model(AiChartChunkOptions options, Action<AiChartChunk, AiChartFixture.ReplyDoc, bool> edit)
        => new((prompt, _) =>
        {
            var ms = int.Parse(ChunkStart().Match(prompt.Messages[0].Text).Groups[1].Value, CultureInfo.InvariantCulture);
            var chunk = _f.Chunks(options).Single(c => c.StartMs == ms);
            var reply = _f.EchoReply(chunk);
            edit(chunk, reply, IsRetry(prompt));
            return reply.Json();
        });

    private Task<AiChartPassResult> Run(FakeChartModel model, AiChartChunkOptions options,
        IReadOnlyList<AiChartFindingTarget>? targets = null)
    {
        var request = new AiChartPassRequest(AiChartJob.FhpAndFingering, ChordNamingStyle.Eof, _f.Facts, _f.Level, _f.Beats,
            Options: options);
        return new AiChartPass(new FingeringGenerationService(), concurrency: 1).RunAsync(
            request with { Targets = targets }, model, null, TestContext.Current.CancellationToken);
    }

    private static string IdAt(AiChartChunk chunk, int beat) => chunk.Core.Single(s => s.Position == AiChartFixture.At(beat)).Id;

    /// <summary>Appends an anchor: the caller's is the latest in the chunk, so time order holds.</summary>
    private static void AddAnchor(AiChartFixture.ReplyDoc reply, string at, int fret) =>
        reply.Anchors.Add(new AiChartFixture.ReplyAnchor { At = at, Fret = fret, Width = 4 });

    private static string RetryTurn(FakeChartModel model) =>
        model.Prompts.Where(IsRetry).Select(p => p.Messages[2].Text).Single();

    [Fact]
    public async Task An_anchor_inside_a_hand_shape_is_rejected_with_I20_and_retried_with_the_reason()
    {
        // The intro's first answer starts the hand again on the C5 at beat 10, inside the 8-13 shape.
        var model = Model(AiChartFixture.PerPhrase, (chunk, reply, retry) =>
        {
            if (chunk.Index == 0 && !retry) AddAnchor(reply, IdAt(chunk, 10), 3);
        });

        var result = await Run(model, AiChartFixture.PerPhrase);

        Assert.Equal(1, result.Retried);
        Assert.Empty(result.Rejected);
        var retry = RetryTurn(model);
        Assert.Contains(AiChartPrompt.RetryHeader, retry, StringComparison.Ordinal);
        Assert.Contains("(rules)", retry, StringComparison.Ordinal);
        Assert.Contains("I20", retry, StringComparison.Ordinal);
        // The retry names the rule in the prompt's own words.
        Assert.Contains(AiChartPrompt.RuleText("I20"), retry, StringComparison.Ordinal);
        var intro = result.Accepted.Single(p => p.ChunkIndex == 0);
        Assert.DoesNotContain(intro.Anchors ?? [], a => a.Position == AiChartFixture.At(10));
    }

    [Fact]
    public async Task A_populated_phrase_left_without_its_own_anchor_is_rejected_with_E06_and_retried_with_the_reason()
    {
        // One chunk over both phrases. The first answer drops the verse's anchor at its start (beat 16)
        // and moves the hand on beat 20 instead: every note still lies inside an anchor, but the verse
        // inherits the intro's hand position.
        var model = Model(Merged, (chunk, reply, retry) =>
        {
            if (retry) return;
            reply.Anchors.RemoveAll(a => a.At == IdAt(chunk, 16));
            reply.Anchors.Insert(reply.Anchors.FindIndex(a => a.At == IdAt(chunk, 24)),
                new AiChartFixture.ReplyAnchor { At = IdAt(chunk, 20), Fret = 5, Width = 4 });
        });

        var result = await Run(model, Merged);

        Assert.Equal(1, result.Retried);
        Assert.Empty(result.Rejected);
        var retry = RetryTurn(model);
        Assert.Contains("E06", retry, StringComparison.Ordinal);
        Assert.DoesNotContain("L05", retry, StringComparison.Ordinal);
        Assert.Contains(AiChartPrompt.RuleText("E06"), retry, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_fix_that_removes_the_targeted_I20_is_accepted_and_one_that_leaves_it_is_retried()
    {
        // The chart already has I20: an anchor on the C5 at beat 10, inside the 8-13 shape.
        _f.Level.Anchors.Add(new FretAnchor(AiChartFixture.At(10), 3, 4));
        var target = new AiChartFindingTarget("I20", 5000);
        // First answer: the chart as it is (I20 stays). The retry drops the anchor.
        var model = Model(AiChartFixture.PerPhrase, (chunk, reply, retry) =>
        {
            if (chunk.Index == 0 && retry) reply.Anchors.RemoveAll(a => a.At == IdAt(chunk, 10));
        });

        var result = await Run(model, AiChartFixture.PerPhrase, [target]);

        Assert.Empty(result.Rejected);
        Assert.Equal(1, result.Retried);
        var retry = RetryTurn(model);
        Assert.Contains($"({AiChartRejectionCodes.FindingLeft})", retry, StringComparison.Ordinal);
        Assert.Contains("I20 at 5.00 s", retry, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Leftovers!, i => i.Code == "I20");
    }

    [Fact]
    public async Task A_fix_that_removes_the_target_but_adds_another_finding_is_rejected_and_nothing_is_kept()
    {
        _f.Level.Anchors.Add(new FretAnchor(AiChartFixture.At(10), 3, 4));
        var target = new AiChartFindingTarget("I20", 5000);
        // Both answers move the bad anchor from beat 10 to beat 12: still inside the shape, a NEW I20.
        var model = Model(AiChartFixture.PerPhrase, (chunk, reply, _) =>
        {
            if (chunk.Index != 0) return;
            var at10 = reply.Anchors.Single(a => a.At == IdAt(chunk, 10));
            at10.At = IdAt(chunk, 12);
        });

        var result = await Run(model, AiChartFixture.PerPhrase, [target]);

        var rejection = Assert.Single(result.Rejected);
        Assert.Equal(0, rejection.ChunkIndex);
        Assert.Equal(AiChartRejectionCodes.Rules, rejection.Code);
        Assert.Contains("I20", rejection.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Accepted, p => p.ChunkIndex == 0);
        Assert.Contains($"({AiChartRejectionCodes.Rules})", RetryTurn(model), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_findings_left_in_the_range_are_reported_with_the_result()
    {
        // The verse plays frets 7 and 8 under an anchor at fret 3 (L05) and the C5 at beat 26 under the
        // fret-1 anchor (L05, E04); an echo changes none of it.
        var model = Model(AiChartFixture.PerPhrase, (_, _, _) => { });

        var result = await Run(model, AiChartFixture.PerPhrase);

        Assert.NotNull(result.Leftovers);
        Assert.Contains(result.Leftovers!, i => i.Code == "L05");
        Assert.All(result.Leftovers!, i => Assert.Contains(i.Type, AiChartRuleGate.Guarded));
    }

    [Theory]
    [InlineData(AiChartJob.FhpAndFingering, new[] { "I20", "E06", "L05", "E04", "L06", "I45" }, new string[0])]
    [InlineData(AiChartJob.FhpOnly, new[] { "I20", "I21", "E06", "L02", "E04", "I46" }, new[] { "L06", "I45" })]
    [InlineData(AiChartJob.FingeringOnly, new[] { "L06", "L07", "L08", "I44", "I45", "E04", "I46" }, new[] { "I20", "E06" })]
    public void The_system_prompt_lists_the_rules_the_job_can_trip_with_their_codes(AiChartJob job, string[] listed,
        string[] absent)
    {
        var system = AiChartPrompt.System(job, ChordNamingStyle.Eof);

        Assert.DoesNotContain("{RULES}", system, StringComparison.Ordinal);
        foreach (var code in listed) Assert.Contains("- " + AiChartPrompt.RuleText(code), system, StringComparison.Ordinal);
        foreach (var code in absent) Assert.DoesNotContain("- " + code + ":", system, StringComparison.Ordinal);
        // Every code the prompt names is one the gate guards, so a retry never names an unknown rule.
        foreach (var code in AiChartPrompt.RuleCodes(job))
            Assert.Contains(AiChartRuleGate.Guarded, t => t.GetCode() == code);
    }

    [Fact]
    public void A_job_that_moves_anchors_is_shown_the_phrase_starts_and_the_sounds_inside_a_hand_shape()
    {
        var chunk = Assert.Single(_f.Chunks(Merged));

        var fhp = AiChartPrompt.Build(AiChartJob.FhpOnly, ChordNamingStyle.Eof, _f.Facts, _f.Level, _f.Beats, chunk)
            .Messages[0].Text;
        var fingering = AiChartPrompt.Build(AiChartJob.FingeringOnly, ChordNamingStyle.Eof, _f.Facts, _f.Level, _f.Beats, chunk)
            .Messages[0].Text;

        Assert.Contains("--- phrase \"verse\" starts at 8000ms: it needs its own anchor (E06) ---", fhp, StringComparison.Ordinal);
        // The C5s on beats 10 and 12 sit strictly inside the 8-13 shape; the one on beat 8 opens it.
        Assert.Contains(IdAt(chunk, 10) + " 5000ms chord", fhp, StringComparison.Ordinal);
        var inShape = fhp.Split('\n').Where(l => l.Contains(" in-shape", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, inShape.Count);
        Assert.Contains(inShape, l => l.StartsWith(IdAt(chunk, 10) + " ", StringComparison.Ordinal));
        Assert.Contains(inShape, l => l.StartsWith(IdAt(chunk, 12) + " ", StringComparison.Ordinal));
        // The fingering job cannot move an anchor: its listing is not marked.
        Assert.DoesNotContain("in-shape", fingering, StringComparison.Ordinal);
        Assert.DoesNotContain("--- phrase", fingering, StringComparison.Ordinal);
    }
}
