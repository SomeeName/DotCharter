using System.Text.Json;
using DotCharter.Tone.Ai;
using DotCharter.Services.Ai.Generate;
using DotCharter.Services.Ai.Match;
using DotCharter.Tone.Audio.Dsp.Tones;

namespace DotCharter.Tests.Ai.Generate;

/// <summary>
///     Fakes for the tone-generation lane. Everything above
///     <see cref="IRsToneMapper" /> is deterministic, so a whole run is testable in
///     the default gate with no provider, no engine and no separation.
/// </summary>
internal static class GenerateFakes
{
    internal const int Rate = 48_000;

    /// <summary>A deterministic "played" window — a decaying 110 Hz tone, long
    /// enough for <see cref="ToneWindowFeatureExtractor" /> to measure.</summary>
    internal static MonoAudio Playing(double seconds = 6.0)
    {
        var x = new float[(int)(seconds * Rate)];
        for (var i = 0; i < x.Length; i++)
        {
            var t = i / (double)Rate;
            var env = 0.5 + 0.5 * Math.Cos(2 * Math.PI * 2.0 * t);
            x[i] = (float)(0.4 * env * Math.Sin(2 * Math.PI * 110.0 * t));
        }

        return new MonoAudio(x, Rate);
    }

    internal static List<BeatMarker> Beats(int bars = 8, double barMs = 2000)
    {
        var beats = new List<BeatMarker>();
        for (var bar = 0; bar <= bars; bar++)
        {
            for (var b = 0; b < (bar == bars ? 1 : 4); b++)
            {
                beats.Add(new BeatMarker
                {
                    Time = bar * barMs + b * barMs / 4.0,
                    IsMeasure = b == 0,
                    BeatsInMeasure = 4
                });
            }
        }

        return beats;
    }

    internal static ToneChange At(double ms, List<BeatMarker> beats, string name = "") =>
        new() { Position = TimelineMath.PositionFromMs(ms, beats), ToneName = name };

    /// <summary>An isolator that answers with prepared audio per stem and can be told
    /// to REFUSE one — the missing-stem case.</summary>
    internal sealed class FakeIsolator : IStemIsolator
    {
        private readonly MonoAudio _audio;

        public FakeIsolator(MonoAudio? audio = null) => _audio = audio ?? Playing();

        public List<(string Stem, TimeSpan From, TimeSpan To)> Calls { get; } = [];

        /// <summary>Stems this isolator refuses, with the message it refuses them
        /// with — the shape a real missing stem takes.</summary>
        public Dictionary<string, string> Missing { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<MonoAudio> IsolateGuitarAsync(
            string songAudioPath, TimeSpan from, TimeSpan to, IProgress<string>? status, CancellationToken ct) =>
            IsolateAsync(songAudioPath, ToneStems.Guitar, from, to, status, ct);

        public Task<MonoAudio> IsolateAsync(
            string songAudioPath, string stem, TimeSpan from, TimeSpan to, IProgress<string>? status,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add((stem, from, to));
            if (Missing.TryGetValue(stem, out var why)) throw new InvalidOperationException(why);
            return Task.FromResult(_audio);
        }
    }

    /// <summary>A mapper that answers with whatever JSON it is given, through the
    /// REAL <see cref="RsToneReplyReader" /> — so a test of "an out-of-catalogue
    /// reply is rejected" exercises the real validator rather than a fake's
    /// opinion.</summary>
    internal sealed class ReplyMapper : IRsToneMapper
    {
        private readonly Func<RsToneMapRequest, string> _reply;

        public ReplyMapper(Func<RsToneMapRequest, string> reply) => _reply = reply;

        public ReplyMapper(string reply) : this(_ => reply) { }

        public List<RsToneMapRequest> Requests { get; } = [];

        public string Describe() => "fake-mapper";

        public Task<RsToneMapping> MapAsync(RsToneMapRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            var tone = RsToneReplyReader.Read(
                _reply(request), request.ToneKey, request.ToneName, RsGearCatalog.Shared, out var rationale);

            var sources = new List<string>(2);
            if (request.Features is not null) sources.Add(ToneGenerationSources.Audio);
            if (!string.IsNullOrWhiteSpace(request.Context)) sources.Add(ToneGenerationSources.Context);
            return Task.FromResult(new RsToneMapping(
                tone, "fake-model", new AiUsage("fake-model", 0, 0), sources, rationale));
        }
    }

    /// <summary>A real amp + cabinet key from the shipped catalogue, so a valid reply
    /// in a test is valid for the same reason the product's is.</summary>
    internal static (string Amp, string Cab) RealGuitarGear()
    {
        var catalog = RsGearCatalog.Shared;
        var amp = RsToneAuthoring.GearOptions(catalog, RsGearType.Amp)
            .First(g => !g.Key.StartsWith("Bass_", StringComparison.Ordinal));
        var cab = RsToneAuthoring.GearOptions(catalog, RsGearType.Cabinet)
            .First(g => !g.Key.StartsWith("Bass_", StringComparison.Ordinal));
        return (amp.Key, cab.Key);
    }

    /// <summary>A well-formed reply naming real gear.</summary>
    internal static string ValidReply(string? ampKey = null, string? cabKey = null, string? knobs = null)
    {
        var (amp, cab) = RealGuitarGear();
        return $$"""
            {
              "name": "Generated",
              "descriptors": ["Clean"],
              "volumeDb": -12,
              "rationale": "a test reply",
              "amp": { "key": "{{ampKey ?? amp}}"{{(knobs is null ? "" : ", \"knobs\": " + knobs)}} },
              "cabinet": { "key": "{{cabKey ?? cab}}" }
            }
            """;
    }

    /// <summary>The first knob of a gear item, for an out-of-range fixture.</summary>
    internal static RsGearKnob FirstKnob(string gearKey)
    {
        Assert.True(RsGearCatalog.Shared.TryGetGear(gearKey, out var gear));
        Assert.NotNull(gear.Knobs);
        return gear.Knobs![0];
    }

    internal static string KnobJson(string pot, double value) =>
        JsonSerializer.Serialize(new[] { new { pot, value } });
}
