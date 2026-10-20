using DotCharter.Services.Ai.Match;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Tests.Ai.Sections;

/// <summary>
///     The sections lane's fakes and synthetic stems.
///     <para>
///         Three of the five interfaces this lane talks to belong to other lanes
///         (<see cref="IToneMatcher" />, <see cref="IToneMatchPriorSource" />) or cost
///         minutes per call (<see cref="IStemIsolator" />), so they are faked here and
///         the fakes COUNT what they were asked for — a sequential set matcher that
///         quietly isolated a section twice, or asked a prior source for a section
///         that already had one, would otherwise look exactly like a working one.
///     </para>
/// </summary>
internal static class SectionFakes
{
    internal const int Rate = 48_000;

    /// <summary>
    ///     A plucked-string-ish test signal: six harmonics of a repeating four-note
    ///     figure, each note decaying. The notes CHANGE, deliberately — a segmenter
    ///     that reacts to a new note instead of to a new tone must fail its test, and
    ///     it cannot fail it against a signal that never plays anything else.
    /// </summary>
    internal static float[] Plucks(double seconds, int seed = 5, double noteSeconds = 0.5)
    {
        var rng = new Random(seed);
        var n = (int)(seconds * Rate);
        var samples = new float[n];
        double[] semitones = [0, 5, 7, 3];

        var notes = (int)Math.Ceiling(seconds / noteSeconds);
        for (var note = 0; note < notes; note++)
        {
            var hz = 110.0 * Math.Pow(2.0, semitones[note % semitones.Length] / 12.0);
            var start = (int)(note * noteSeconds * Rate);
            var length = Math.Min((int)(noteSeconds * Rate), n - start);
            var phase = rng.NextDouble() * Math.PI;

            for (var i = 0; i < length; i++)
            {
                var t = i / (double)Rate;
                var envelope = Math.Exp(-3.5 * t);
                double v = 0;
                for (var k = 1; k <= 6; k++) v += Math.Sin(2 * Math.PI * hz * k * t + phase) / k;
                samples[start + i] = (float)(0.35 * envelope * v);
            }
        }

        return samples;
    }

    /// <summary>The same playing, hard-clipped: a different TONE over the same content.
    /// Clipping is what a distortion pedal does, and it is the one change that moves
    /// the crest factor and the high-band energy together.</summary>
    internal static float[] Clipped(double seconds, int seed = 5)
    {
        var clean = Plucks(seconds, seed);
        var driven = new float[clean.Length];
        for (var i = 0; i < clean.Length; i++)
            driven[i] = (float)Math.Clamp(clean[i] * 9.0, -0.3, 0.3);
        return driven;
    }

    /// <summary>Clean for <paramref name="firstSeconds" />, then driven — one timbre
    /// boundary at a known instant and no other.</summary>
    internal static MonoAudio Spliced(double firstSeconds, double secondSeconds)
    {
        var a = Plucks(firstSeconds);
        var b = Clipped(secondSeconds, seed: 11);
        var all = new float[a.Length + b.Length];
        a.CopyTo(all, 0);
        b.CopyTo(all, a.Length);
        return new MonoAudio(all, Rate);
    }

    /// <summary>One timbre, all the way through.</summary>
    internal static MonoAudio Constant(double seconds) => new(Plucks(seconds), Rate);

    /// <summary>
    ///     Hands back a prepared stem, cut to the span asked for, and remembers every
    ///     stem name it was asked for.
    /// </summary>
    internal sealed class FakeIsolator : IStemIsolator
    {
        private readonly Dictionary<string, MonoAudio> _perStem = new(StringComparer.OrdinalIgnoreCase);
        private readonly MonoAudio _default;

        public FakeIsolator(MonoAudio defaultStem) => _default = defaultStem;

        public List<string> Requested { get; } = [];

        public int Calls => Requested.Count;

        /// <summary>Stems this isolator refuses, with the message it refuses them with.</summary>
        public Dictionary<string, string> Failures { get; } = new(StringComparer.OrdinalIgnoreCase);

        public FakeIsolator WithStem(string stem, MonoAudio audio)
        {
            _perStem[stem] = audio;
            return this;
        }

        public Task<MonoAudio> IsolateGuitarAsync(
            string songAudioPath, TimeSpan from, TimeSpan to, IProgress<string>? status, CancellationToken ct) =>
            IsolateAsync(songAudioPath, ToneStems.Guitar, from, to, status, ct);

        public Task<MonoAudio> IsolateAsync(
            string songAudioPath, string stem, TimeSpan from, TimeSpan to, IProgress<string>? status,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Requested.Add(stem);
            if (Failures.TryGetValue(stem, out var message))
                throw new InvalidOperationException(message);

            var source = _perStem.TryGetValue(stem, out var specific) ? specific : _default;
            var start = Math.Clamp((int)(from.TotalSeconds * source.SampleRate), 0, source.Samples.Length);
            var end = Math.Clamp((int)(to.TotalSeconds * source.SampleRate), start, source.Samples.Length);
            return Task.FromResult(new MonoAudio(source.Samples[start..end], source.SampleRate));
        }
    }

    /// <summary>Records every request and answers with a rig named after the section,
    /// so a test can prove WHICH target and WHICH prior reached the matcher.</summary>
    internal sealed class RecordingMatcher : IToneMatcher
    {
        public List<ToneMatchRequest> Requests { get; } = [];

        public Func<ToneMatchRequest, ToneMatchResult>? Answer { get; set; }

        public Task<ToneMatchResult> MatchAsync(
            ToneMatchRequest request, IProgress<string>? status, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request);
            if (Answer is not null) return Task.FromResult(Answer(request));

            return Task.FromResult(new ToneMatchResult(
                [new ToneMatchCandidate(Rig("amp"), 0.25, "retrieval")], 0.1, TimeSpan.FromSeconds(1), []));
        }
    }

    /// <summary>A prior source that counts its calls, so "only when the section has
    /// none" is a fact and not a comment.</summary>
    internal sealed class FakePriorSource(bool available, ToneMatchPrior? prior = null) : IToneMatchPriorSource
    {
        public int Calls { get; private set; }

        public List<ToneSection> Asked { get; } = [];

        public bool Throws { get; set; }

        public bool IsAvailable { get; } = available;

        public Task<ToneMatchPrior?> GetPriorAsync(
            ToneSection section, string? songTitle, string? artist, IProgress<string>? status, CancellationToken ct)
        {
            Calls++;
            Asked.Add(section);
            if (Throws) throw new InvalidOperationException("the provider is down");
            return Task.FromResult(prior);
        }
    }

    /// <summary>
    ///     A SYNCHRONOUS progress sink. <see cref="Progress{T}" /> posts through the
    ///     synchronization context, so in a test host its callbacks land on the thread
    ///     pool AFTER the awaited call has returned — an assertion on what was
    ///     reported then passes or fails on timing.
    /// </summary>
    internal sealed class Lines : IProgress<string>
    {
        private readonly List<string> _lines = [];

        public IReadOnlyList<string> Reported
        {
            get { lock (_lines) return [.. _lines]; }
        }

        public void Report(string value)
        {
            lock (_lines) _lines.Add(value);
        }
    }

    internal static DcRigPreset Rig(params string[] moduleTypeIds) =>
        new([.. moduleTypeIds.Select(id => new DcRigModule(id, new Dictionary<string, float>()))]);

    internal static ToneSection Section(
        string id, double fromSeconds, double toSeconds, string stem = ToneStems.Guitar,
        ToneMatchPrior? prior = null) =>
        new(id, id, stem, TimeSpan.FromSeconds(fromSeconds), TimeSpan.FromSeconds(toSeconds), prior);
}
