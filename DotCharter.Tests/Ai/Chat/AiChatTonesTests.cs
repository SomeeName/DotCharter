using DotCharter.Tone.Ai;
using DotCharter.Services.Ai.Chart;
using DotCharter.Services.Ai.Chat;
using DotCharter.Services.Ai.Generate;
using DotCharter.Services.Ai.Match;
using DotCharter.Tests.Ai.Chart;
using DotCharter.Tests.Ai.Generate;
using DotCharter.Tone.Audio.Dsp.Tones;

namespace DotCharter.Tests.Ai.Chat;

/// <summary>
///     The AI chat's Tones task (#614, and #316's free-text context): the chat message reaches every
///     tone's prompt; the scope picks the right tone changes (selected / at the playhead / all) and one
///     proposal per tone DEFINITION; a reply naming gear the game does not have, or a knob outside its
///     range, is REFUSED, never repaired; the proposals are HELD (the project deep-compares unchanged);
///     Apply writes the accepted tone definitions as ONE undo node, nothing else; undo restores the old
///     definitions exactly. Fake model, fake stems (<see cref="AiChatToneFixture" />): no network.
/// </summary>
public sealed class AiChatTonesTests
{
    private const string Instruction = "the Lead_Dist tone is a high-gain Mesa, darker";

    private readonly AiChatToneFixture _f = new();

    private static string Text(ToneDesignPrompt prompt) => prompt.Messages[0].Text.ReplaceLineEndings("\n");

    private AiChatInstructionRunner Runner(FakeChartModel model, out AiChartPassRun run, IStemIsolator? isolator = null)
    {
        run = _f.Run(model, isolator);
        return new AiChatInstructionRunner(run, _f.Loc);
    }

    private static FakeChartModel Valid() => new((_, _) => GenerateFakes.ValidReply());

    private void AssertProjectUntouched(ChartProject before, string because)
    {
        var diffs = DeepCompare.Diff("project", before, _f.Project, new HashSet<string>());
        Assert.True(diffs.Count == 0, because + ":\n" + string.Join("\n", diffs.Take(20)));
    }

    // ── the instruction (#316's free-text context) ──────────────────────────────────

    [Fact]
    public async Task The_chat_message_and_the_conversation_reach_every_tone_prompt_after_the_context()
    {
        var model = Valid();
        var runner = Runner(model, out _);
        var at = new DateTimeOffset(2026, 10, 10, 9, 0, 0, TimeSpan.Zero);
        var chat = AiChatConversation.Start("chart", "fake-model", at)
            .Append(new AiChatTurn(AiChatRole.User, "The record is Mesa Boogie all the way.", at));

        var reply = await runner.RunInstructionAsync(AiChatToneFixture.Ask(Instruction, _f.All(), chat), CancellationToken.None);

        Assert.Equal(AiChartRunStatus.Held, reply.Status);
        Assert.Equal(2, model.Prompts.Count);
        foreach (var prompt in model.Prompts)
        {
            var text = Text(prompt);
            var context = text.IndexOf("CONTEXT the charter supplied", StringComparison.Ordinal);
            var header = text.IndexOf(RsToneGeneratePrompt.InstructionHeader, StringComparison.Ordinal);
            Assert.True(context >= 0 && header > context, "the instruction section is missing or not after the context:\n" + text);
            Assert.Contains(Instruction, text, StringComparison.Ordinal);
            Assert.Contains("charter: The record is Mesa Boogie all the way.", text, StringComparison.Ordinal);
            Assert.Contains("band: The Band", text, StringComparison.Ordinal);
            // The system prompt is the generator's own, catalogue and all: the chat widens nothing.
            Assert.Equal(RsToneGeneratePrompt.SystemPrompt(bass: false), prompt.System);
            Assert.Equal(RsToneGeneratePrompt.Schema, prompt.JsonSchema);
        }

        // The tokens of every mapping reach the chat's cost line.
        Assert.Equal(new AiChatUsage(200, 100), reply.Usage);
    }

    [Fact]
    public void Without_an_instruction_the_menu_verbs_tone_prompt_has_no_instruction_section()
    {
        var request = new RsToneMapRequest(null, "band: X", "Tone_B", "Tone B", ToneStems.Guitar, "Song", "X");
        var text = Text(RsToneGeneratePrompt.Build(request));

        Assert.DoesNotContain(RsToneGeneratePrompt.InstructionHeader, text, StringComparison.Ordinal);
        Assert.DoesNotContain("The charter named this tone", text, StringComparison.Ordinal);
    }

    // ── the scope ───────────────────────────────────────────────────────────────────

    [Fact]
    public void The_scope_reads_the_selected_tone_changes_the_one_at_the_playhead_or_all_of_them_in_time_order()
    {
        var selected = AiChatScope.Tones(AiChatScopeKind.Selection, _f.Arrangement, [_f.CleanOutro, _f.Lead], 0, _f.Beats)!;
        Assert.Equal([_f.Lead, _f.CleanOutro], selected.ToneChanges!);
        Assert.Equal(AiChatScopeKind.Selection, selected.Kind);

        // The playhead at 7,000 ms is in Lead_Dist (placed at 6,000); at 6,000 exactly too; at 1,999 nothing is in force.
        Assert.Equal([_f.Lead], AiChatScope.Tones(AiChatScopeKind.Section, _f.Arrangement, [], 7000, _f.Beats)!.ToneChanges!);
        Assert.Equal([_f.Lead], AiChatScope.Tones(AiChatScopeKind.Section, _f.Arrangement, [], 6000, _f.Beats)!.ToneChanges!);
        Assert.Null(AiChatScope.Tones(AiChatScopeKind.Section, _f.Arrangement, [], 1999, _f.Beats));

        Assert.Equal([_f.CleanIntro, _f.Lead, _f.CleanOutro, _f.Unnamed], _f.All().ToneChanges!);

        // Nothing selected (or only another arrangement's change) is NOT the whole arrangement.
        Assert.Null(AiChatScope.Tones(AiChatScopeKind.Selection, _f.Arrangement, [], 0, _f.Beats));
        Assert.Null(AiChatScope.Tones(AiChatScopeKind.Selection, _f.Arrangement, [new ToneChange { ToneName = "Clean" }], 0, _f.Beats));
    }

    [Fact]
    public void One_target_per_tone_definition_from_its_longest_window_and_an_unnamed_change_is_refused()
    {
        var plan = AiTonePass.Plan(_f.Project, _f.Arrangement, _f.All().ToneChanges!, AiChatToneFixture.SongMs, _f.Loc);

        Assert.Equal(["Lead_Dist", "Clean"], plan.Targets.Select(t => t.Key));
        Assert.Equal(["Lead Dist", "Clean"], plan.Targets.Select(t => t.Name));
        var clean = plan.Targets[1];
        Assert.Equal((10000, 8000, 13000), (clean.Window.PlacedMs, clean.Window.FromMs, clean.Window.ToMs));
        Assert.Equal(_f.Project.ToneDefinitions[0].Json, clean.CurrentJson);
        var refused = Assert.Single(plan.Refused);
        Assert.Equal(AiTonePass.UnnamedCode, refused.Code);
        Assert.Contains("0:15", refused.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_generator_measures_exactly_the_scoped_windows_on_the_arrangements_stem()
    {
        var runner = Runner(Valid(), out var run);
        var playhead = AiChatScope.Tones(AiChatScopeKind.Section, _f.Arrangement, [], 11000, _f.Beats)!;

        await runner.RunInstructionAsync(AiChatToneFixture.Ask(Instruction, playhead), CancellationToken.None);

        var call = Assert.Single(_f.Isolator.Calls);
        Assert.Equal((ToneStems.Guitar, TimeSpan.FromMilliseconds(8000), TimeSpan.FromMilliseconds(13000)), call);
        Assert.Equal("Clean", Assert.Single(run.Review!.Tones).Key);
    }

    // ── the gate: rejected, never repaired ──────────────────────────────────────────

    [Fact]
    public async Task A_reply_naming_gear_the_game_does_not_have_is_refused_not_repaired()
    {
        var model = new FakeChartModel((prompt, _) => Text(prompt).Contains("Tone key to use: Clean", StringComparison.Ordinal)
            ? GenerateFakes.ValidReply(ampKey: "Amp_MesaBoogieMarkV")
            : GenerateFakes.ValidReply());
        var runner = Runner(model, out var run);

        var reply = await runner.RunInstructionAsync(AiChatToneFixture.Ask(Instruction, _f.All()), CancellationToken.None);

        Assert.Equal(AiChartRunStatus.Held, reply.Status);
        var held = Assert.Single(run.Review!.Tones);
        Assert.Equal("Lead_Dist", held.Key);
        Assert.DoesNotContain(run.Review.Tones, t => t.Proposed.Amp.GearKey == "Amp_MesaBoogieMarkV");
        var refused = run.Review.Result.Rejected.Single(r => r.Code == AiTonePass.NotGeneratedCode);
        Assert.Contains("Amp_MesaBoogieMarkV", refused.Reason, StringComparison.Ordinal);
        Assert.Contains("Amp_MesaBoogieMarkV", reply.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_reply_with_a_knob_outside_its_range_is_refused_not_clamped()
    {
        var (amp, _) = GenerateFakes.RealGuitarGear();
        var knob = GenerateFakes.FirstKnob(amp);
        var model = new FakeChartModel((_, _) => GenerateFakes.ValidReply(knobs: GenerateFakes.KnobJson(knob.Pot, knob.MaxValue + 150f)));
        var runner = Runner(model, out var run);
        var before = ChartProjectCloner.Clone(_f.Project);

        var reply = await runner.RunInstructionAsync(AiChatToneFixture.Ask(Instruction, _f.All()), CancellationToken.None);

        Assert.Equal(AiChartRunStatus.AllRejected, reply.Status);
        Assert.Null(run.Review);
        Assert.Null(reply.Review);
        Assert.Contains(knob.Key, reply.Text, StringComparison.Ordinal);
        AssertProjectUntouched(before, "a refused reply changed the project");
        Assert.False(_f.History.CanUndo);
    }

    // ── held, then applied as one undo node ─────────────────────────────────────────

    [Fact]
    public async Task The_proposals_are_held_and_the_project_is_untouched_until_apply()
    {
        var runner = Runner(Valid(), out var run);
        var before = ChartProjectCloner.Clone(_f.Project);

        var reply = await runner.RunInstructionAsync(AiChatToneFixture.Ask(Instruction, _f.All()), CancellationToken.None);

        var review = Assert.IsType<AiChartReview>(run.Review);
        Assert.Same(review, reply.Review);
        Assert.Equal(AiChartJob.Tones, review.Job);
        Assert.Equal(["Lead_Dist", "Clean"], review.Tones.Select(t => t.Key));
        Assert.All(review.Tones, t => Assert.Equal(AiChartDecision.Pending, t.Decision));
        Assert.All(review.Tones, t => Assert.True(RsToneCatalogValidation.IsValid(t.Proposed, RsGearCatalog.Shared)));
        Assert.All(review.Tones, t => Assert.NotNull(t.Current));
        Assert.Empty(review.Chunks);
        Assert.Empty(review.Items);
        AssertProjectUntouched(before, "holding the proposals changed the project");
        Assert.False(_f.History.CanUndo);

        // The reply lists each tone and its chain, the refused unnamed change, and where to accept them.
        Assert.Contains(_f.Loc.Format("AiChat_TonesProposed_Many", 2), reply.Text, StringComparison.Ordinal);
        Assert.Contains(AiToneText.Chain(review.Tones[0].Proposed), reply.Text, StringComparison.Ordinal);
        Assert.Contains(_f.Loc.Format("AiTones_Unnamed", "0:15"), reply.Text, StringComparison.Ordinal);
        Assert.EndsWith(_f.Loc["AiChat_TonesReviewHint"], reply.Text, StringComparison.Ordinal);
        Assert.Equal(review.Id, reply.ToTurn(DateTimeOffset.UnixEpoch).ReviewId);
    }

    [Fact]
    public async Task Apply_writes_the_accepted_tone_definitions_as_one_undo_node_and_undo_restores_them_exactly()
    {
        var runner = Runner(Valid(), out var run);
        var before = ChartProjectCloner.Clone(_f.Project);
        var cleanBefore = _f.Project.ToneDefinitions.Single(d => d.Key == "Clean");
        var leadBefore = _f.Project.ToneDefinitions.Single(d => d.Key == "Lead_Dist");
        await runner.RunInstructionAsync(AiChatToneFixture.Ask(Instruction, _f.All()), CancellationToken.None);
        var review = run.Review!;
        review.Decide(review.Tones.Single(t => t.Key == "Lead_Dist"), AiChartDecision.Accepted);
        review.Decide(review.Tones.Single(t => t.Key == "Clean"), AiChartDecision.Rejected);

        Assert.True(run.ApplyReview());

        Assert.Null(run.Review);
        Assert.Single(_f.History.GetUndoSteps());
        var lead = _f.Project.ToneDefinitions.Single(d => d.Key == "Lead_Dist");
        Assert.Equal(review.Tones.Single(t => t.Key == "Lead_Dist").ProposedJson, lead.Json);
        Assert.Equal("Lead Dist", lead.Name);
        Assert.Same(cleanBefore, _f.Project.ToneDefinitions.Single(d => d.Key == "Clean"));
        Assert.Equal(1, _f.Project.ToneDefinitions.IndexOf(lead));
        Assert.True(RsToneJsonParser.TryParse(lead.Json, out var written));
        Assert.Equal("Lead_Dist", written!.Key);

        // Nothing but the tone definitions changed: not a tone change, not a slot, not a note.
        var diffs = DeepCompare.Diff("project", before, _f.Project, new HashSet<string>());
        Assert.NotEmpty(diffs);
        Assert.All(diffs, d => Assert.StartsWith("project.ToneDefinitions", d, StringComparison.Ordinal));

        _f.History.Undo();
        AssertProjectUntouched(before, "undo did not restore the old tone definitions");
        Assert.Same(leadBefore, _f.Project.ToneDefinitions[1]);
    }

    [Fact]
    public async Task Accepting_every_tone_writes_them_all_in_one_undo_step()
    {
        var runner = Runner(Valid(), out var run);
        var before = ChartProjectCloner.Clone(_f.Project);
        await runner.RunInstructionAsync(AiChatToneFixture.Ask(Instruction, _f.All()), CancellationToken.None);
        var review = run.Review!;
        review.AcceptAll();

        Assert.True(run.ApplyReview());

        Assert.Single(_f.History.GetUndoSteps());
        Assert.All(review.Tones, t => Assert.Equal(t.ProposedJson, _f.Project.ToneDefinitions.Single(d => d.Key == t.Key).Json));
        _f.History.Undo();
        AssertProjectUntouched(before, "one undo did not take every tone back");
    }

    [Fact]
    public async Task A_definition_edited_while_the_card_waits_is_stale_and_left_alone()
    {
        var runner = Runner(Valid(), out var run);
        await runner.RunInstructionAsync(AiChatToneFixture.Ask(Instruction, _f.All()), CancellationToken.None);
        run.Review!.AcceptAll();
        var edited = RsToneJsonWriter.Write(
            RsToneAuthoring.CreateDefaultTone("Clean", "Clean", RsGearCatalog.Shared) with { VolumeDb = -20f });
        Assert.NotEqual(AiChatToneFixture.Body("Clean"), edited);
        _f.Project.ToneDefinitions.Single(d => d.Key == "Clean").Json = edited;

        Assert.True(run.ApplyReview());

        Assert.Equal(edited, _f.Project.ToneDefinitions.Single(d => d.Key == "Clean").Json);
        Assert.NotEqual(AiChatToneFixture.Body("Lead_Dist"), _f.Project.ToneDefinitions.Single(d => d.Key == "Lead_Dist").Json);
        Assert.Equal(_f.Loc.Format("AiTones_Done", 1, 2), _f.State.MainDisplayMessage);
    }

    /// <summary>The chain the card and the reply print: every gear item in signal order by the game's own name,
    /// a cabinet with its mic rig, every knob by name with the value the model gave (not clamped, not rounded
    /// into another value).</summary>
    [Fact]
    public void A_chain_names_every_gear_item_in_signal_order_with_its_knobs()
    {
        var (amp, cab) = GenerateFakes.RealGuitarGear();
        var knob = GenerateFakes.FirstKnob(amp);
        var tone = RsToneReplyReader.Read(GenerateFakes.ValidReply(knobs: GenerateFakes.KnobJson(knob.Pot, knob.MinValue + 0.25)),
            "Clean", "Clean", RsGearCatalog.Shared, out _);
        Assert.True(RsGearCatalog.Shared.TryGetGear(amp, out var ampData));
        Assert.True(RsGearCatalog.Shared.TryGetGear(cab, out var cabData));

        var chain = AiToneText.Chain(tone);

        var value = (knob.MinValue + 0.25).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        Assert.StartsWith($"{ampData.Name} ({knob.Name} {value}) → {cabData.Name} · ", chain, StringComparison.Ordinal);
        var parts = cab.Split('_');
        Assert.EndsWith(parts[^2] + " " + parts[^1], chain, StringComparison.Ordinal);
    }

    // ── no cached stems: never a separation ─────────────────────────────────────────

    [Fact]
    public async Task With_no_cached_stems_a_tone_is_mapped_from_the_words_alone_and_says_so()
    {
        var model = Valid();
        var cacheMiss = new CacheMissIsolator(_f.Loc["Stems_NotCachedNoSeparation"]);
        var runner = Runner(model, out var run, cacheMiss);
        var playhead = AiChatScope.Tones(AiChatScopeKind.Section, _f.Arrangement, [], 7000, _f.Beats)!;

        var reply = await runner.RunInstructionAsync(AiChatToneFixture.Ask(Instruction, playhead), CancellationToken.None);

        Assert.Equal(AiChartRunStatus.Held, reply.Status);
        var tone = Assert.Single(run.Review!.Tones);
        Assert.Equal(RsToneGenerator.NoStem, tone.Provenance!.Stem);
        Assert.Equal(RsToneGenerator.UnratedNoStem, tone.Provenance.UnratedReason);
        Assert.Null(tone.Provenance.Features);
        Assert.Equal(_f.Loc["AiTones_FromWordsOnly"], AiToneText.Evidence(tone, _f.Loc));
        var text = Text(Assert.Single(model.Prompts));
        Assert.Contains("No measurements are available", text, StringComparison.Ordinal);
        Assert.Contains(Instruction, text, StringComparison.Ordinal);
    }

    /// <summary>The cache-only isolator's shape on a miss, without a cache on disk.</summary>
    private sealed class CacheMissIsolator(string message) : IStemIsolator
    {
        public Task<MonoAudio> IsolateGuitarAsync(string songAudioPath, TimeSpan from, TimeSpan to, IProgress<string>? status,
            CancellationToken ct) => throw new StemsNotCachedException(message);

        public Task<MonoAudio> IsolateAsync(string songAudioPath, string stem, TimeSpan from, TimeSpan to,
            IProgress<string>? status, CancellationToken ct) => throw new StemsNotCachedException(message);
    }

    // ── the other three tasks are untouched ─────────────────────────────────────────

    /// <summary>
    ///     The menu verb's tone prompt (Generate tones with AI…) for a generic name and no instruction, byte
    ///     for byte as the builder wrote it before #614: the Tones task added an optional instruction section
    ///     and a soft name hint, and neither may appear here. The expected text is the old builder spelled
    ///     out, line by line. (The FHP / fingering, sections and explain prompts are pinned by hash in
    ///     <c>AiStructureTests.The_fhp_and_fingering_prompts_are_unchanged</c> and the explain tests.)
    /// </summary>
    [Fact]
    public void The_menu_verbs_tone_prompt_for_a_generic_name_is_unchanged()
    {
        var features = new ToneWindowFeatureExtractor().Extract(GenerateFakes.Playing());
        var prompt = RsToneGeneratePrompt.Build(
            new RsToneMapRequest(features, "band: X, song: Y", "Tone_A", "Tone A", ToneStems.Guitar, "Y", "X"));

        var expected = "Instrument: guitar\nTone key to use: Tone_A\nTone name to use: Tone A\nSong: Y — X\n\n" +
                       "MEASURED from the isolated stem for this exact stretch of the song:\n" +
                       features.ToPromptLines().ReplaceLineEndings("\n") +
                       "\nCONTEXT the charter supplied (band, era, known rig, intent):\nband: X, song: Y\n\n" +
                       "Answer with the JSON object only.\n";
        Assert.Equal(expected, prompt.Messages[0].Text.ReplaceLineEndings("\n"));
        Assert.Equal(RsToneGeneratePrompt.SystemPrompt(bass: false), prompt.System);
    }
}
