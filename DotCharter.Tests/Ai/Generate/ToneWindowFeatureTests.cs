using DotCharter.Services.Ai.Generate;
using DotCharter.Services.Ai.Match;
using DotCharter.Tone.Audio.Dsp.Tones;

namespace DotCharter.Tests.Ai.Generate;

/// <summary>
///     The DSP half: named, unit-carrying measurements a prompt can carry and a
///     reader can disagree with. Synthetic signals with known answers, so each case
///     asserts the axis rather than a number nobody derived.
/// </summary>
public sealed class ToneWindowFeatureTests
{
    private const int Rate = 48_000;
    private static readonly ToneWindowFeatureExtractor Extractor = new();

    private static MonoAudio Tone(double hz, double seconds = 4.0, double amplitude = 0.3,
        Func<double, double>? envelope = null)
    {
        var x = new float[(int)(seconds * Rate)];
        for (var i = 0; i < x.Length; i++)
        {
            var t = i / (double)Rate;
            x[i] = (float)(amplitude * (envelope?.Invoke(t) ?? 1.0) * Math.Sin(2 * Math.PI * hz * t));
        }

        return new MonoAudio(x, Rate);
    }

    private static MonoAudio Noise(double seconds = 4.0, int seed = 7)
    {
        var rng = new Random(seed);
        var x = new float[(int)(seconds * Rate)];
        for (var i = 0; i < x.Length; i++) x[i] = (float)(rng.NextDouble() * 0.6 - 0.3);
        return new MonoAudio(x, Rate);
    }

    [Fact]
    public void ABrightSignalHasAHigherCentroidThanADarkOne()
    {
        var dark = Extractor.Extract(Tone(120));
        var bright = Extractor.Extract(Tone(3000));

        Assert.True(bright.CentroidHz > dark.CentroidHz,
            $"3 kHz centroid {bright.CentroidHz:0} Hz vs 120 Hz tone {dark.CentroidHz:0} Hz");
        Assert.InRange(bright.CentroidHz, 2000, 5000);
    }

    [Fact]
    public void TheTiltIsNegativeForADarkSignal_AndHigherForABrightOne()
    {
        var dark = Extractor.Extract(Tone(100));
        var bright = Extractor.Extract(Tone(4000));

        Assert.True(dark.TiltDbPerOctave < bright.TiltDbPerOctave,
            $"dark {dark.TiltDbPerOctave:0.0} dB/oct is not below bright {bright.TiltDbPerOctave:0.0}");
        Assert.True(dark.TiltDbPerOctave < 0, $"a 100 Hz tone tilts {dark.TiltDbPerOctave:0.0} dB/octave");
    }

    [Fact]
    public void NoiseReadsFlatterThanATone() => Assert.True(Extractor.Extract(Noise()).Flatness > Extractor.Extract(Tone(220)).Flatness);

    [Fact]
    public void AHardClippedSignalHasALowerCrestThanACleanOne()
    {
        // The compression/distortion axis in the time domain — measured, not assumed.
        var clean = Tone(220);
        var clipped = new float[clean.Samples.Length];
        for (var i = 0; i < clipped.Length; i++) clipped[i] = Math.Clamp(clean.Samples[i] * 12f, -0.3f, 0.3f);

        var a = Extractor.Extract(clean);
        var b = Extractor.Extract(new MonoAudio(clipped, Rate));

        Assert.True(b.CrestDb < a.CrestDb, $"clipped crest {b.CrestDb:0.0} dB vs clean {a.CrestDb:0.0} dB");
    }

    [Fact]
    public void ADecayingNoteMeasuresItsDecay_AndASustainedOneReportsNone()
    {
        var decaying = Extractor.Extract(Tone(220, 4.0, 0.4, t => Math.Exp(-t * 3.0)));
        var sustained = Extractor.Extract(Tone(220));

        Assert.False(double.IsNaN(decaying.DecaySeconds));
        // −20 dB at e^(−3t) is t = ln(10)/3 ≈ 0.77 s; the envelope is measured in
        // ~21 ms frames, so a tenth of a second either way is the resolution.
        Assert.InRange(decaying.DecaySeconds, 0.6, 0.95);
        Assert.True(double.IsNaN(sustained.DecaySeconds),
            $"a sustained tone reported a decay of {sustained.DecaySeconds:0.00} s");
    }

    [Fact]
    public void ATREMOLOIsDetectedAtItsRate_AndAStaticSignalIsNot()
    {
        // 5 Hz amplitude modulation: the tremolo/chorus axis.
        var tremolo = Extractor.Extract(Tone(300, 6.0, 0.35,
            t => 0.5 + 0.5 * Math.Sin(2 * Math.PI * 5.0 * t)));
        var flat = Extractor.Extract(Tone(300, 6.0));

        Assert.InRange(tremolo.ModulationHz, 4.5, 5.5);
        Assert.True(tremolo.ModulationDepth > 0.2,
            $"a 100 % tremolo measured a depth of {tremolo.ModulationDepth:0.00}");
        Assert.True(flat.ModulationDepth < tremolo.ModulationDepth / 2,
            $"a static tone measured depth {flat.ModulationDepth:0.00}");
    }

    [Fact]
    public void ThePromptLinesNameEveryAxisAndCarryTheirUNITS()
    {
        var text = Extractor.Extract(Tone(400, 5.0, 0.3, t => Math.Exp(-t * 2.0))).ToPromptLines();

        foreach (var needle in new[] { "level:", "crest:", "centroid:", "spectral tilt:", "presence", "decay", "modulation" })
            Assert.Contains(needle, text, StringComparison.Ordinal);
        foreach (var unit in new[] { "dBFS", "dB/octave", "Hz" })
            Assert.Contains(unit, text, StringComparison.Ordinal);
        // A reading, not just a number: the axis has to transfer to a model that has
        // never seen this app's scale.
        Assert.Contains("(", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AWindowTooShortToMeasure_SaysSoRatherThanAnswering()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => Extractor.Extract(new MonoAudio(new float[512], Rate)));
        Assert.Contains("at least", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheMeasurementIsDETERMINISTIC()
    {
        var audio = Tone(330, 5.0, 0.3, t => Math.Exp(-t));
        Assert.Equal(Extractor.Extract(audio), Extractor.Extract(audio));
    }
}

/// <summary>The catalogue-grounded prompt: it must carry the game's own gear and
/// nothing else, and it must not carry a copy of the catalogue.</summary>
public sealed class RsToneGeneratePromptTests
{
    [Fact]
    public void TheGuitarCatalogueBlockNamesRealAmpsAndOffersNoBassGear()
    {
        var block = RsToneGeneratePrompt.CatalogueBlock(bass: false);
        var (amp, _) = GenerateFakes.RealGuitarGear();

        Assert.Contains(amp, block, StringComparison.Ordinal);
        Assert.DoesNotContain("Bass_Amp", block, StringComparison.Ordinal);
        Assert.DoesNotContain("Bass_Cab", block, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBassCatalogueBlockOffersBassAmpsAndCabs()
    {
        var block = RsToneGeneratePrompt.CatalogueBlock(bass: true);

        var bassAmp = RsToneAuthoring.GearOptions(RsGearCatalog.Shared, RsGearType.Amp)
            .First(g => g.Key.StartsWith("Bass_", StringComparison.Ordinal));
        Assert.Contains(bassAmp.Key, block, StringComparison.Ordinal);
        Assert.Contains("Bass_Cab_", block, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryAmpInTheCatalogueIsOffered_WithItsKnobRanges()
    {
        // The whole grounding claim: a model cannot pick gear it was not shown, and
        // a hand-written subset would rot the day the game's data changed.
        var block = RsToneGeneratePrompt.CatalogueBlock(bass: false);
        var amps = RsToneAuthoring.GearOptions(RsGearCatalog.Shared, RsGearType.Amp)
            .Where(g => !g.Key.StartsWith("Bass_", StringComparison.Ordinal))
            .ToList();

        Assert.True(amps.Count > 40, $"only {amps.Count} guitar amps — the catalogue did not load");
        Assert.All(amps, a => Assert.Contains(a.Key, block, StringComparison.Ordinal));

        var knob = amps[0].Knobs![0];
        Assert.Contains(knob.Pot, block, StringComparison.Ordinal);
        Assert.Contains(knob.MaxValue.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
            block, StringComparison.Ordinal);
    }

    [Fact]
    public void CabinetsAreOfferedAsStemsPlusMicPositions_NotAsFourHundredAndFiftyKeys()
    {
        // 450 full keys is twelve kilobytes of prompt describing two choices. The
        // composed key is validated afterwards, so an impossible combination is
        // caught rather than trusted.
        var block = RsToneGeneratePrompt.CatalogueBlock(bass: false);

        Assert.Contains("models:", block, StringComparison.Ordinal);
        Assert.Contains("mic_position:", block, StringComparison.Ordinal);

        var cabs = RsToneAuthoring.GearOptions(RsGearCatalog.Shared, RsGearType.Cabinet)
            .Count(g => !g.Key.StartsWith("Bass_", StringComparison.Ordinal));
        var listed = block.Split('\n').Count(l => l.StartsWith("Cab_", StringComparison.Ordinal));
        Assert.True(listed < cabs / 4, $"{listed} cabinet keys are listed out of {cabs}");
    }

    [Fact]
    public void ThePromptCarriesTheMeasuredFeatures_AndTheChartersContext()
    {
        var features = new ToneWindowFeatureExtractor().Extract(GenerateFakes.Playing());
        var prompt = RsToneGeneratePrompt.Build(new RsToneMapRequest(
            features, "a 2018 Japanese rock trio", "verse", "Verse", ToneStems.Guitar, "Sakasma", "Polkadot Stingray"));

        var user = Assert.Single(prompt.Messages).Text;
        Assert.Contains("spectral tilt", user, StringComparison.Ordinal);
        Assert.Contains("a 2018 Japanese rock trio", user, StringComparison.Ordinal);
        Assert.Contains("Sakasma", user, StringComparison.Ordinal);
        Assert.Contains("verse", user, StringComparison.Ordinal);
        Assert.Equal(RsToneGeneratePrompt.SchemaName, prompt.SchemaName);
    }

    [Fact]
    public void WithNoMeasurement_ThePromptSaysSoRatherThanSendingBlanks()
    {
        var prompt = RsToneGeneratePrompt.Build(new RsToneMapRequest(
            null, "a clean Fender", "verse", "Verse", ToneStems.Guitar, null, null));

        Assert.Contains("No measurements are available", Assert.Single(prompt.Messages).Text,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheBassPromptSaysItIsABassArrangement()
    {
        var prompt = RsToneGeneratePrompt.Build(new RsToneMapRequest(
            null, null, "bass", "Bass", ToneStems.Bass, null, null));

        Assert.Contains("BASS arrangement", prompt.System, StringComparison.Ordinal);
    }
}
