using System.Text.Json;
using DotCharter.Analysis.BeatMap;
using DotCharter.Analysis.Lyrics;
using DotCharter.Core.Domain.Vocals;
using DotCharter.LyricSync;
using DotCharter.Services.Audio.Lyrics;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Tools.LyricSync;

/// <summary>
///     The CLI gets the app's lyric fold (#809): syllable spacing on a beat grid
///     (tracked, or <c>--bpm</c>), the fixed gap only as the fallback, the app's
///     review flags in every output, and — through the REAL
///     <see cref="LyricAutoSyncService" /> behind a fake acoustic session — the
///     stem-duration check and the envelope-driven #627 review.
/// </summary>
public sealed class LyricSyncSpacingCommandTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dc-lyricsync-spacing").FullName;
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _out.Dispose();
        _err.Dispose();
        BestEffortDelete.Directory(_dir);
    }

    private string Touch(string name, string content = "x")
    {
        var path = Path.Combine(_dir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private sealed class FixedCore(LyricSyncResult result) : ILyricSyncCore
    {
        public string ModelDirectory => "(nowhere)";

        public bool IsModelInstalled => true;

        public Task<LyricSyncResult> AlignAsync(string lyricsText, string audioPath, LyricSyncOptions? options = null,
            IProgress<string>? status = null, CancellationToken cancellationToken = default) => Task.FromResult(result);
    }

    /// <summary>"Hel-lo world": "Hel" spans to "lo" (1.3 s) inside its word; "lo" ends a word.</summary>
    private static LyricSyncResult Aligned(bool interpolateWorld = false)
    {
        var sheet = LyricSheetParser.Parse("Hel-lo world");
        List<AlignedSyllable> aligned =
        [
            new(0, 1.000, 1.100, 0.4, false),
            new(1, 1.300, 1.400, 0.3, false),
            new(2, 2.000, 2.400, 0.2, interpolateWorld),
        ];
        return new LyricSyncResult(true, null, sheet, aligned,
            [.. sheet.Syllables.Select((s, i) => new LyricSyncSyllable(i, s.Text, s.ContinuesWord, s.EndsLine, s.LineIndex,
                aligned[i].StartSeconds, aligned[i].EndSeconds, aligned[i].Confidence, aligned[i].IsInterpolated))],
            0, 0, "mix", "song.wav", "lyric-align", false, 0.0, 10.0, []);
    }

    private LyricSyncCommand Command(ILyricSyncCore core, Func<string, CancellationToken, LyricSyncBeatGrid>? detect = null) =>
        new(_out, _err, _ => core, (_, _, _) => Task.FromResult(true), detect);

    private async Task<(int Code, JsonElement Json)> RunJson(ILyricSyncCore core, Func<string, CancellationToken, LyricSyncBeatGrid>? detect,
        params string[] extra)
    {
        var output = Path.Combine(_dir, "out");
        var code = await Command(core, detect).RunAsync(
            [Touch("song.wav"), Touch("lyrics.txt", "Hel-lo world"), "--out", output, "--json", "--format", "both", .. extra], Ct);
        var json = File.Exists(output + ".lyricsync.json")
            ? JsonDocument.Parse(await File.ReadAllTextAsync(output + ".lyricsync.json", Ct)).RootElement.Clone()
            : default;
        return (code, json);
    }

    private static double[] Ends(JsonElement json) =>
        [.. json.GetProperty("syllables").EnumerateArray().Select(s => s.GetProperty("end").GetDouble())];

    /// <summary>The app's own fold on the same grid: the reference the CLI must match exactly.</summary>
    private static double[] AppEnds(LyricSyncResult result, IList<BeatMarker> beats, bool spanWordGap = false) =>
    [
        .. LyricAutoSyncService.BuildPhrases(result.Sheet, result.Aligned, beats,
                LyricSyncCommand.SpacingOptions(new LyricSyncArguments("a", "b") { SpanWordGap = spanWordGap }), result.Envelope)
            .SelectMany(p => p.Lyrics).Select(l => l.EndPosition.GetPositionMs(beats) / 1000.0)
    ];

    private static LyricSyncBeatGrid Tracked(double secondsPerBeat) =>
        LyricSyncBeatGrid.FromDetected([.. Enumerable.Range(0, 20).Select(i =>
            new DetectedBeat(i * secondsPerBeat, 60.0 / secondsPerBeat, 0.9f, i % 4 == 0))]);

    [Fact]
    public async Task ABpmGridSpacesSyllablesExactlyAsTheAppDoes()
    {
        var result = Aligned();
        var (code, json) = await RunJson(new FixedCore(result), null, "--bpm", "60");

        Assert.Equal(LyricSyncExit.Ok, code);
        var cli = Ends(json);
        var app = AppEnds(result, LyricSyncBeatGrid.FromTempo(60, 0, 4, 10).Beats);
        Assert.Equal(app.Length, cli.Length);
        for (var i = 0; i < app.Length; i++) Assert.Equal(app[i], cli[i], 6);
        // One 1/64 measure at 60 BPM is 62.5 ms, not the fixed 30 ms.
        Assert.InRange(cli[0], 1.235, 1.245);
        Assert.Contains("--bpm 60 grid", _out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheGridTrackedFromTheAudioIsTheDefault()
    {
        var result = Aligned();
        var tracked = Tracked(1.0);
        string? seen = null;
        var (code, json) = await RunJson(new FixedCore(result), (path, _) => { seen = path; return tracked; });

        Assert.Equal(LyricSyncExit.Ok, code);
        Assert.EndsWith("song.wav", seen, StringComparison.Ordinal);
        var app = AppEnds(result, tracked.Beats);
        var cli = Ends(json);
        for (var i = 0; i < app.Length; i++) Assert.Equal(app[i], cli[i], 6);
        Assert.InRange(cli[0], 1.235, 1.245);
        Assert.Contains("tracked from the audio", json.GetProperty("spacing").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoBeatDetectSkipsTheTrackerAndUsesTheFixedGap()
    {
        var (code, json) = await RunJson(new FixedCore(Aligned()),
            (_, _) => throw new InvalidOperationException("must not run"), "--no-beat-detect");

        Assert.Equal(LyricSyncExit.Ok, code);
        Assert.Equal(1.3 - LyricSyncSpacing.FallbackGapMilliseconds / 1000.0, Ends(json)[0], 4);
        Assert.Contains("fixed 30 ms gap", _out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedTrackerFallsBackToTheFixedGapAndSaysSo()
    {
        var (code, json) = await RunJson(new FixedCore(Aligned()), (_, _) => throw new IOException("unreadable"));

        Assert.Equal(LyricSyncExit.Ok, code);
        Assert.Equal(1.3 - LyricSyncSpacing.FallbackGapMilliseconds / 1000.0, Ends(json)[0], 4);
        Assert.Contains("Beat tracking failed (unreadable)", _err.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    ///     The fallback runs the app's rule with a 30 ms gap, so it trims an end that
    ///     reaches past the next onset inside a word — the old CLI never shortened it.
    /// </summary>
    [Fact]
    public void TheFallbackTrimsAnOverlapInsideAWordLikeTheApp()
    {
        var sheet = LyricSheetParser.Parse("a-b c");
        List<AlignedSyllable> aligned = [new(0, 1.0, 1.94, 0.5, false), new(1, 1.95, 2.0, 0.5, false), new(2, 3.0, 3.1, 0.5, false)];
        var result = new LyricSyncResult(true, null, sheet, aligned,
            [.. sheet.Syllables.Select((s, i) => new LyricSyncSyllable(i, s.Text, s.ContinuesWord, s.EndsLine, s.LineIndex,
                aligned[i].StartSeconds, aligned[i].EndSeconds, 0.5, false))],
            0, 0, "mix", "song.wav", "m", false, 0, 5.0, []);

        var spaced = LyricSyncSpacing.Apply(result, LyricSyncBeatGrid.None, LyricAutoSyncOptions.Default with { SpanWordEndToNextWord = false });

        Assert.Equal(1.92, spaced[0].EndSeconds, 4);
        Assert.Equal(2.0, spaced[1].EndSeconds, 4);
        Assert.Equal(1.0, spaced[0].StartSeconds, 4);
    }

    [Fact]
    public async Task TheAppsReviewFlagsReachTheReportTheXmlAndTheJson()
    {
        var (code, json) = await RunJson(new FixedCore(Aligned(interpolateWorld: true)), null);

        Assert.Equal(LyricSyncExit.Ok, code);
        var world = json.GetProperty("syllables")[2].GetProperty("reviewReasons").GetString();
        Assert.Contains(nameof(LyricReviewReason.Interpolated), world, StringComparison.Ordinal);
        Assert.True(json.GetProperty("reviewCount").GetInt32() >= 1);
        var report = _out.ToString();
        Assert.Contains("syllables flagged", report, StringComparison.Ordinal);
        Assert.Contains("'world'", report, StringComparison.Ordinal);
        var xml = await File.ReadAllTextAsync(Path.Combine(_dir, "out_vocals.xml"), Ct);
        Assert.Contains("<!-- review: ", xml, StringComparison.Ordinal);
        Assert.Contains("<vocals count=\"3\"", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void TheGridOptionsAreRead()
    {
        var parse = LyricSyncArgumentParser.Parse(
            ["s.wav", "l.txt", "--bpm", "92.5", "--offset", "1.25", "--beats-per-bar", "3", "--no-beat-detect"]);

        Assert.True(parse.Ok, parse.Error);
        Assert.Equal(92.5, parse.Arguments!.Bpm);
        Assert.Equal(1.25, parse.Arguments.OffsetSeconds);
        Assert.Equal(3, parse.Arguments.BeatsPerBar);
        Assert.True(parse.Arguments.NoBeatDetect);
    }

    [Theory]
    [InlineData(new[] { "s.wav", "l.txt", "--bpm", "0" }, "--bpm must be")]
    [InlineData(new[] { "s.wav", "l.txt", "--bpm", "fast" }, "--bpm must be")]
    [InlineData(new[] { "s.wav", "l.txt", "--bpm" }, "--bpm needs a value")]
    [InlineData(new[] { "s.wav", "l.txt", "--offset", "2" }, "need --bpm")]
    [InlineData(new[] { "s.wav", "l.txt", "--bpm", "120", "--beats-per-bar", "0" }, "--beats-per-bar must be")]
    public void ABadGridOptionIsRefusedWithAReason(string[] args, string expected)
    {
        var parse = LyricSyncArgumentParser.Parse(args);

        Assert.False(parse.Ok);
        Assert.Contains(expected, parse.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void ATempoGridHasItsDownbeatAtTheOffsetAndCoversTheStart()
    {
        var grid = LyricSyncBeatGrid.FromTempo(120, 1.25, 3, 10);

        Assert.True(grid.Beats[0].Time <= 0);
        var downbeat = grid.Beats.Single(b => Math.Abs(b.Time - 1250) < 1e-6);
        Assert.True(downbeat.IsMeasure);
        Assert.Equal(3, downbeat.BeatsInMeasure);
        Assert.True(grid.Beats[^1].Time >= 10_000);
        Assert.Equal(120, grid.AverageBpm, 6);
    }

    // ───────────── the real service behind the CLI (fake acoustic session) ─────────────

    private static readonly LyricVocabulary Vocab = LyricVocabulary.FromTokens(["<pad>", "a", "b", "c"]);

    private sealed class FakeSession(CtcEmissions emissions) : ILyricAcousticSession
    {
        public LyricVocabulary Vocabulary => Vocab;

        public CtcEmissions Encode(float[] mono16k, CancellationToken cancellationToken = default) => emissions;

        public void Dispose() { }
    }

    /// <summary>"a", "b", "c" heard at 1.0, 1.4 and 1.8 s; 3.6 s of 20 ms frames.</summary>
    private static CtcEmissions Emissions()
    {
        const int frames = 180;
        const int width = 4;
        var probabilities = new float[frames * width];
        for (var f = 0; f < frames; f++)
        {
            var time = f * 0.02;
            var symbol = time switch
            {
                >= 1 and < 1.4 => 1,
                >= 1.4 and < 1.8 => 2,
                >= 1.8 and < 2.2 => 3,
                _ => 0
            };
            for (var column = 0; column < width; column++)
                probabilities[f * width + column] = (float)Math.Log(column == symbol ? 0.97 : 0.01);
        }

        return new CtcEmissions(probabilities, frames, width, 0.02);
    }

    /// <summary>16 kHz mono: a tone between <paramref name="from" /> and <paramref name="to" />, silence elsewhere.</summary>
    private static float[] Voice(double seconds, double from, double to) =>
        [.. Enumerable.Range(0, (int)(seconds * 16000)).Select(i =>
            i / 16000.0 >= from && i / 16000.0 < to ? (float)(0.5 * Math.Sin(i * 0.2)) : 0f)];

    private LyricAutoSyncService RealCore(Func<string, IList<string>, float[]> audio)
    {
        var folder = Path.Combine(_dir, "models", LyricModelCatalog.MultilingualId);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "model.onnx"), "not a real graph");
        File.WriteAllText(Path.Combine(folder, "vocab.json"), "{}");
        return new LyricAutoSyncService(FakeLocalization.English(), vocalStemLocator: _ => null,
            sessionOpener: (_, _, _) => new FakeSession(Emissions()), audioLoader: audio,
            modelRoots: [Path.Combine(_dir, "models")], providerMemory: new LyricProviderMemory());
    }

    private async Task<(int Code, JsonElement Json)> RunReal(LyricAutoSyncService core, string stem)
    {
        var output = Path.Combine(_dir, "real");
        var code = await Command(core).RunAsync(
            [Touch("song.wav"), Touch("lyrics.txt", "a-b-c"), "--stem", stem, "--out", output, "--json"], Ct);
        var json = code == LyricSyncExit.Ok
            ? JsonDocument.Parse(await File.ReadAllTextAsync(output + ".lyricsync.json", Ct)).RootElement.Clone()
            : default;
        return (code, json);
    }

    [Fact]
    public async Task AStemOfTheWrongLengthIsRefusedForTheMixAndTheCliSaysSo()
    {
        var stem = Touch(Path.Combine("stems", "vocals.wav"));
        var core = RealCore((path, _) => path == stem ? Voice(2.0, 1.0, 2.2) : Voice(3.6, 1.0, 2.2));

        var (code, json) = await RunReal(core, stem);

        Assert.Equal(LyricSyncExit.Ok, code);
        Assert.Contains("differs in duration from mix", _err.ToString(), StringComparison.Ordinal);
        Assert.Equal("mix", json.GetProperty("audioSource").GetString());
        // No stem, no envelope: the voice-based Gap finding of the next test is absent.
        var first = json.GetProperty("syllables")[0].GetProperty("reviewReasons").GetString();
        Assert.DoesNotContain(nameof(LyricReviewReason.Gap), first, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The #627 review: with a stem, the voice the free wildcard swallowed before
    ///     "a" is flagged as a Gap — and the flag reaches the CLI's report and JSON.
    ///     The same alignment from the mix carries no such flag (previous test).
    /// </summary>
    [Fact]
    public async Task TheStemEnvelopeReviewReachesTheCliOutput()
    {
        var stem = Touch(Path.Combine("stems", "vocals.wav"));
        var core = RealCore((_, _) => Voice(3.6, 1.0, 2.2));

        var (code, json) = await RunReal(core, stem);

        Assert.Equal(LyricSyncExit.Ok, code);
        Assert.Equal("vocal stem", json.GetProperty("audioSource").GetString());
        var first = json.GetProperty("syllables")[0].GetProperty("reviewReasons").GetString();
        Assert.Contains(nameof(LyricReviewReason.Gap), first, StringComparison.Ordinal);
        Assert.Contains("'a'", _out.ToString(), StringComparison.Ordinal);
    }
}
