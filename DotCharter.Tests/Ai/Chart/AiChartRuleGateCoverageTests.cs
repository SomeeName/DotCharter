using DotCharter.Services.Ai.Chart;
using DotCharter.Services.Ai.Chat;
using DotCharter.Services.Editing.Validation.Rules;

namespace DotCharter.Tests.Ai.Chart;

/// <summary>
///     #803: the AI chart pass's rule gate ran five of the inspector's rules, and the model's own anchors
///     tripped two it left out (I20, E06). This pins, PER JOB, every production rule
///     (<see cref="ArrangementValidationService.DefaultRules" />, read by reflection) to one of two places:
///     the gate's list for that job (<see cref="AiChartRuleGate.RulesFor" />), or <see cref="NotGuarded" />
///     with the reason the job's output cannot trip it. A new production rule fails here until someone
///     decides which.
/// </summary>
public sealed class AiChartRuleGateCoverageTests
{
    private const string AnchorsAreInput = "the job cannot move an anchor, and the rule reads only anchors and notes";

    private const string NotesAndPhrases = "reads notes, techniques, phrases, sections or tones; no chunked job changes any";

    /// <summary>The rules a job's gate does NOT run, each with why its output cannot trip it.</summary>
    private static readonly Dictionary<AiChartJob, Dictionary<Type, string>> NotGuarded = new()
    {
        [AiChartJob.FhpAndFingering] = Shared(),
        [AiChartJob.FhpOnly] = Shared(),
        [AiChartJob.FingeringOnly] = WithAnchorRulesExempt(),
        [AiChartJob.ChordNames] = WithAnchorRulesExempt(),
    };

    private static Dictionary<Type, string> Shared() => new()
    {
        [typeof(PhraseRule)] = NotesAndPhrases,
        [typeof(SongEndAudioRule)] = NotesAndPhrases,
        [typeof(OpenNoteBendRule)] = NotesAndPhrases,
        [typeof(LinkNextRule)] = NotesAndPhrases,
        [typeof(SlideTechniqueRule)] = NotesAndPhrases,
        [typeof(ToneSilenceAndPhraseRule)] = NotesAndPhrases,
        [typeof(ChordOutsideHandShapeRule)] = "positions only; a chord or a hand shape is never moved in time",
        [typeof(HandShapeContentRule)] =
            "compares FRETS under a shape with the shape's template frets; a reply echoes every fret unchanged",
    };

    private static Dictionary<Type, string> WithAnchorRulesExempt()
    {
        var map = Shared();
        map[typeof(AnchorRule)] = AnchorsAreInput;
        map[typeof(PhraseAnchorRule)] = AnchorsAreInput;
        map[typeof(NoteRule)] = AnchorsAreInput + " (I42 I43 follow the anchors; its other checks read notes)";
        return map;
    }

    public static TheoryData<AiChartJob> ChunkedJobs => [.. NotGuarded.Keys];

    [Theory]
    [MemberData(nameof(ChunkedJobs))]
    public void Every_production_rule_is_in_the_jobs_gate_or_listed_with_a_reason(AiChartJob job)
    {
        var production = ArrangementValidationService.DefaultRules().Select(r => r.GetType()).ToHashSet();
        var gated = AiChartRuleGate.RulesFor(job).Select(r => r.GetType()).ToHashSet();
        var exempt = NotGuarded[job];

        var missing = production.Where(t => !gated.Contains(t) && !exempt.ContainsKey(t)).Select(t => t.Name).ToList();
        Assert.True(missing.Count == 0,
            $"{job}: production rule(s) neither in AiChartRuleGate.RulesFor nor listed with a reason: {string.Join(", ", missing)}");

        // No stale or contradictory entry: an exemption names a production rule the gate does not run.
        Assert.All(exempt, kv =>
        {
            Assert.Contains(kv.Key, production);
            Assert.DoesNotContain(kv.Key, gated);
            Assert.False(string.IsNullOrWhiteSpace(kv.Value));
        });
        Assert.All(gated, t => Assert.Contains(t, production));
    }

    [Fact]
    public void Every_chunked_job_is_pinned()
    {
        var chunked = Enum.GetValues<AiChartJob>()
            .Where(j => j.ProposesAnchors() || j.ProposesFingers() || j == AiChartJob.ChordNames);
        Assert.Equal(chunked.Order(), NotGuarded.Keys.Order());
    }

    [Fact]
    public void A_job_that_moves_anchors_runs_the_anchor_rules()
    {
        foreach (var job in new[] { AiChartJob.FhpAndFingering, AiChartJob.FhpOnly })
        {
            var gated = AiChartRuleGate.RulesFor(job).Select(r => r.GetType()).ToList();
            Assert.Contains(typeof(AnchorRule), gated);
            Assert.Contains(typeof(PhraseAnchorRule), gated);
        }
    }

    /// <summary>Fix with AI offers exactly the findings the gate can check were removed.</summary>
    [Fact]
    public void Every_finding_fix_with_ai_offers_is_one_the_gate_guards_and_back()
    {
        Assert.Equal(AiChartRuleGate.Guarded.Order(), AiChatFindings.FixableTypes.Order());
        foreach (var type in AiChartRuleGate.Guarded)
        {
            var job = AiChatFindings.JobFor(type);
            Assert.NotNull(job);
            Assert.Contains(type.GetCode(), AiChartPrompt.RuleCodes(job!.Value));
        }
    }
}
