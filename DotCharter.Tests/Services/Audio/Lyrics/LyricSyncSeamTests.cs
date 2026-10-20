using DotCharter.Analysis.Lyrics;
using DotCharter.Services.Audio.Lyrics;
using DotCharter.Tone.Infrastructure.Storage;
using JuceSharp.StemSeparation.Onnx;

namespace DotCharter.Tests.Services.Audio.Lyrics;

/// <summary>
///     The chart-free seam <c>dotcharter-lyricsync</c> stands on:
///     <see cref="ILyricSyncCore.AlignAsync" />.
///     <para>
///         <b>These are the first tests to drive the orchestration at all.</b>
///         Everything between "here is some text and a file" and "here are the
///         syllable seconds" — the stem choice, the model router, the ensemble, the
///         low-confidence count — used to be reachable only through a 340 MB ONNX
///         model, so it was covered by reading the source. A fake acoustic session
///         producing a KNOWN emission matrix makes the whole path assertable in
///         milliseconds.
///     </para>
/// </summary>
public sealed class LyricSyncSeamTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dc-lyricsync-seam").FullName;

    /// <summary>xUnit wants every awaited call to honour the test's own cancellation.</summary>
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    ///     Options for the two cases that assert ABSOLUTE seconds, with the
    ///     wildcard priced out of the search.
    ///     <para>
    ///         <b>The shipping star cost of 0.0 is a measured product decision and
    ///         makes absolute onsets meaningless in a fixture this small.</b> A free
    ///         star is ≥ every emission, so it provably eats the front of the
    ///         syllable it precedes — pinned, on purpose, by
    ///         <c>CtcForcedAlignerTests</c> — and over a two-letter matrix that is
    ///         the whole signal: the first syllable lands wherever the trellis can
    ///         no longer delay it. Pricing the star out here isolates what these
    ///         cases are for, which is the arithmetic from frames to seconds and
    ///         back. The star POLICY belongs to the aligner's own tests, and this
    ///         file must not silently re-litigate it.
    ///     </para>
    /// </summary>
    private static LyricSyncOptions Exact(double onsetBiasSeconds = 0.0) =>
        new() { StarLogProb = -20.0, OnsetBiasSeconds = onsetBiasSeconds };

    public void Dispose() => BestEffortDelete.Directory(_dir);

    /// <summary>An installed model: the presence check is <c>model.onnx</c> + <c>vocab.json</c>.</summary>
    private string InstallModel(string id = LyricModelCatalog.MultilingualId)
    {
        var folder = Path.Combine(_dir, "models", id);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "model.onnx"), "not a real graph");
        File.WriteAllText(Path.Combine(folder, "vocab.json"), "{}");
        return Path.Combine(_dir, "models");
    }

    private string Touch(string name, string content = "audio")
    {
        var path = Path.Combine(_dir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>
    ///     A vocabulary over the letters this fixture uses, plus CTC's blank.
    ///     <c>FromTokens</c> folds to lower case, which is what makes two heads
    ///     comparable at all.
    /// </summary>
    private static LyricVocabulary Vocab() =>
        LyricVocabulary.FromTokens(["<pad>", "a", "b", "c", "d"]);

    /// <summary>
    ///     An emission matrix that spells one letter per <paramref name="framesPerLetter" />
    ///     frames and blank in between, at 20 ms a frame. So "a" then "b" with 5
    ///     frames each puts "a" at 0.00 s and "b" at 0.10 s — times a test can
    ///     assert against rather than merely observe.
    /// </summary>
    private static CtcEmissions Spell(string letters, int framesPerLetter = 5, double secondsPerFrame = 0.02)
    {
        var vocab = Vocab();
        var width = vocab.WidthWithStar;
        var frames = letters.Length * framesPerLetter;
        var data = new float[frames * width];

        for (var f = 0; f < frames; f++)
        {
            var letter = letters[f / framesPerLetter];
            // The letter wins on the FIRST frame of its slot and blank wins on the
            // rest — which is exactly how a real CTC head behaves, and why absolute
            // per-syllable confidences look low.
            var winner = f % framesPerLetter == 0
                ? (vocab.TryGetId(letter, out var id) ? id : vocab.BlankId)
                : vocab.BlankId;

            for (var s = 0; s < width; s++)
                data[f * width + s] = (float)Math.Log(s == winner ? 0.9 : 0.1 / (width - 1));
        }

        return new CtcEmissions(data, frames, width, secondsPerFrame);
    }

    /// <summary>A session that hands back a fixed matrix and counts its opens.</summary>
    private sealed class FakeSession(CtcEmissions emissions) : ILyricAcousticSession
    {
        public LyricVocabulary Vocabulary { get; } = Vocab();

        public CtcEmissions Encode(float[] mono16k, CancellationToken cancellationToken = default)
            => emissions;

        public void Dispose() { }
    }

    private LyricAutoSyncService Core(
        string modelsRoot,
        CtcEmissions? emissions = null,
        LyricAcousticSessionOpener? opener = null,
        Func<string, IList<string>, float[]>? audio = null,
        Func<string, string?>? stemLocator = null)
        => new(
            FakeLocalization.English(),
            logger: null,
            modelDirectoryOverride: null,
            vocalStemLocator: stemLocator ?? (_ => null),
            sessionOpener: opener ?? ((_, _, _) => new FakeSession(emissions ?? Spell("ab"))),
            audioLoader: audio ?? ((_, _) => new float[16000]),
            modelRoots: [modelsRoot]);

    /// <summary>
    ///     The headline: a known emission matrix produces known syllable seconds.
    ///     "a-b" is one word of two syllables, so both land where the letters fire
    ///     — 5 frames × 20 ms apart.
    /// </summary>
    [Fact]
    public async Task AKnownEmissionMatrixProducesKnownSyllableSeconds()
    {
        var roots = InstallModel();

        var result = await Core(roots)
            .AlignAsync("a-b", Touch("song.wav"), Exact(), cancellationToken: Ct);

        Assert.True(result.Ran, result.Error);
        Assert.Equal(2, result.Syllables.Count);
        // Frame 1 and frame 5, at 20 ms a frame. The first syllable starts at
        // frame ONE, not zero, because the wildcard that wraps every line takes a
        // frame of its own — the mechanism that lets an instrumental intro be
        // absorbed by something other than the first word.
        Assert.Equal(0.02, result.Syllables[0].StartSeconds, 3);
        Assert.Equal(0.10, result.Syllables[1].StartSeconds, 3);

        // Structure survives the projection: "a-b" is one word.
        Assert.True(result.Syllables[0].ContinuesWord);
        Assert.False(result.Syllables[1].ContinuesWord);
        Assert.True(result.Syllables[1].EndsLine);
    }

    /// <summary>
    ///     <see cref="LyricSyncResult.Aligned" /> is index-parallel to the sheet and
    ///     is what the app's own fold consumes, so a projection that quietly
    ///     reordered or dropped a syllable would mistime a chart while this file's
    ///     other assertions stayed green.
    /// </summary>
    [Fact]
    public async Task TheRawAlignmentAndTheFlatTableAgreeSyllableForSyllable()
    {
        var roots = InstallModel();

        var result = await Core(roots, Spell("abc"))
            .AlignAsync("a b\nc", Touch("song.wav"), cancellationToken: Ct);

        Assert.True(result.Ran, result.Error);
        Assert.Equal(result.Sheet.Syllables.Count, result.Aligned.Count);
        Assert.Equal(result.Aligned.Count, result.Syllables.Count);
        for (var i = 0; i < result.Aligned.Count; i++)
        {
            Assert.Equal(result.Aligned[i].StartSeconds, result.Syllables[i].StartSeconds);
            Assert.Equal(result.Sheet.Syllables[i].Text, result.Syllables[i].Text);
        }

        Assert.Equal(2, result.Sheet.LineCount);
    }

    /// <summary>
    ///     <b>An explicitly named stem is listened to, and never separated for.</b>
    ///     The whole feature's restraint rule: the caller says which file, the core
    ///     opens that file, nothing starts a separation.
    /// </summary>
    [Fact]
    public async Task AnExplicitStemIsWhatGetsDecoded()
    {
        var roots = InstallModel();
        var stem = Touch("stems/vocals.wav");
        var decoded = new List<string>();

        var result = await Core(roots, audio: (path, _) => { decoded.Add(path); return new float[16000]; })
            .AlignAsync("a-b", Touch("song.wav"), new LyricSyncOptions { VocalStemPath = stem },
                cancellationToken: Ct);

        Assert.True(result.Ran, result.Error);
        Assert.Equal([stem], decoded);
        Assert.Equal("vocal stem", result.AudioSource);
        Assert.Equal(stem, result.AudioPath);
        Assert.Empty(result.Warnings);
    }

    /// <summary>
    ///     <b>The complement stem is the silent failure this guards.</b> Every
    ///     separator also emits <c>no_vocals</c>; aligning against it throws
    ///     nothing, returns every syllable and simply places them badly. A warning
    ///     is the only signal there can be — refusing would break the legitimate
    ///     case of a hand-renamed stem.
    /// </summary>
    [Fact]
    public async Task AStemThatIsNotNamedLikeAVocalStemIsWarnedAbout()
    {
        var roots = InstallModel();
        var stem = Touch("stems/no_vocals.wav");

        var result = await Core(roots)
            .AlignAsync("a-b", Touch("song.wav"), new LyricSyncOptions { VocalStemPath = stem },
                cancellationToken: Ct);

        Assert.True(result.Ran, result.Error);
        Assert.Contains(result.Warnings, w => w.Contains("no_vocals", StringComparison.Ordinal));
    }

    /// <summary>
    ///     A stem the caller NAMED and that is not there is a hard failure. Falling
    ///     back to the mix would read to the caller as a bad model rather than as a
    ///     typo — and the mix is measurably worse.
    /// </summary>
    [Fact]
    public async Task ANamedStemThatIsMissingFailsInsteadOfUsingTheMix()
    {
        var roots = InstallModel();

        var result = await Core(roots).AlignAsync("a-b", Touch("song.wav"),
            new LyricSyncOptions { VocalStemPath = Path.Combine(_dir, "nope.wav") },
            cancellationToken: Ct);

        Assert.False(result.Ran);
        Assert.Equal("no-stem", result.Error);
    }

    /// <summary>
    ///     A decode failure carries its own prefix, because it is the one failure a
    ///     caller can act on — and the CLI turns it into its own exit code.
    /// </summary>
    [Fact]
    public async Task ADecodeFailureIsReportedWithItsOwnPrefix()
    {
        var roots = InstallModel();

        var result = await Core(roots,
                audio: (_, _) => throw new NotSupportedException("Cannot decode '.aiff' without ffmpeg."))
            .AlignAsync("a-b", Touch("song.aiff"), cancellationToken: Ct);

        Assert.False(result.Ran);
        Assert.StartsWith(ILyricSyncCore.DecodeFailedPrefix, result.Error);
        Assert.Contains("ffmpeg", result.Error, StringComparison.Ordinal);
    }

    /// <summary>The three preconditions, in the order the app has always reported them.</summary>
    [Theory]
    [InlineData("missing-audio", "no-audio")]
    [InlineData("no-model", "no-model")]
    [InlineData("blank-lyrics", "no-lyrics")]
    public async Task EachPreconditionHasItsOwnStableCode(string scenario, string expected)
    {
        var roots = scenario == "no-model" ? Path.Combine(_dir, "empty-models") : InstallModel();
        var audio = scenario == "missing-audio" ? Path.Combine(_dir, "gone.wav") : Touch("song.wav");
        var lyrics = scenario == "blank-lyrics" ? "   \n\n" : "a-b";

        var result = await Core(roots).AlignAsync(lyrics, audio, cancellationToken: Ct);

        Assert.False(result.Ran);
        Assert.Equal(expected, result.Error);
    }

    /// <summary>
    ///     <b>Both heads installed means both heads RUN.</b> The ensemble is where
    ///     the accuracy is (English p90 602 → 292 ms) and it is the default; a
    ///     regression that quietly stopped opening the second session would cost
    ///     that with nothing failing.
    /// </summary>
    [Fact]
    public async Task BothInstalledHeadsAreOpenedAndTheResultSaysSo()
    {
        InstallModel();
        var roots = InstallModel(LyricModelCatalog.EnglishId);
        var opened = new List<string>();

        var result = await Core(roots, opener: (model, _, _) =>
        {
            opened.Add(model.Id);
            return new FakeSession(Spell("ab"));
        }).AlignAsync("a-b", Touch("song.wav"), cancellationToken: Ct);

        Assert.True(result.Ran, result.Error);
        Assert.Equal(2, opened.Count);
        Assert.True(result.IsEnsemble);
        Assert.Contains('+', result.Model);
    }

    /// <summary>
    ///     …and <c>AllowEnsemble = false</c> — the CLI's <c>--no-ensemble</c> — runs
    ///     ONE head, with that model's own SOLO bias rather than the combination's.
    /// </summary>
    [Fact]
    public async Task TurningTheEnsembleOffOpensOneHead()
    {
        InstallModel();
        var roots = InstallModel(LyricModelCatalog.EnglishId);
        var opened = new List<string>();

        var result = await Core(roots, opener: (model, _, _) =>
            {
                opened.Add(model.Id);
                return new FakeSession(Spell("ab"));
            })
            .AlignAsync("a-b", Touch("song.wav"), new LyricSyncOptions { AllowEnsemble = false },
                cancellationToken: Ct);

        Assert.True(result.Ran, result.Error);
        Assert.Equal([LyricModelCatalog.MultilingualId], opened);
        Assert.False(result.IsEnsemble);
    }

    /// <summary>
    ///     <c>--lang en</c> forces the English head. The router is deliberately
    ///     biased towards MMS-FA — a wrong "English" hands a German sheet to a model
    ///     that has never seen the language — so the override has to be explicit.
    /// </summary>
    [Fact]
    public async Task ForcingEnglishRoutesToTheEnglishHeadAsPrimary()
    {
        InstallModel();
        var roots = InstallModel(LyricModelCatalog.EnglishId);
        var opened = new List<string>();

        var result = await Core(roots, opener: (model, _, _) =>
            {
                opened.Add(model.Id);
                return new FakeSession(Spell("ab"));
            })
            .AlignAsync("a-b", Touch("song.wav"),
                new LyricSyncOptions { ForceEnglishModel = true, AllowEnsemble = false },
                cancellationToken: Ct);

        Assert.True(result.Ran, result.Error);
        Assert.Equal([LyricModelCatalog.EnglishId], opened);
    }

    /// <summary>
    ///     The caller's bias is ADDED to the model's own, never replaces it — the
    ///     per-model part describes where that CTC head fires inside a syllable and
    ///     is not the caller's business.
    /// </summary>
    [Fact]
    public async Task TheCallersBiasShiftsEveryOnsetAndIsReportedBack()
    {
        var roots = InstallModel();

        var shifted = await Core(roots)
            .AlignAsync("a-b", Touch("song.wav"), Exact(onsetBiasSeconds: 0.25),
                cancellationToken: Ct);

        Assert.True(shifted.Ran, shifted.Error);
        Assert.Equal(0.25, shifted.OnsetBiasSeconds, 6);
        // The same 0.02 / 0.10 frames, moved a quarter second later.
        Assert.Equal(0.27, shifted.Syllables[0].StartSeconds, 3);
        Assert.Equal(0.35, shifted.Syllables[1].StartSeconds, 3);
    }

    /// <summary>
    ///     <b>The app's own path goes through the seam, and this is the guard that
    ///     the two cannot fork.</b> A second implementation would pass every test
    ///     above and still drift from what DotCharter ships; a source lint is the
    ///     only thing that sees the call itself.
    /// </summary>
    [Fact]
    public void TheAppsAutoSyncRoutesThroughTheSeam()
    {
        var source = File.ReadAllText(RepoPath("DotCharter.Services/Audio/Lyrics/LyricAutoSyncService.cs"));

        Assert.Contains("ILyricSyncCore", source, StringComparison.Ordinal);

        var syncCore = source.IndexOf("private LyricAutoSyncOutcome SyncCore(", StringComparison.Ordinal);
        Assert.True(syncCore > 0, "SyncCore has moved — repoint this guard.");

        var body = source[syncCore..];
        var call = body.IndexOf("Align(lyricsText, audioPath, options.ToSyncOptions()", StringComparison.Ordinal);
        Assert.True(call > 0, "the app's auto-sync no longer runs through the standalone seam.");

        // …and the fold onto the beat grid must come AFTER it, or the seam is
        // being called for something other than the alignment it owns.
        var fold = body.IndexOf("BuildPhrases(", StringComparison.Ordinal);
        Assert.True(fold > call, "the beat-grid fold escaped the seam's result.");
    }

    /// <summary>
    ///     <see cref="LyricAutoSyncOptions.ToSyncOptions" /> is the ONE projection,
    ///     and it must carry every knob the alignment itself reads. A knob left
    ///     behind here is a setting that silently stops working in the app while the
    ///     CLI keeps honouring it.
    /// </summary>
    [Fact]
    public void TheProjectionCarriesEveryAlignmentKnob()
    {
        var projected = new LyricAutoSyncOptions
        {
            OnsetBiasSeconds = -0.017,
            StarLogProb = -0.5,
            Provider = OnnxExecutionProvider.Cpu
        }.ToSyncOptions();

        Assert.Equal(-0.017, projected.OnsetBiasSeconds, 6);
        Assert.Equal(-0.5, projected.StarLogProb, 6);
        Assert.Equal(OnnxExecutionProvider.Cpu, projected.Provider);
    }

    private static string RepoPath(string relative) =>
        Path.Combine(TestPaths.RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar));
}
