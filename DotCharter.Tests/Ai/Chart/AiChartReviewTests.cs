using System.Globalization;
using System.Text.RegularExpressions;
using DotCharter.Tone.Ai;
using DotCharter.Services.Ai.Chart;
using DotCharter.Services.Editing.Fingering;

namespace DotCharter.Tests.Ai.Chart;

/// <summary>
///     Phase 3 of the AI chart pass (#506): the proposals are HELD for review, the charter
///     accepts or rejects them per section, the accepted ones are written as ONE undo node,
///     and the marks the review publishes are exactly what that write changes. On a FAKE
///     model: no network, no provider.
///     <para>
///         The fixture is cut one chunk per phrase: section 0 is the intro (beats 0-15: the
///         C5 chords on 8 10 12 under a C5 hand shape, a G5 on 14), section 1 the verse
///         (beats 16-31: C5 on 16 18 26, an open Em on 24). The model re-fingers every C5
///         with a ring-finger barre in both, and widens the verse's first anchor to six frets.
///     </para>
/// </summary>
public sealed partial class AiChartReviewTests
{
    private readonly AiChartFixture _f = new();
    private readonly ProjectState _state = new();
    private readonly HistoryService _history = new(new FakeAppLogger());
    private readonly FakeLocalization _loc = FakeLocalization.English();
    private readonly RigConfig _config = new() { Ai = new AiProviderSettings(AiProviderKind.ClaudeCli, "fake-model", null) };

    public AiChartReviewTests()
    {
        _state.ActiveProject = _f.Project;
        _state.ActiveArrangement = _f.Arrangement;
        _state.ActiveLevel = _f.Level;
    }

    [GeneratedRegex(@"--- chunk starts at (\d+)ms")]
    private static partial Regex ChunkStart();

    /// <summary>The ring-finger barre on every C5, and the verse's first anchor six frets wide.</summary>
    internal static void Proposal(AiChartChunk chunk, AiChartFixture.ReplyDoc reply)
    {
        foreach (var c in reply.Chords.Where(c => c.Frets.SequenceEqual([-1, 3, 5, 5, -1, -1])))
            c.Fingers = [-1, 1, 3, 3, -1, -1];
        if (chunk.Index == 1) reply.Anchors[0].Width = 6;
    }

    private FakeChartModel Model(Action<AiChartChunk, AiChartFixture.ReplyDoc>? edit = null)
        => Model((_, chunk, reply) => (edit ?? Proposal)(chunk, reply));

    /// <summary>A model whose reply edit also sees the prompt it answers (a retry has three turns).</summary>
    private FakeChartModel Model(Action<ToneDesignPrompt, AiChartChunk, AiChartFixture.ReplyDoc> edit)
        => new((prompt, _) =>
        {
            var ms = int.Parse(ChunkStart().Match(prompt.Messages[0].Text).Groups[1].Value, CultureInfo.InvariantCulture);
            var chunk = _f.Chunks().Single(c => c.StartMs == ms);
            var reply = _f.EchoReply(chunk);
            edit(prompt, chunk, reply);
            return reply.Json();
        });

    private async Task<AiChartPassRun> Held(FakeChartModel? model = null)
    {
        var run = new AiChartPassRun(_state, _history, null, _loc, new FakeAppLogger(), new FingeringGenerationService(),
            new Factory(model ?? Model()), () => _config, () => null)
        {
            ChunkOptions = AiChartFixture.PerPhrase
        };
        await run.RunAsync(AiChartJob.FhpAndFingering);
        return run;
    }

    [Fact]
    public async Task The_proposals_are_held_and_the_chart_is_untouched_until_apply()
    {
        var before = Fingerprint.Of(_f);

        var run = await Held();

        var review = Assert.IsType<AiChartReview>(run.Review);
        Assert.Equal([0, 1], review.Chunks.Select(c => c.Plan.ChunkIndex));
        Assert.All(review.Chunks, c => Assert.Equal(AiChartDecision.Pending, c.Decision));
        Assert.Equal(_loc.Format("AiChart_ReviewReady", 2, 0), _state.MainDisplayMessage);
        Assert.Equal(before, Fingerprint.Of(_f));
        Assert.False(_history.CanUndo);

        // Deciding writes nothing either; only Apply does.
        review.AcceptAll();
        Assert.Equal(before, Fingerprint.Of(_f));
        Assert.False(_history.CanUndo);
    }

    [Fact]
    public async Task Reject_all_rejects_only_the_undecided_and_keeps_a_hand_accepted_section()
    {
        var run = await Held();
        var review = run.Review!;
        review.Decide(review.Chunks[1], AiChartDecision.Accepted);

        review.RejectAll();

        Assert.Equal([AiChartDecision.Rejected, AiChartDecision.Accepted], review.Chunks.Select(c => c.Decision));
        Assert.Equal((1, 1, 0), (review.AcceptedCount, review.RejectedCount, review.PendingCount));
        Assert.Equal([review.Chunks[1].Plan], review.AcceptedPlans);
    }

    [Fact]
    public async Task Discard_writes_nothing_and_closes_the_review()
    {
        var before = Fingerprint.Of(_f);
        var run = await Held();
        run.Review!.AcceptAll();

        run.DiscardReview();

        Assert.Null(run.Review);
        Assert.False(run.ApplyReview());
        Assert.Equal(before, Fingerprint.Of(_f));
        Assert.False(_history.CanUndo);
        Assert.Equal(_loc["AiChart_ReviewDiscarded"], _state.MainDisplayMessage);
    }

    [Fact]
    public async Task Accepting_a_subset_writes_exactly_those_sections_as_one_undo_node()
    {
        var before = Fingerprint.Of(_f);
        var run = await Held();
        var review = run.Review!;
        review.Decide(review.Chunks[0], AiChartDecision.Rejected);
        review.Decide(review.Chunks[1], AiChartDecision.Accepted);

        Assert.True(run.ApplyReview());

        // The verse's three C5 moved to ONE minted barre template; the intro's did not move.
        var barre = _f.Level.Chords[4].TemplateId;
        Assert.NotEqual(AiChartFixture.PowerC, barre);
        Assert.Equal([AiChartFixture.PowerC, AiChartFixture.PowerC, AiChartFixture.PowerC],
            _f.Level.Chords.Take(3).Select(c => c.TemplateId));
        Assert.Equal(barre, _f.Level.Chords[5].TemplateId);
        Assert.Equal(barre, _f.Level.Chords[7].TemplateId);
        Assert.Equal(AiChartFixture.PowerC, _f.Level.HandShapes[0].TemplateId);
        Assert.Equal(4, _f.Arrangement.ChordTemplates.Count);
        Assert.Contains(_f.Level.Anchors, a => a.Position == AiChartFixture.At(16) && a.Width == 6);
        Assert.Contains(_f.Level.Anchors, a => a.Position == AiChartFixture.At(8) && a is { Fret: 3, Width: 4 });

        // ONE node: a single Ctrl+Z restores everything, and there is nothing under it.
        _history.Undo();
        Assert.False(_history.CanUndo);
        Assert.Equal(before, Fingerprint.Of(_f));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task The_marks_are_exactly_what_the_apply_changes(bool intro, bool verse)
    {
        var run = await Held();
        var review = run.Review!;
        review.Decide(review.Chunks[0], intro ? AiChartDecision.Accepted : AiChartDecision.Rejected);
        review.Decide(review.Chunks[1], verse ? AiChartDecision.Accepted : AiChartDecision.Rejected);
        var marks = review.Marks();
        var templateIds = TemplateIds(_f.Level);
        var anchorsBefore = _f.Level.Anchors.ToList();

        Assert.True(run.ApplyReview());

        var changed = templateIds.Where(kv => CurrentTemplateId(kv.Key) != kv.Value).Select(kv => kv.Key).ToHashSet(
            ReferenceEqualityComparer.Instance);
        Assert.True(changed.SetEquals(marks.Chords.Cast<object>().Concat(marks.HandShapes)),
            $"marked {marks.Chords.Count} chords + {marks.HandShapes.Count} hand shapes, the write changed {changed.Count}");
        Assert.All(marks.RemovedAnchors, a => Assert.Contains(a, anchorsBefore));
        var (removed, added) = AnchorDifference(anchorsBefore, _f.Level.Anchors);
        Assert.Equal(removed, marks.RemovedAnchors.Select(Value).Order());
        Assert.Equal(added, marks.AddedAnchors.Select(Value).Order());
        Assert.NotEqual(0, marks.Count);
        // The hand shape over the intro's C5 is marked exactly when the intro is written.
        Assert.Equal(intro, marks.HandShapes.Contains(_f.Level.HandShapes[0]));
        Assert.Equal(verse, marks.AddedAnchors.Any(a => a.Width == 6));
    }

    [Fact]
    public async Task Pending_sections_are_marked_but_never_written()
    {
        var run = await Held();
        var review = run.Review!;

        Assert.Equal(6, review.Marks().Chords.Count);
        review.Decide(review.Chunks[0], AiChartDecision.Rejected);
        Assert.Equal(3, review.Marks().Chords.Count);
        Assert.All(review.Marks().Chords, c => Assert.True(c.Position >= AiChartFixture.At(16)));

        // Section 1 is still pending: Apply writes nothing.
        var before = Fingerprint.Of(_f);
        Assert.False(run.ApplyReview());
        Assert.Equal(before, Fingerprint.Of(_f));
        Assert.False(_history.CanUndo);
    }

    [Fact]
    public async Task A_section_edited_while_it_waits_is_stale_unmarked_and_unwritten()
    {
        var run = await Held();
        var review = run.Review!;
        review.AcceptAll();

        // The charter moves the verse's second hand position while the review is open.
        var em = _f.Level.Anchors.Single(a => a.Position == AiChartFixture.At(24));
        _f.Level.Anchors.Remove(em);
        _f.Level.Anchors.Add(new FretAnchor(AiChartFixture.At(24), 2, 4));

        Assert.All(review.Marks().Chords, c => Assert.True(c.Position < AiChartFixture.At(16)));
        Assert.True(run.ApplyReview());
        Assert.Equal(AiChartFixture.PowerC, _f.Level.Chords[4].TemplateId);
        Assert.NotEqual(AiChartFixture.PowerC, _f.Level.Chords[0].TemplateId);
        Assert.Contains(_f.Level.Anchors, a => a.Position == AiChartFixture.At(16) && a is { Fret: 3, Width: 4 });
    }

    [Fact]
    public async Task A_chunk_whose_reply_is_rejected_is_asked_once_more_with_the_reason()
    {
        // The verse's first reply moves a fret; its retry is right.
        var model = Model((prompt, chunk, reply) =>
        {
            Proposal(chunk, reply);
            if (chunk.Index == 1 && prompt.Messages.Count == 1) reply.Chords[0].Frets[1] = 4;
        });
        var run = await Held(model);

        var verse = model.Prompts.Where(p => p.Messages[0].Text.Contains("--- chunk starts at 8000ms", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(2, verse.Count);
        var first = verse.Single(p => p.Messages.Count == 1);
        var retry = verse.Single(p => p.Messages.Count == 3);
        Assert.DoesNotContain(AiChartPrompt.RetryHeader, first.Messages[0].Text, StringComparison.Ordinal);
        Assert.Equal(ToneDesignRole.Assistant, retry.Messages[1].Role);
        Assert.Contains("\"frets\":[-1,4,5,5,-1,-1]", retry.Messages[1].Text, StringComparison.Ordinal);
        Assert.StartsWith($"{AiChartPrompt.RetryHeader} ({AiChartRejectionCodes.FretsChanged})", retry.Messages[2].Text,
            StringComparison.Ordinal);
        Assert.Equal(1, Count(retry.Messages.Select(m => m.Text), AiChartPrompt.RetryHeader));

        // The intro answered right the first time: asked once.
        Assert.Single(model.Prompts, p => p.Messages[0].Text.Contains("--- chunk starts at 0ms", StringComparison.Ordinal));
        Assert.Equal(1, run.Review!.Result.Retried);
        Assert.Empty(run.Review.Result.Rejected);
        Assert.Equal(2, run.Review.Chunks.Count);
    }

    [Fact]
    public async Task A_retry_that_is_rejected_again_is_final_and_never_asked_a_third_time()
    {
        var model = Model((chunk, reply) =>
        {
            Proposal(chunk, reply);
            if (chunk.Index == 1) reply.Chords[0].Frets[1] = 4;
        });

        var run = await Held(model);

        var verse = model.Prompts.Where(p => p.Messages[0].Text.Contains("--- chunk starts at 8000ms", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(2, verse.Count);
        Assert.Equal(1, verse.Count(p => p.Messages.Count == 3));
        Assert.Equal(1, Count(model.Prompts.SelectMany(p => p.Messages).Select(m => m.Text), AiChartPrompt.RetryHeader));
        var review = run.Review!;
        Assert.Equal(1, review.Result.Retried);
        Assert.Equal(AiChartRejectionCodes.FretsChanged, Assert.Single(review.Result.Rejected).Code);
        Assert.Equal([0], review.Chunks.Select(c => c.Plan.ChunkIndex));
    }

    [Fact]
    public async Task A_model_that_did_not_answer_is_not_retried()
    {
        var calls = 0;
        var model = new FakeChartModel((prompt, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1) throw new ToneDesignException(ToneDesignFault.Network, "timed out");
            return Model().CompleteAsync(prompt, CancellationToken.None).Result.Json;
        });

        var run = await Held(model);

        Assert.Equal(2, model.Calls); // one per chunk; the failed one is not asked again
        Assert.Equal(0, run.Review!.Result.Retried);
        Assert.Equal(AiChartRejectionCodes.ModelFailed, Assert.Single(run.Review.Result.Rejected).Code);
    }

    // ---- helpers ------------------------------------------------------------------

    private static int Count(IEnumerable<string> texts, string needle)
        => texts.Sum(t => Regex.Matches(t, Regex.Escape(needle)).Count);

    private static Dictionary<object, int> TemplateIds(ChartLevel level)
    {
        var ids = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
        foreach (var c in level.Chords) ids[c] = c.TemplateId;
        foreach (var h in level.HandShapes) ids[h] = h.TemplateId;
        return ids;
    }

    private static int CurrentTemplateId(object o) => o is Chord c ? c.TemplateId : ((HandShape)o).TemplateId;

    private static (FractionalPosition, int, int) Value(FretAnchor a) => (a.Position, a.Fret, a.Width);

    private static (List<(FractionalPosition, int, int)> Removed, List<(FractionalPosition, int, int)> Added)
        AnchorDifference(IEnumerable<FretAnchor> before, IEnumerable<FretAnchor> after)
    {
        var removed = before.Select(Value).ToList();
        var added = new List<(FractionalPosition, int, int)>();
        foreach (var a in after.Select(Value))
        {
            if (!removed.Remove(a)) added.Add(a);
        }

        return (removed.Order().ToList(), added.Order().ToList());
    }

    /// <summary>Everything the pass may write, by value: anchors, every chord's and hand
    /// shape's template id, and the template list.</summary>
    internal sealed record Fingerprint(string Text)
    {
        public static Fingerprint Of(AiChartFixture f)
        {
            var anchors = string.Join(";", f.Level.Anchors.OrderBy(a => a.Position)
                .Select(a => string.Create(CultureInfo.InvariantCulture, $"{a.Position}:{a.Fret}/{a.Width}")));
            var chords = string.Join(",", f.Level.Chords.Select(c => c.TemplateId));
            var shapes = string.Join(",", f.Level.HandShapes.Select(h => h.TemplateId));
            var templates = string.Join(";", f.Arrangement.ChordTemplates.Select(t =>
                $"{t.Id}:{t.Name}:{string.Join(",", t.Fingers.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"))}"));
            return new Fingerprint($"{anchors}|{chords}|{shapes}|{templates}");
        }
    }

    private sealed class Factory(IToneDesignModel model) : IToneDesignModelFactory
    {
        public IToneDesignModel Create(AiProviderSettings settings) => model;

        public bool Supports(AiProviderKind kind) => kind != AiProviderKind.None;
    }
}
