using DotCharter.Services.Ai.Generate;
using DotCharter.Services.Ai.Match;
using DotCharter.Tone.Ai;
using DotCharter.Tone.Audio.Dsp.Tones;
using DotCharter.Tone.Audio.Tonelab;

namespace DotCharter.Tests.Ai.Generate;

/// <summary>
///     Fakes for the AI tone check (<see cref="RsToneFeedbackLoop" />) whose numbers can be worked out by hand. A "render"
///     is a constant signal: the clone plays a tone as the unit position of its FIRST knob axis
///     (<see cref="RsToneKnobSpace" />, keys in ordinal order, so the amp's Bass), the game the same value minus a
///     per-amp offset (the game hears that amp darker), the reference rig as 0. The scorer's distance is the gap between
///     the means, its percentage the W5b formula over the reference's gap.
/// </summary>
internal static class RsToneLoopFakes
{
    internal const string AmpA = "Amp_AT120";
    internal const string AmpB = "Amp_BT15";
    internal const int Rate = 48_000;
    internal const int Frames = 480;

    /// <summary>A catalogue-valid guitar tone on <paramref name="amp" />, every knob at its default except Bass.</summary>
    internal static RsTone MakeTone(string amp, float bass)
    {
        var catalog = RsGearCatalog.Shared;
        Assert.True(catalog.TryGetGear(amp, out var gear));
        var knobs = gear.Knobs!.ToDictionary(k => k.Key, k => k.Key.EndsWith("_Bass", StringComparison.Ordinal) ? bass : k.DefaultValue,
            StringComparer.Ordinal);
        var basic = RsToneAuthoring.CreateDefaultTone("ai_tone", "Test", catalog);
        return RsToneCatalogValidation.Require(basic with { Amp = new RsAmp(amp, gear.Category, knobs) }, catalog, "a test tone");
    }

    /// <summary>The value the clone renders <paramref name="tone" /> as.</summary>
    internal static double Unit(RsTone tone) => RsToneKnobSpace.Of(tone, RsGearCatalog.Shared).Encode(tone)[0];

    internal static MonoAudio Constant(double value) => new(Enumerable.Repeat((float)value, Frames).ToArray(), Rate);

    internal static double Mean(MonoAudio audio) => audio.Samples.Average(s => (double)s);

    internal static RsToneLoopInput Input(double target, RsToneLoopOptions? options = null, string? instruction = "tight rhythm") =>
        new(new RsToneMapRequest(null, null, "ai_tone", "Test", ToneStems.Guitar, "Song", "Band", instruction),
            Constant(target), Constant(0), options ?? new RsToneLoopOptions(Proposals: 2, RevisionRounds: 2, Finalists: 2, KnobEvaluations: 24));

    /// <summary>The model: answers the tones in order (the last one again once they run out), or throws what it is told to.</summary>
    internal sealed class Mapper(params Func<RsToneMapRequest, RsTone>[] answers) : IRsToneMapper
    {
        public List<RsToneMapRequest> Requests { get; } = [];

        public string Describe() => "fake-loop-model";

        public Task<RsToneMapping> MapAsync(RsToneMapRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            var tone = answers[Math.Min(Requests.Count - 1, answers.Length - 1)](request);
            return Task.FromResult(new RsToneMapping(tone, "fake-loop-model", new AiUsage("fake-loop-model", 0, 0), [], $"answer {Requests.Count}"));
        }
    }

    internal sealed class Clone : IRsToneCloneRenderer
    {
        public int Renders { get; private set; }

        public Task<MonoAudio> RenderAsync(MonoAudio di, RsTone tone, CancellationToken ct)
        {
            Renders++;
            return Task.FromResult(Constant(Unit(tone)));
        }

        public Task<MonoAudio> RenderReferenceAsync(MonoAudio di, CancellationToken ct) => Task.FromResult(Constant(0));
    }

    internal sealed class Scorer : IToneLoopScorer
    {
        public ToneLoopScore Score(MonoAudio target, MonoAudio candidate, MonoAudio reference)
        {
            var gap = Mean(candidate) - Mean(target);
            var anchor = Math.Abs(Mean(reference) - Mean(target));
            var percent = (int)Math.Round(Math.Clamp(1 - (Math.Abs(gap) / anchor), 0, 1) * 100);
            IReadOnlyList<string> differences = Math.Abs(gap) < 0.02 ? [] : [gap > 0 ? "too bright: fake" : "too dark: fake"];
            return new ToneLoopScore(percent, null, Math.Abs(gap), differences);
        }
    }

    /// <summary>The game: renders each tone as its clone value minus the amp's offset, or refuses with <see cref="Refusal" />.</summary>
    internal sealed class Game : IGameToneRenderer
    {
        public Dictionary<string, double> Offsets { get; } = new(StringComparer.Ordinal);

        public string? Refusal { get; set; }

        public List<IReadOnlyList<RsTone>> Batches { get; } = [];

        public int SampleRate => Rate;

        public GameRenderAvailability Availability() => new(Refusal);

        public Task<IReadOnlyList<GameRenderResult>> RenderAsync(float[] di, IReadOnlyList<RsTone> tones, CancellationToken ct)
        {
            Batches.Add(tones);
            IReadOnlyList<GameRenderResult> results = tones
                .Select(t => new GameRenderResult(Constant(Unit(t) - Offsets.GetValueOrDefault(t.Amp.GearKey)).Samples, null))
                .ToList();
            return Task.FromResult(results);
        }
    }
}
