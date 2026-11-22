using System.Text.Json;
using DotCharter.Analysis.Lyrics;
using DotCharter.Core.Domain.Vocals;
using DotCharter.LyricSync;
using DotCharter.Services.Audio.Lyrics;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Tools.LyricSync;

/// <summary>
///     The standalone CLI: argument parsing, exit codes and what it writes.
///     <para>
///         <b>Driven in-process, deliberately.</b> Spawning the executable would
///         need the 340 MB acoustic model installed on whatever machine runs the
///         gate, so the exit codes — which are the tool's only API for a script —
///         would be covered nowhere at all. <see cref="LyricSyncCommand" /> takes
///         its core and its model fetch as delegates for exactly this.
///     </para>
/// </summary>
public sealed class LyricSyncCommandTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dc-lyricsync-cli").FullName;
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();

    /// <summary>xUnit wants every awaited call to honour the test's own cancellation.</summary>
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
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>A core that answers whatever the test needs, with no model and no audio.</summary>
    private sealed class FakeCore : ILyricSyncCore
    {
        public string ModelDirectory { get; init; } = "(nowhere)";

        public bool IsModelInstalled { get; init; } = true;

        public LyricSyncResult Result { get; init; } = Aligned();

        public Action? AfterAlignment { get; init; }

        /// <summary>A status line the core reports before it answers, as the real service does.</summary>
        public string? StatusLine { get; init; }

        public string? SeenLyrics { get; private set; }

        public LyricSyncOptions? SeenOptions { get; private set; }

        public Task<LyricSyncResult> AlignAsync(
            string lyricsText, string audioPath, LyricSyncOptions? options = null,
            IProgress<string>? status = null, CancellationToken cancellationToken = default)
        {
            SeenLyrics = lyricsText;
            SeenOptions = options;
            if (StatusLine != null) status?.Report(StatusLine);
            AfterAlignment?.Invoke();
            return Task.FromResult(Result);
        }
    }

    /// <summary>"Hel-lo world" on one line, at times a test can read back.</summary>
    private static LyricSyncResult Aligned()
    {
        var sheet = LyricSheetParser.Parse("Hel-lo world");
        List<AlignedSyllable> aligned =
        [
            new(0, 1.000, 1.100, 0.4, false),
            new(1, 1.300, 1.400, 0.3, false),
            new(2, 2.000, 2.400, 0.2, false),
        ];

        return new LyricSyncResult(
            true, null, sheet, aligned,
            [
                new LyricSyncSyllable(0, "Hel", true, false, 0, 1.000, 1.100, 0.4, false),
                new LyricSyncSyllable(1, "lo", false, false, 0, 1.300, 1.400, 0.3, false),
                new LyricSyncSyllable(2, "world", false, true, 0, 2.000, 2.400, 0.2, false),
            ],
            InterpolatedCount: 0, LowConfidenceCount: 0,
            AudioSource: "mix", AudioPath: "song.wav",
            Model: "lyric-align", IsEnsemble: false, OnsetBiasSeconds: 0.0,
            AudioSeconds: 10.0, Warnings: []);
    }

    private LyricSyncCommand Command(FakeCore core, bool modelsAvailable = true) =>
        new(_out, _err, _ => core, (_, _, _) => Task.FromResult(modelsAvailable));

    private async Task<int> Run(FakeCore core, params string[] args) =>
        await Command(core).RunAsync(args, Ct);

    [Fact]
    public async Task JsonRetainsOriginalReviewFindingsAfterARepairRaisesConfidence()
    {
        var result = Aligned();
        var syllables = result.Syllables.ToArray();
        const LyricReviewReason reasons = LyricReviewReason.LowScore | LyricReviewReason.Repaired;
        syllables[0] = syllables[0] with { Confidence = 0.9, ReviewReasons = reasons };
        var core = new FakeCore { Result = result with { Syllables = syllables } };
        var output = Path.Combine(_dir, "review");
        var code = await Run(core, Touch("song.wav"), Touch("lyrics.txt", "Hel-lo world"), "--json", "--out", output);

        Assert.Equal(LyricSyncExit.Ok, code);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(output + ".lyricsync.json", Ct));
        var first = json.RootElement.GetProperty("syllables")[0];
        Assert.Equal(0.9, first.GetProperty("confidence").GetDouble());
        Assert.Equal(reasons.ToString(), first.GetProperty("reviewReasons").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TextOutputs_ReplaceDestinationWithoutOpeningItForWrite(bool json)
    {
        var outputBase = Path.Combine(_dir, "output");
        var path = Touch(json ? "output.lyricsync.json" : "output.lrc", "previous");
        using var previous = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        var args = new List<string>
        {
            Touch("song.wav"), Touch("lyrics.txt", "Hel-lo world"), "--out", outputBase, "--format", "lrc"
        };
        if (json) args.Add("--json");

        var code = await Command(new FakeCore()).RunAsync(args, Ct);

        Assert.Equal(LyricSyncExit.Ok, code);
        using var reader = new StreamReader(previous);
        Assert.Equal("previous", reader.ReadToEnd());
        Assert.Contains(json ? "audioSource" : "world", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public async Task LrcEncodingFailure_PreservesPreviousExportAndRemovesStage()
    {
        var path = Touch("output.lrc", "previous");
        var result = Aligned();
        var core = new FakeCore
        {
            Result = result with { Syllables = [result.Syllables[0] with { Text = "\uD800" }] }
        };

        await Assert.ThrowsAsync<System.Text.EncoderFallbackException>(() => Command(core).RunAsync(
            [Touch("song.wav"), Touch("lyrics.txt", "hello"), "--out", Path.Combine(_dir, "output"),
                "--format", "lrc"], Ct));

        Assert.Equal("previous", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public async Task CancellationAfterAlignment_PreservesAllPreviousOutputs()
    {
        var paths = new[]
        {
            Touch("output_vocals.xml", "previous xml"),
            Touch("output.lrc", "previous lrc"),
            Touch("output.lyricsync.json", "previous json")
        };
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var core = new FakeCore { AfterAlignment = () => cancellation.Cancel() };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Command(core).RunAsync(
            [Touch("song.wav"), Touch("lyrics.txt", "hello"), "--out", Path.Combine(_dir, "output"),
                "--format", "both", "--json"], cancellation.Token));

        Assert.Equal(["previous xml", "previous lrc", "previous json"], paths.Select(File.ReadAllText).ToArray());
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
        Assert.DoesNotContain("Wrote ", _out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancellation_while_reporting_a_warning_preserves_the_previous_xml()
    {
        var path = Touch("output_vocals.xml", "previous xml");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        using var warnings = new HeldWarningWriter();
        var core = new FakeCore { Result = Aligned() with { Warnings = ["warning"] } };
        var command = new LyricSyncCommand(_out, warnings, _ => core, (_, _, _) => Task.FromResult(true));
        var run = command.RunAsync([Touch("song.wav"), Touch("lyrics.txt", "hello"),
            "--out", Path.Combine(_dir, "output"), "--format", "rsxml"], cancellation.Token);
        await warnings.Entered.Task.WaitAsync(Ct);
        cancellation.Cancel();
        warnings.Release.SetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal("previous xml", File.ReadAllText(path));
        Assert.DoesNotContain("Wrote ", _out.ToString(), StringComparison.Ordinal);
    }

    private sealed class HeldWarningWriter : StringWriter
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task WriteLineAsync(string? value)
        {
            Entered.TrySetResult();
            await Release.Task;
            await base.WriteLineAsync(value);
        }
    }

    // ─────────────────────────── argument parsing ────────────────────────────

    [Fact]
    public void TheTwoPositionalsAreTheAudioAndTheLyrics()
    {
        var parse = LyricSyncArgumentParser.Parse(["song.wav", "lyrics.txt"]);

        Assert.True(parse.Ok);
        Assert.Equal("song.wav", parse.Arguments!.AudioPath);
        Assert.Equal("lyrics.txt", parse.Arguments.LyricsPath);
        Assert.Equal(LyricSyncFormat.RsXml, parse.Arguments.Format);
        Assert.Equal(LyricSyncLanguage.Auto, parse.Arguments.Language);
    }

    [Fact]
    public void EveryOptionIsRead()
    {
        var parse = LyricSyncArgumentParser.Parse([
            "song.wav", "lyrics.txt", "--stem", "v.wav", "--out", "base", "--format", "both",
            "--model-dir", "m", "--lang", "en", "--no-ensemble", "--span-word-gap", "--json",
            "--note", "255"
        ]);

        Assert.True(parse.Ok);
        var a = parse.Arguments!;
        Assert.Equal("v.wav", a.StemPath);
        Assert.Equal("base", a.OutputBase);
        Assert.Equal(LyricSyncFormat.Both, a.Format);
        Assert.Equal("m", a.ModelDirectory);
        Assert.Equal(LyricSyncLanguage.English, a.Language);
        Assert.True(a.NoEnsemble);
        Assert.True(a.SpanWordGap);
        Assert.True(a.Json);
        Assert.Equal(255, a.Note);
    }

    /// <summary>
    ///     <b>An unknown option must never be taken for a file name.</b> It is
    ///     almost always a typo, and accepting it as a positional is how a run ends
    ///     up aligning the wrong file and saying nothing.
    /// </summary>
    [Theory]
    [InlineData(new[] { "song.wav", "lyrics.txt", "--ensemble" }, "Unknown option")]
    [InlineData(new[] { "song.wav" }, "Usage")]
    [InlineData(new[] { "a", "b", "c" }, "Expected 2 file arguments")]
    [InlineData(new[] { "song.wav", "lyrics.txt", "--format", "midi" }, "--format must be")]
    [InlineData(new[] { "song.wav", "lyrics.txt", "--lang", "de" }, "--lang must be")]
    [InlineData(new[] { "song.wav", "lyrics.txt", "--note", "999" }, "--note must be")]
    [InlineData(new[] { "song.wav", "lyrics.txt", "--out" }, "--out needs a value")]
    [InlineData(new[] { "song.wav", "lyrics.txt", "--out", "--json" }, "--out needs a value")]
    public void ABadCommandLineIsRefusedWithAReason(string[] args, string expected)
    {
        var parse = LyricSyncArgumentParser.Parse(args);

        Assert.False(parse.Ok);
        Assert.Contains(expected, parse.Error, StringComparison.Ordinal);
    }

    // ───────────────────────────── exit codes ────────────────────────────────

    [Fact]
    public async Task AnUnparseableCommandLineIsBadInput()
    {
        var code = await Run(new FakeCore(), "song.wav", "lyrics.txt", "--nope");

        Assert.Equal(LyricSyncExit.BadInput, code);
        Assert.Contains("Unknown option", _err.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("missing-audio")]
    [InlineData("missing-lyrics")]
    [InlineData("missing-stem")]
    [InlineData("empty-lyrics")]
    public async Task EveryMissingOrEmptyInputIsBadInput(string scenario)
    {
        var audio = scenario == "missing-audio" ? Path.Combine(_dir, "gone.wav") : Touch("song.wav");
        var lyrics = scenario switch
        {
            "missing-lyrics" => Path.Combine(_dir, "gone.txt"),
            "empty-lyrics" => Touch("empty.txt", "  \n "),
            _ => Touch("lyrics.txt", "a b")
        };

        var args = new List<string> { audio, lyrics };
        if (scenario == "missing-stem") { args.Add("--stem"); args.Add(Path.Combine(_dir, "gone.wav")); }

        var code = await Command(new FakeCore()).RunAsync(args, Ct);

        Assert.Equal(LyricSyncExit.BadInput, code);
    }

    /// <summary>A model that could not be fetched is its own code — a script can retry that.</summary>
    [Fact]
    public async Task AModelThatCannotBeFetchedIsItsOwnCode()
    {
        var command = new LyricSyncCommand(
            _out, _err, _ => new FakeCore(), (_, _, _) => Task.FromResult(false));

        var code = await command.RunAsync([Touch("song.wav"), Touch("lyrics.txt", "a b")], Ct);

        Assert.Equal(LyricSyncExit.ModelUnavailable, code);
    }

    /// <summary>…and so is a model that is simply not on disk.</summary>
    [Fact]
    public async Task AnAbsentModelIsModelUnavailable()
    {
        var code = await Run(new FakeCore { IsModelInstalled = false },
            Touch("song.wav"), Touch("lyrics.txt", "a b"));

        Assert.Equal(LyricSyncExit.ModelUnavailable, code);
    }

    /// <summary>
    ///     The decode failure keeps its own code all the way out, and the message
    ///     names the fix rather than the exception.
    /// </summary>
    [Fact]
    public async Task ADecodeFailureIsItsOwnCodeAndNamesFfmpeg()
    {
        var core = new FakeCore
        {
            Result = LyricSyncResult.Failed(
                ILyricSyncCore.DecodeFailedPrefix + "Cannot decode '.aiff' without ffmpeg.")
        };

        var code = await Run(core, Touch("song.aiff"), Touch("lyrics.txt", "a b"));

        Assert.Equal(LyricSyncExit.AudioDecodeFailed, code);
        Assert.Contains("ffmpeg", _err.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    ///     A status line is on stderr by the time <c>Report</c> returns (#819). A
    ///     <see cref="Progress{T}" /> posts it to the thread pool instead, where it
    ///     lands out of order, after the process has exited, or on a writer its owner
    ///     already closed: a test host died on that <c>ObjectDisposedException</c>.
    /// </summary>
    [Fact]
    public async Task AStatusLineIsWrittenBeforeReportReturns()
    {
        string? seen = null;
        var core = new FakeCore { StatusLine = "Decoding audio", AfterAlignment = () => seen = _err.ToString() };

        var code = await Run(core, Touch("song.wav"), Touch("lyrics.txt", "a b"));

        Assert.Equal(LyricSyncExit.Ok, code);
        Assert.Contains("Decoding audio", seen, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHelpfulRunSucceedsAndPrintsTheLicences()
    {
        var code = await Run(new FakeCore(), "--help");

        Assert.Equal(LyricSyncExit.Ok, code);
        var text = _out.ToString();
        Assert.Contains("CC-BY-NC 4.0", text, StringComparison.Ordinal);
        Assert.Contains("Apache 2.0", text, StringComparison.Ordinal);
        Assert.Contains("GPL-3.0-or-later", text, StringComparison.Ordinal);
        Assert.Contains("beat grid", text, StringComparison.Ordinal);
    }

    /// <summary>
    ///     <c>--about</c> quotes the corpus figures, and both corpora — the two
    ///     disagree by twelve points and quoting one as "the" accuracy is how the
    ///     optimistic figure shipped for several releases.
    /// </summary>
    [Fact]
    public async Task AboutQuotesBothCorpora()
    {
        var code = await Run(new FakeCore(), "--about");

        Assert.Equal(LyricSyncExit.Ok, code);
        var text = _out.ToString();
        Assert.Contains("91.3%", text, StringComparison.Ordinal);
        Assert.Contains("79.2%", text, StringComparison.Ordinal);
        Assert.Contains("66.2%", text, StringComparison.Ordinal);
        Assert.Contains("75.5%", text, StringComparison.Ordinal);
    }

    // ───────────────────────────── the outputs ───────────────────────────────

    [Fact]
    public async Task BothFormatsAreWrittenBesideTheAudioByDefault()
    {
        var audio = Touch("song.wav");
        var code = await Run(new FakeCore(), audio, Touch("lyrics.txt", "Hel-lo world"),
            "--format", "both", "--json");

        Assert.Equal(LyricSyncExit.Ok, code);
        var expectedBase = Path.Combine(_dir, "song");
        Assert.True(File.Exists(expectedBase + "_vocals.xml"));
        Assert.True(File.Exists(expectedBase + ".lrc"));
        Assert.True(File.Exists(expectedBase + ".lyricsync.json"));

        // The JSON is the raw table WITH confidences — that is its whole purpose.
        var json = await File.ReadAllTextAsync(expectedBase + ".lyricsync.json", Ct);
        Assert.Contains("\"confidence\"", json, StringComparison.Ordinal);
        Assert.Contains("\"world\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnlyTheRequestedFormatIsWritten()
    {
        var audio = Touch("song.wav");
        await Run(new FakeCore(), audio, Touch("lyrics.txt", "a b"), "--format", "lrc");

        var expectedBase = Path.Combine(_dir, "song");
        Assert.True(File.Exists(expectedBase + ".lrc"));
        Assert.False(File.Exists(expectedBase + "_vocals.xml"));
    }

    /// <summary>The CLI's flags reach the seam — otherwise they are decoration.</summary>
    [Fact]
    public async Task TheFlagsReachTheSeam()
    {
        var core = new FakeCore();
        var stem = Touch("vocals.wav");

        await Run(core, Touch("song.wav"), Touch("lyrics.txt", "a b"),
            "--stem", stem, "--lang", "romaji", "--no-ensemble");

        Assert.Equal(stem, core.SeenOptions!.VocalStemPath);
        Assert.False(core.SeenOptions.ForceEnglishModel);
        Assert.False(core.SeenOptions.AllowEnsemble);
        Assert.Equal("a b", core.SeenLyrics);
    }

    /// <summary>
    ///     The tool reads its status text through <c>LocalizationService</c>, whose
    ///     type initializer builds <c>en-US</c>. Globalization-invariant mode refuses
    ///     every culture but the invariant one, so with it on the published tool died
    ///     before aligning anything (found while checking #809).
    /// </summary>
    [Fact]
    public void TheToolKeepsTheCultureDataItsLocalizationNeeds()
    {
        var tool = Path.Combine(TestPaths.RepoRoot(), "tools", "lyric-sync", "DotCharter.LyricSync");
        var program = File.ReadAllText(Path.Combine(tool, "Program.cs"));
        var project = System.Xml.Linq.XDocument.Load(Path.Combine(tool, "DotCharter.LyricSync.csproj"));

        Assert.Contains("LocalizationService", program, StringComparison.Ordinal);
        Assert.DoesNotContain(project.Descendants("InvariantGlobalization"),
            e => string.Equals(e.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase));
    }
}
