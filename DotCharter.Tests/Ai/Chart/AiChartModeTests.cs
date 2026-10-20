using System.Globalization;
using System.Text.RegularExpressions;
using DotCharter.Tone.Ai;
using DotCharter.Services.Ai.Chart;
using DotCharter.Services.Editing.Anchors;
using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.FretAnchors;

namespace DotCharter.Tests.Ai.Chart;

/// <summary>
///     The charter's three FHP / fingering modes (#497, owner decision 2026-10-09), through
///     the whole verb on a FAKE model: <b>FHP only</b> never asks the model about a finger
///     and lets <see cref="ChordFingeringFollower" /> keep free fingerings consistent;
///     <b>fingering only</b> never moves an anchor and rejects a fingering that contradicts
///     one (E04). The joint mode is pinned by <see cref="AiChartPassRunTests" />.
/// </summary>
public sealed partial class AiChartModeTests
{
    /// <summary>The double stop added for these tests: D string fret 7, G string fret 9.</summary>
    private const int DoubleStop = 3;

    private static readonly FractionalPosition StopAt = AiChartFixture.At(28);

    private readonly AiChartFixture _f = new();
    private readonly ProjectState _state = new();
    private readonly HistoryService _history = new(new FakeAppLogger());
    private readonly FakeLocalization _loc = FakeLocalization.English();
    private readonly RigConfig _config = new() { Ai = new AiProviderSettings(AiProviderKind.ClaudeCli, "fake-model", null) };

    public AiChartModeTests()
    {
        _state.ActiveProject = _f.Project;
        _state.ActiveArrangement = _f.Arrangement;
        _state.ActiveLevel = _f.Level;
    }

    [GeneratedRegex(@"--- chunk starts at (\d+)ms")]
    private static partial Regex ChunkStart();

    /// <summary>Adds the double stop at beat 28 under its own anchor on the grip's index fret.
    /// <paramref name="fingers" /> null = the suggestion (a FREE fingering).</summary>
    private ChordTemplate AddDoubleStop(Dictionary<int, int>? fingers = null)
    {
        var template = new ChordTemplate { Id = DoubleStop, Name = "", Frets = new() { [2] = 7, [3] = 9 } };
        if (fingers is null) ChordTemplateFingerSetter.SetSuggestedFingers(template, 6);
        else template.Fingers = fingers;
        _f.Arrangement.ChordTemplates.Add(template);

        var chord = new Chord { Position = StopAt, TemplateId = DoubleStop };
        foreach (var (s, fret) in template.Frets) chord.ChordNotes.Add(new ChordNote { StringIndex = s, Fret = fret });
        _f.Level.Chords.Add(chord);
        _f.Level.Anchors.Add(new FretAnchor(StopAt, IndexFingerPosition.Of(template)!.Value, 4));
        return template;
    }

    private FakeChartModel Model(AiChartJob job, Action<AiChartChunk, AiChartFixture.ReplyDoc>? edit = null)
        => new((prompt, _) =>
        {
            var ms = int.Parse(ChunkStart().Match(prompt.Messages[0].Text).Groups[1].Value, CultureInfo.InvariantCulture);
            var chunk = _f.Chunks(AiChartChunkOptions.Default).Single(c => c.StartMs == ms);
            var reply = _f.EchoReply(chunk, job);
            edit?.Invoke(chunk, reply);
            return reply.Json();
        });

    /// <summary>The verb, then "Accept all" and "Apply" on its review (#506): these cases judge
    /// what the pass would write.</summary>
    private async Task Run(FakeChartModel model, AiChartJob job)
    {
        var run = new AiChartPassRun(_state, _history, null, _loc, new FakeAppLogger(), new FingeringGenerationService(),
            new Factory(model), () => _config, () => null);
        await run.RunAsync(job);
        run.Review?.AcceptAll();
        run.ApplyReview();
    }

    /// <summary>The reply's anchor ON the double stop, moved one fret down the neck.</summary>
    private static Action<AiChartChunk, AiChartFixture.ReplyDoc> LowerTheStopAnchor(int indexFret)
        => (chunk, reply) =>
        {
            var id = chunk.Core.Single(s => s.Position == StopAt).Id;
            reply.Anchors.Single(a => a.At == id).Fret = indexFret - 1;
        };

    private List<(FractionalPosition, int, int)> Anchors()
        => _f.Level.Anchors.OrderBy(a => a.Position).Select(a => (a.Position, a.Fret, a.Width)).ToList();

    // ---- FHP only ------------------------------------------------------------------------

    [Fact]
    public async Task Fhp_only_asks_for_anchors_alone_and_never_shows_the_model_a_free_fingering()
    {
        var free = AddDoubleStop();
        var model = Model(AiChartJob.FhpOnly);

        await Run(model, AiChartJob.FhpOnly);

        var prompt = Assert.Single(model.Prompts);
        Assert.Equal(AiChartPrompt.FhpSchema, prompt.JsonSchema);
        Assert.DoesNotContain("\"fingers\"", prompt.JsonSchema, StringComparison.Ordinal);
        Assert.DoesNotContain("[fix]", prompt.Messages[0].Text, StringComparison.Ordinal);
        var line = prompt.Messages[0].Text.ReplaceLineEndings("\n").Split('\n')
            .Single(l => l.Contains("chord frets [-1,-1,7,9,-1,-1]", StringComparison.Ordinal));
        Assert.DoesNotContain("fingers", line, StringComparison.Ordinal);
        Assert.Equal(0, ChordFingeringFollower.SuggestedShift(free, 6));
    }

    [Fact]
    public void Fhp_only_reads_a_reply_that_carries_chords_as_malformed()
    {
        const string withFingers = """{"anchors":[{"at":"start","fret":5,"width":4}],"chords":[]}""";

        Assert.False(AiChartReplyReader.TryRead(withFingers, AiChartJob.FhpOnly, out _, out var why));
        Assert.Contains("chords", why, StringComparison.Ordinal);
        Assert.True(AiChartReplyReader.TryRead("""{"anchors":[{"at":"start","fret":5,"width":4}]}""", AiChartJob.FhpOnly,
            out var reply, out _));
        Assert.Empty(reply!.Chords);
    }

    [Fact]
    public async Task Fhp_only_a_free_fingering_follows_the_moved_anchor_through_a_variant_in_one_undo_step()
    {
        var shared = AddDoubleStop();
        var index = IndexFingerPosition.Of(shared)!.Value;
        var sharedFingers = new Dictionary<int, int>(shared.Fingers);
        var anchorsBefore = Anchors();
        var templatesBefore = _f.Arrangement.ChordTemplates.Count;

        await Run(Model(AiChartJob.FhpOnly, LowerTheStopAnchor(index)), AiChartJob.FhpOnly);

        Assert.Equal(StatusSeverity.Success, _state.MainDisplaySeverity);
        Assert.Contains(_f.Level.Anchors, a => a.Position == StopAt && a.Fret == index - 1);
        var chord = _f.Level.Chords.Single(c => c.Position == StopAt);
        Assert.NotEqual(DoubleStop, chord.TemplateId);
        var variant = _f.Arrangement.ChordTemplates.Single(t => t.Id == chord.TemplateId);
        // The base grip one finger up the hand, so the index (on no string now, inferred
        // from the lowest finger, #452) sits on the new anchor; the SHARED template is untouched.
        Assert.All(shared.Frets.Keys, s => Assert.Equal(sharedFingers[s] + 1, variant.Fingers[s]));
        Assert.Equal(index - 1, IndexFingerPosition.Of(variant));
        Assert.True(ChordFingeringFollower.IsMintedVariant(variant));
        Assert.Equal(sharedFingers, shared.Fingers);
        Assert.Same(shared, _f.TemplateById(DoubleStop));

        // Anchors, the minted template and the repoint are ONE node.
        _history.Undo();
        Assert.False(_history.CanUndo);
        Assert.Equal(anchorsBefore, Anchors());
        Assert.Equal(DoubleStop, chord.TemplateId);
        Assert.Equal(templatesBefore, _f.Arrangement.ChordTemplates.Count);
    }

    [Fact]
    public async Task Fhp_only_an_authored_fingering_is_never_refingered_and_a_reply_that_contradicts_it_is_rejected()
    {
        // Index on 7, pinky on 9: not the suggestion and no shift of it, so AUTHORED.
        var authored = AddDoubleStop(new Dictionary<int, int> { [2] = 1, [3] = 4 });
        Assert.Null(ChordFingeringFollower.SuggestedShift(authored, 6));
        var anchorsBefore = Anchors();
        var model = Model(AiChartJob.FhpOnly, LowerTheStopAnchor(7));

        await Run(model, AiChartJob.FhpOnly);

        // Shown to the model as fixed, because it pins its anchor: in the first prompt and in
        // the one retry the rejection earns (#506), and never a third time.
        Assert.Equal(2, model.Prompts.Count);
        Assert.All(model.Prompts, p => Assert.Contains("chord frets [-1,-1,7,9,-1,-1] fingers [-1,-1,1,4,-1,-1] fixed",
            p.Messages[0].Text, StringComparison.Ordinal));
        Assert.StartsWith(_loc.Format("AiChart_AllRejected", 1, "").TrimEnd(), _state.MainDisplayMessage,
            StringComparison.Ordinal);
        Assert.Contains("E04", _state.MainDisplayMessage, StringComparison.Ordinal);
        Assert.False(_history.CanUndo);
        Assert.Equal(anchorsBefore, Anchors());
        Assert.Equal(DoubleStop, _f.Level.Chords.Single(c => c.Position == StopAt).TemplateId);
    }

    // ---- fingering only ------------------------------------------------------------------

    [Fact]
    public async Task Fingering_only_changes_fingers_and_never_an_anchor()
    {
        var anchorsBefore = Anchors();
        // The C5s under the fret-3 anchor get a ring-finger barre (index still on 3).
        var model = Model(AiChartJob.FingeringOnly, (_, reply) =>
        {
            foreach (var c in reply.Chords.Where(c => c.Frets.SequenceEqual([-1, 3, 5, 5, -1, -1]) && c.Ms < 13000))
                c.Fingers = [-1, 1, 3, 3, -1, -1];
        });

        await Run(model, AiChartJob.FingeringOnly);

        var prompt = Assert.Single(model.Prompts);
        Assert.Equal(AiChartPrompt.FingeringOnlySchema, prompt.JsonSchema);
        Assert.DoesNotContain("\"anchors\"", prompt.JsonSchema, StringComparison.Ordinal);
        Assert.Equal(_loc.Format("AiChart_Done", 0, 5, 0, 1), _state.MainDisplayMessage);
        Assert.Equal(anchorsBefore, Anchors());
        Assert.False(AiChartReplyReader.TryRead("""{"anchors":[],"chords":[]}""", AiChartJob.FingeringOnly, out _, out _));
    }

    [Fact]
    public async Task Fingering_only_rejects_a_fingering_that_puts_the_index_off_the_fixed_anchor()
    {
        // Em under the fret-1 anchor, fingered with the index on fret 2: E04.
        var model = Model(AiChartJob.FingeringOnly, (_, reply) =>
        {
            foreach (var c in reply.Chords.Where(c => c.Frets[0] == 0)) c.Fingers = [-1, 1, 2, -1, -1, -1];
        });

        await Run(model, AiChartJob.FingeringOnly);

        Assert.StartsWith(_loc.Format("AiChart_AllRejected", 1, "").TrimEnd(), _state.MainDisplayMessage,
            StringComparison.Ordinal);
        Assert.Contains("E04", _state.MainDisplayMessage, StringComparison.Ordinal);
        Assert.False(_history.CanUndo);
    }

    [Fact]
    public async Task Fingering_only_rejects_a_new_fingering_that_still_contradicts_the_anchor_even_where_the_chart_already_did()
    {
        // The C5 on beat 26 sits under the fret-1 anchor with its index on 3: the chart
        // ALREADY has that E04, so the rule gate alone would not call it introduced. A new
        // fingering that keeps contradicting the fixed anchor is still a rejection.
        var model = Model(AiChartJob.FingeringOnly, (_, reply) =>
            reply.Chords.Single(c => c.Ms == 13000).Fingers = [-1, 1, 3, 3, -1, -1]);

        await Run(model, AiChartJob.FingeringOnly);

        Assert.StartsWith(_loc.Format("AiChart_AllRejected", 1, "").TrimEnd(), _state.MainDisplayMessage,
            StringComparison.Ordinal);
        Assert.Contains("anchor is on fret 1", _state.MainDisplayMessage, StringComparison.Ordinal);
        Assert.False(_history.CanUndo);
    }

    [Fact]
    public async Task Fingering_only_with_nothing_to_finger_says_so()
    {
        _f.Level.Chords.Clear();

        await Run(Model(AiChartJob.FingeringOnly), AiChartJob.FingeringOnly);

        Assert.Equal(_loc["AiChart_NoChordsToFinger"], _state.MainDisplayMessage);
    }

    private sealed class Factory(IToneDesignModel model) : IToneDesignModelFactory
    {
        public IToneDesignModel Create(AiProviderSettings settings) => model;

        public bool Supports(AiProviderKind kind) => kind != AiProviderKind.None;
    }
}
