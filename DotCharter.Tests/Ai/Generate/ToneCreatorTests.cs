using DotCharter.Services.Ai.Generate;
using DotCharter.Services.Ai.Match;
using DotCharter.Services.Editing.Tones;
using DotCharter.Tone.Audio.Dsp.Tones;

namespace DotCharter.Tests.Ai.Generate;

/// <summary>
///     The AI tone creator's Services half: measuring the cached stem when asked (and the words alone on a cache miss),
///     the instrument check the catalogue gate does not make, the span at the playhead, the free slot order, and that a
///     first ask's prompt carries no refine section.
/// </summary>
public sealed class ToneCreatorTests
{
    private static ToneCreatorAsk Ask(ToneCreatorWindow? window = null, string description = "tight and scooped") =>
        new(description, ToneStems.Guitar, "ai_tone", "AI tone", "song: Battery", "Battery", "Band", "Lead", window);

    [Fact]
    public async Task A_window_is_measured_from_the_cached_stem_and_a_cache_miss_falls_back_to_the_words()
    {
        var mapper = new GenerateFakes.ReplyMapper(GenerateFakes.ValidReply());
        var isolator = new GenerateFakes.FakeIsolator();
        var creator = new ToneCreator(mapper, isolator, new MeasuredToneEvidenceSource());

        var measured = await creator.ProposeAsync(Ask(new ToneCreatorWindow("song.ogg", 1000, 7000)), CancellationToken.None);
        var words = await creator.ProposeAsync(Ask(), CancellationToken.None);
        var missed = await new ToneCreator(mapper, new MissingStems(), new MeasuredToneEvidenceSource())
            .ProposeAsync(Ask(new ToneCreatorWindow("song.ogg", 1000, 7000)), CancellationToken.None);

        Assert.True(measured.Measured);
        Assert.NotNull(mapper.Requests[0].Features);
        Assert.Equal((ToneStems.Guitar, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(7)), Assert.Single(isolator.Calls));
        Assert.False(words.Measured);
        Assert.Null(mapper.Requests[1].Features);
        Assert.False(missed.Measured);
        Assert.Equal("tight and scooped", mapper.Requests[2].Instruction);
    }

    [Fact]
    public async Task Nothing_to_create_from_is_refused_before_the_model_is_asked()
    {
        var mapper = new GenerateFakes.ReplyMapper(GenerateFakes.ValidReply());
        var creator = new ToneCreator(mapper, new GenerateFakes.FakeIsolator(), new MeasuredToneEvidenceSource());

        await Assert.ThrowsAsync<ArgumentException>(() => creator.ProposeAsync(Ask(description: "  "), CancellationToken.None));
        Assert.Empty(mapper.Requests);
    }

    [Fact]
    public void Gear_of_the_other_instrument_is_named()
    {
        var bassAmp = RsToneAuthoring.GearOptions(RsGearCatalog.Shared, RsGearType.Amp).First(g => g.Key.StartsWith("Bass_", StringComparison.Ordinal)).Key;
        var guitar = RsToneReplyReader.Read(GenerateFakes.ValidReply(), "t", "t", RsGearCatalog.Shared, out _);
        var withBassAmp = guitar with { Amp = guitar.Amp with { GearKey = bassAmp } };

        Assert.Null(ToneCreator.InstrumentFault(guitar, ToneStems.Guitar));
        Assert.Equal(guitar.Amp.GearKey, ToneCreator.InstrumentFault(guitar, ToneStems.Bass)?.GearKey);
        var fault = ToneCreator.InstrumentFault(withBassAmp, ToneStems.Guitar);
        Assert.Equal(ToneCreator.WrongInstrumentCode, fault?.Code);
        Assert.Contains(bassAmp, fault!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_span_at_the_playhead_is_the_placed_changes_window_else_the_next_ten_seconds()
    {
        var beats = GenerateFakes.Beats();
        var project = new ChartProject();
        foreach (var beat in beats) project.Beats.Add(beat);
        var lead = new Arrangement { Name = "Lead" };
        lead.ToneChanges.Add(GenerateFakes.At(4000, beats, "dist"));

        var held = ToneCreatorEvidence.Window("a.ogg", project, lead, 5000, 16000);
        var before = ToneCreatorEvidence.Window("a.ogg", project, lead, 1000, 16000);

        Assert.Equal((2000, 16000), (held.FromMs, held.ToMs));
        Assert.Equal((1000, 11000), (before.FromMs, before.ToMs));
    }

    [Fact]
    public void With_a_tone_the_span_is_where_that_tone_is_placed_wherever_the_playhead_is()
    {
        var beats = GenerateFakes.Beats();
        var project = new ChartProject();
        foreach (var beat in beats) project.Beats.Add(beat);
        var lead = new Arrangement { Name = "Lead" };
        lead.ToneChanges.Add(GenerateFakes.At(4000, beats, "clean"));
        lead.ToneChanges.Add(GenerateFakes.At(9000, beats, "dist"));
        var windows = ToneWindowPlanner.Plan(lead.ToneChanges, project.Beats, 16000);
        var (clean, dist) = (windows.Single(w => w.ToneName == "clean"), windows.Single(w => w.ToneName == "dist"));

        var cleanFromDist = ToneCreatorEvidence.Window("a.ogg", project, lead, dist.FromMs + 100, 16000, "clean");
        var distBeforeAll = ToneCreatorEvidence.Window("a.ogg", project, lead, 500, 16000, "DIST");
        var unplaced = ToneCreatorEvidence.Window("a.ogg", project, lead, 500, 16000, "crunch");

        Assert.Equal((clean.FromMs, clean.ToMs, "clean"), (cleanFromDist.FromMs, cleanFromDist.ToMs, cleanFromDist.ToneName));
        Assert.Equal((dist.FromMs, dist.ToMs, "dist"), (distBeforeAll.FromMs, distBeforeAll.ToMs, distBeforeAll.ToneName));
        Assert.Equal((500, 10500, null), (unplaced.FromMs, unplaced.ToMs, unplaced.ToneName));
    }

    [Fact]
    public void A_free_slot_is_the_first_empty_of_Base_then_A_to_D()
    {
        var arrangement = new Arrangement();
        Assert.Equal("Base", ToneCreatorAccept.FreeSlot(arrangement));
        (arrangement.ToneBase, arrangement.ToneA) = ("x", "x");
        Assert.Equal("B", ToneCreatorAccept.FreeSlot(arrangement));
        (arrangement.ToneB, arrangement.ToneC, arrangement.ToneD) = ("x", "x", "x");
        Assert.Null(ToneCreatorAccept.FreeSlot(arrangement));
    }

    [Fact]
    public void A_first_asks_prompt_has_no_refine_section_and_a_refine_carries_the_previous_json()
    {
        var request = new RsToneMapRequest(null, null, "k", "n", ToneStems.Guitar, null, null, "tight");
        var previous = RsToneReplyReader.Read(GenerateFakes.ValidReply(), "k", "n", RsGearCatalog.Shared, out _);

        var first = RsToneGeneratePrompt.Build(request).Messages[0].Text;
        var refine = RsToneGeneratePrompt.Build(request with { PreviousProposal = RsToneJsonWriter.Write(previous) }).Messages[0].Text;

        Assert.DoesNotContain(RsToneGeneratePrompt.PreviousHeader, first, StringComparison.Ordinal);
        Assert.Contains(RsToneGeneratePrompt.PreviousHeader, refine, StringComparison.Ordinal);
        Assert.Contains(previous.Amp.GearKey, refine, StringComparison.Ordinal);
        Assert.True(refine.IndexOf(RsToneGeneratePrompt.PreviousHeader, StringComparison.Ordinal) <
                    refine.IndexOf(RsToneGeneratePrompt.InstructionHeader, StringComparison.Ordinal));
    }

    private sealed class MissingStems : IStemIsolator
    {
        public Task<MonoAudio> IsolateGuitarAsync(string songAudioPath, TimeSpan from, TimeSpan to, IProgress<string>? status,
            CancellationToken ct) => throw new StemsNotCachedException("no stems");

        public Task<MonoAudio> IsolateAsync(string songAudioPath, string stem, TimeSpan from, TimeSpan to, IProgress<string>? status,
            CancellationToken ct) => throw new StemsNotCachedException("no stems");
    }
}
