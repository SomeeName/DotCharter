using DotCharter.Services.Ai.Generate;
using DotCharter.Services.Ai.Match;
using DotCharter.Tone.Audio.Dsp.Tones;

namespace DotCharter.Tests.Ai.Generate;

/// <summary>
///     The generation run: the right stem for the right window, a missing stem said
///     out loud, and an out-of-catalogue reply rejected by name.
/// </summary>
public sealed class RsToneGeneratorTests
{
    private static ToneGenerateRequest Request(
        IReadOnlyList<ToneWindow> windows, string stem = ToneStems.Guitar, string? context = null) =>
        new("song.wav", "Lead", stem, windows, context, "Sakasma", "Polkadot Stingray", []);

    private static IReadOnlyList<ToneWindow> OneWindow()
    {
        var beats = GenerateFakes.Beats();
        return ToneWindowPlanner.Plan([GenerateFakes.At(4000, beats, "Verse")], beats, 10_000);
    }

    [Fact]
    public async Task ItIsolatesTheWINDOW_FromTheArrangementsOwnStem()
    {
        var isolator = new GenerateFakes.FakeIsolator();
        var generator = new RsToneGenerator(isolator, new GenerateFakes.ReplyMapper(GenerateFakes.ValidReply()), FakeLocalization.English());

        var result = await generator.GenerateAsync(
            Request(OneWindow(), ToneStems.Bass), null, TestContext.Current.CancellationToken);

        var call = Assert.Single(isolator.Calls);
        Assert.Equal(ToneStems.Bass, call.Stem);
        // One measure of lead: the change is at 4,000 ms in 2,000 ms bars.
        Assert.Equal(2000, call.From.TotalMilliseconds, 1);
        Assert.Equal(10_000, call.To.TotalMilliseconds, 1);
        Assert.Equal(1, result.Generated);
    }

    [Fact]
    public async Task AMissingStem_FailsThatChangeByNAME_AndNeverFallsBackToTheFullMix()
    {
        // A bass tone measured off the whole record is a tone built from the drums,
        // and it would look exactly as confident as a real one.
        var isolator = new GenerateFakes.FakeIsolator();
        isolator.Missing[ToneStems.Bass] = "The stems for song.wav contain no source acceptable for 'bass'.";
        var mapper = new GenerateFakes.ReplyMapper(GenerateFakes.ValidReply());
        var generator = new RsToneGenerator(isolator, mapper, FakeLocalization.English());

        var result = await generator.GenerateAsync(
            Request(OneWindow(), ToneStems.Bass), null, TestContext.Current.CancellationToken);

        var entry = Assert.Single(result.Tones);
        Assert.Null(entry.Tone);
        Assert.NotNull(entry.Error);
        Assert.Contains("bass", entry.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, result.Generated);
        // …and nothing was asked of the model, so no tone was invented for it.
        Assert.Empty(mapper.Requests);
        Assert.Contains(result.Notes, n => n.Contains("failed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ItReportsTheShareOfToneChangesStarted_ThenOne()
    {
        // The rail's progress bar: a run over two changes reads 0, ½, then done.
        var beats = GenerateFakes.Beats();
        var windows = ToneWindowPlanner.Plan(
            [GenerateFakes.At(4000, beats, "A"), GenerateFakes.At(8000, beats, "B")], beats, 12_000);
        var fractions = new Recorder();

        await new RsToneGenerator(new GenerateFakes.FakeIsolator(),
                new GenerateFakes.ReplyMapper(GenerateFakes.ValidReply()), FakeLocalization.English())
            .GenerateAsync(Request(windows), null, TestContext.Current.CancellationToken, fractions);

        Assert.Equal([0.0, 0.5, 1.0], fractions.Values);
    }

    /// <summary>Records on the reporting thread, in order — <see cref="Progress{T}" />
    /// would post each value and lose the order.</summary>
    private sealed class Recorder : IProgress<double>
    {
        public List<double> Values { get; } = [];

        public void Report(double value) => Values.Add(value);
    }

    [Fact]
    public async Task OneFailedWindow_DoesNotLoseTheOthers()
    {
        var beats = GenerateFakes.Beats();
        var windows = ToneWindowPlanner.Plan(
            [GenerateFakes.At(4000, beats, "A"), GenerateFakes.At(8000, beats, "B")], beats, 12_000);

        var calls = 0;
        var isolator = new GenerateFakes.FakeIsolator();
        var mapper = new GenerateFakes.ReplyMapper(_ =>
            ++calls == 1 ? "not json at all" : GenerateFakes.ValidReply());
        var result = await new RsToneGenerator(isolator, mapper, FakeLocalization.English())
            .GenerateAsync(Request(windows), null, TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Tones.Count);
        Assert.Null(result.Tones[0].Tone);
        Assert.NotNull(result.Tones[1].Tone);
        Assert.Equal(1, result.Generated);
    }

    [Fact]
    public async Task AReplyNamingGearTheGameDoesNotHave_IsRejected_AndTheErrorNamesIt()
    {
        var mapper = new GenerateFakes.ReplyMapper(GenerateFakes.ValidReply(ampKey: "Amp_Marshall_JCM800"));
        var result = await new RsToneGenerator(new GenerateFakes.FakeIsolator(), mapper, FakeLocalization.English())
            .GenerateAsync(Request(OneWindow()), null, TestContext.Current.CancellationToken);

        var entry = Assert.Single(result.Tones);
        Assert.Null(entry.Tone);
        Assert.Contains("Amp_Marshall_JCM800", entry.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AKnobOutsideItsRange_IsRejected_AndTheErrorNamesTheValueAndTheRange()
    {
        var (amp, _) = GenerateFakes.RealGuitarGear();
        var knob = GenerateFakes.FirstKnob(amp);
        var over = knob.MaxValue + 150f;

        var mapper = new GenerateFakes.ReplyMapper(
            GenerateFakes.ValidReply(knobs: GenerateFakes.KnobJson(knob.Pot, over)));
        var result = await new RsToneGenerator(new GenerateFakes.FakeIsolator(), mapper, FakeLocalization.English())
            .GenerateAsync(Request(OneWindow()), null, TestContext.Current.CancellationToken);

        var entry = Assert.Single(result.Tones);
        Assert.Null(entry.Tone);
        Assert.Contains(knob.Key, entry.Error!, StringComparison.Ordinal);
        Assert.Contains(over.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
            entry.Error!, StringComparison.Ordinal);
        Assert.Contains("outside Rocksmith's range", entry.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheProvenanceCarriesTheWindow_TheStem_TheSourcesAndTheModel()
    {
        var mapper = new GenerateFakes.ReplyMapper(GenerateFakes.ValidReply());
        var result = await new RsToneGenerator(new GenerateFakes.FakeIsolator(), mapper, FakeLocalization.English())
            .GenerateAsync(Request(OneWindow(), context: "a 2018 Japanese rock trio, bright single-coil"),
                null, TestContext.Current.CancellationToken);

        var provenance = Assert.Single(result.Tones).Provenance;
        Assert.NotNull(provenance);
        Assert.Equal(ToneStems.Guitar, provenance!.Stem);
        Assert.Equal("fake-model", provenance.ModelId);
        Assert.Equal([ToneGenerationSources.Audio, ToneGenerationSources.Context], provenance.Sources);
        Assert.NotNull(provenance.Features);
        Assert.Contains("one measure", provenance.Window.Describe(), StringComparison.Ordinal);
        // No renderer was supplied, so the confidence is UNRATED with its reason —
        // never a 0 that reads as a measurement.
        Assert.Null(provenance.ConfidencePercent);
        Assert.Equal(RsToneGenerator.UnratedNoRenderer, provenance.UnratedReason);
    }

    [Fact]
    public async Task BothSourcesReachTheMapper_AndEitherAloneIsEnough()
    {
        var mapper = new GenerateFakes.ReplyMapper(GenerateFakes.ValidReply());
        await new RsToneGenerator(new GenerateFakes.FakeIsolator(), mapper, FakeLocalization.English())
            .GenerateAsync(Request(OneWindow(), context: "context text"), null,
                TestContext.Current.CancellationToken);

        var request = Assert.Single(mapper.Requests);
        Assert.NotNull(request.Features);
        Assert.Equal("context text", request.Context);
        Assert.Equal("Sakasma", request.SongTitle);
    }

    [Fact]
    public async Task WithNeitherMeasurementNorContext_ItRefusesRatherThanInventing()
    {
        // A window too short to measure and no context is nothing to generate FROM.
        // The alternative is a tone built out of a tone key.
        var isolator = new GenerateFakes.FakeIsolator(GenerateFakes.Playing(0.02));
        var mapper = new GenerateFakes.ReplyMapper(GenerateFakes.ValidReply());

        var result = await new RsToneGenerator(isolator, mapper, FakeLocalization.English())
            .GenerateAsync(Request(OneWindow()), null, TestContext.Current.CancellationToken);

        Assert.Null(Assert.Single(result.Tones).Tone);
        Assert.Contains("nothing to generate from", result.Tones[0].Error!, StringComparison.Ordinal);
        Assert.Empty(mapper.Requests);
    }

    [Fact]
    public async Task AWindowTooShortToMeasure_StillGeneratesFromContextAlone()
    {
        // The guard against the guard above: context alone IS enough, and the
        // provenance then says only `context` contributed.
        var isolator = new GenerateFakes.FakeIsolator(GenerateFakes.Playing(0.02));
        var mapper = new GenerateFakes.ReplyMapper(GenerateFakes.ValidReply());

        var result = await new RsToneGenerator(isolator, mapper, FakeLocalization.English())
            .GenerateAsync(Request(OneWindow(), context: "a clean Fender amp"), null,
                TestContext.Current.CancellationToken);

        var entry = Assert.Single(result.Tones);
        Assert.NotNull(entry.Tone);
        Assert.Equal([ToneGenerationSources.Context], entry.Provenance!.Sources);
        Assert.Null(entry.Provenance.Features);
    }

    [Fact]
    public async Task EveryToneItReturns_PassesTheCatalogGate()
    {
        var result = await new RsToneGenerator(
                new GenerateFakes.FakeIsolator(), new GenerateFakes.ReplyMapper(GenerateFakes.ValidReply()),
                FakeLocalization.English())
            .GenerateAsync(Request(OneWindow()), null, TestContext.Current.CancellationToken);

        Assert.All(result.Tones.Where(t => t.Tone is not null), t =>
        {
            var fault = RsToneCatalogValidation.FirstFault(t.Tone, RsGearCatalog.Shared);
            Assert.True(fault is null, fault?.Message);
        });
    }
}
