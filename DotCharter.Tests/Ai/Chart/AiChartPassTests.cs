using System.Globalization;
using System.Text.RegularExpressions;
using DotCharter.Tone.Ai;
using DotCharter.Services.Ai.Chart;
using DotCharter.Services.Editing.Fingering;

namespace DotCharter.Tests.Ai.Chart;

/// <summary>
///     The whole pass over a FAKE model (no network, no CLI): one request per chunk, a
///     rejected chunk is counted and never applied, and context is never applied.
/// </summary>
public sealed partial class AiChartPassTests
{
    private readonly AiChartFixture _f = new();

    [GeneratedRegex(@"--- chunk starts at (\d+)ms")]
    private static partial Regex ChunkStart();

    /// <summary>Answers each prompt with <paramref name="edit" /> applied to the echo of
    /// the chunk the prompt is about (found by its start time).</summary>
    private FakeChartModel Model(Action<AiChartChunk, AiChartFixture.ReplyDoc>? edit = null, AiChartJob job = AiChartJob.FhpAndFingering)
        => new((prompt, _) =>
        {
            var ms = int.Parse(ChunkStart().Match(prompt.Messages[0].Text).Groups[1].Value, CultureInfo.InvariantCulture);
            var chunk = _f.Chunks().Single(c => c.StartMs == ms);
            var reply = _f.EchoReply(chunk, job);
            edit?.Invoke(chunk, reply);
            return reply.Json();
        });

    private Task<AiChartPassResult> Run(IToneDesignModel model, AiChartJob job = AiChartJob.FhpAndFingering)
        => new AiChartPass(new FingeringGenerationService(), concurrency: 2).RunAsync(
            new AiChartPassRequest(job, ChordNamingStyle.Eof, _f.Facts, _f.Level, _f.Beats, Options: AiChartFixture.PerPhrase),
            model, null, TestContext.Current.CancellationToken);

    [Fact]
    public async Task One_request_per_chunk_with_one_system_prompt_and_the_strict_schema()
    {
        var model = Model();
        var result = await Run(model);

        Assert.Equal(2, model.Calls);
        Assert.Equal(2, result.ChunkCount);
        Assert.Single(model.Prompts.Select(p => p.System).Distinct());
        Assert.All(model.Prompts, p => Assert.Equal(AiChartPrompt.FingeringSchema, p.JsonSchema));
        Assert.Equal(2, result.Accepted.Count);
        Assert.Empty(result.Rejected);
        Assert.Equal(200, result.InputTokens);
        Assert.Equal("fake-model", result.ModelId);
    }

    [Fact]
    public async Task The_listing_shows_context_as_ctx_and_offers_only_core_chords_for_fixing()
    {
        var model = Model();
        await Run(model);

        var verse = model.Prompts.Select(p => p.Messages[0].Text).Single(t => t.Contains("--- chunk starts at 8000ms", StringComparison.Ordinal));
        Assert.Contains("ctx s11 7000ms chord frets [3,5,5,-1,-1,-1] fingers [1,3,4,-1,-1,-1]\n", verse.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Contains("s12 8000ms chord frets [-1,3,5,5,-1,-1] fingers [-1,1,3,4,-1,-1] [fix]", verse, StringComparison.Ordinal);
        Assert.Contains("tuning E A D G B e (lowest first), capo 0", verse, StringComparison.Ordinal);
        Assert.DoesNotContain("ctx s11 7000ms chord frets [3,5,5,-1,-1,-1] fingers [1,3,4,-1,-1,-1] [fix]", verse, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_reply_that_touches_context_rejects_its_chunk_only_and_is_counted()
    {
        var model = Model((chunk, reply) =>
        {
            if (chunk.Index != 1) return;
            reply.Chord("s12").Fingers = [-1, 1, 3, 3, -1, -1];
            reply.Anchors.Add(new AiChartFixture.ReplyAnchor { At = chunk.Before[^1].Id, Fret = 2, Width = 4 });
        });

        var result = await Run(model);

        var rejection = Assert.Single(result.Rejected);
        Assert.Equal(1, rejection.ChunkIndex);
        Assert.Equal(AiChartRejectionCodes.ContextSound, rejection.Code);
        Assert.Equal(0, Assert.Single(result.Accepted).ChunkIndex);
    }

    [Fact]
    public async Task Accepted_plans_never_reach_outside_their_own_core()
    {
        // The model re-fingers every power chord it is offered.
        var model = Model((chunk, reply) =>
        {
            foreach (var c in reply.Chords.Where(c => c.Frets.SequenceEqual([-1, 3, 5, 5, -1, -1])))
                c.Fingers = [-1, 1, 3, 3, -1, -1];
        });

        var result = await Run(model);
        var chunks = _f.Chunks();

        foreach (var plan in result.Accepted)
        {
            var chunk = chunks[plan.ChunkIndex];
            var coreChords = chunk.Core.Where(s => s.Chord is not null).Select(s => s.ChordIndex).ToHashSet();
            Assert.All(plan.Chords, c => Assert.Contains(c.ChordIndex, coreChords));
            Assert.All(plan.Anchors ?? [], a => Assert.True(chunk.Contains(a.Position)));
        }

        Assert.Equal(6, result.Accepted.Sum(p => p.Chords.Count));
    }

    [Fact]
    public async Task A_provider_fault_on_one_chunk_is_a_rejection_but_a_sign_in_fault_stops_the_pass()
    {
        var calls = 0;
        var flaky = new FakeChartModel((prompt, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
                throw new ToneDesignException(ToneDesignFault.Network, "timed out");
            return Model().CompleteAsync(prompt, CancellationToken.None).Result.Json;
        });
        var result = await Run(flaky);
        Assert.Equal(AiChartRejectionCodes.ModelFailed, Assert.Single(result.Rejected).Code);
        Assert.Single(result.Accepted);

        var signedOut = new FakeChartModel((_, _) =>
            throw new ToneDesignException(ToneDesignFault.Authentication, "not signed in"));
        await Assert.ThrowsAsync<ToneDesignException>(() => Run(signedOut));
    }

    [Fact]
    public async Task The_name_job_uses_its_own_schema_and_lists_sounding_notes()
    {
        var model = Model((_, reply) => { foreach (var c in reply.Chords.Where(c => c.Name == "Emin")) c.Name = "Em"; },
            AiChartJob.ChordNames);

        var result = await Run(model, AiChartJob.ChordNames);

        Assert.All(model.Prompts, p => Assert.Equal(AiChartPrompt.NamesSchema, p.JsonSchema));
        Assert.Contains(model.Prompts, p => p.Messages[0].Text.Contains("notes E B E G B E", StringComparison.Ordinal));
        var change = Assert.Single(result.Accepted.SelectMany(p => p.Chords));
        Assert.Equal("Em", change.Name);
    }
}
