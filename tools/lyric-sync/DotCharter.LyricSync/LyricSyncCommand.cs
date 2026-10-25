using System.Globalization;
using System.Text;
using System.Text.Json;
using DotCharter.Analysis.Lyrics;
using DotCharter.Services.Audio.Lyrics;
using DotCharter.Services.Export.Lyrics;

namespace DotCharter.LyricSync;

/// <summary>The process exit codes, as the README documents them.</summary>
public static class LyricSyncExit
{
    /// <summary>Everything was written.</summary>
    public const int Ok = 0;

    /// <summary>Bad input: a missing or unreadable argument, an empty sheet, a named stem that is not there.</summary>
    public const int BadInput = 2;

    /// <summary>The acoustic model is missing and could not be fetched, or it failed to load.</summary>
    public const int ModelUnavailable = 3;

    /// <summary>The audio could not be decoded.</summary>
    public const int AudioDecodeFailed = 4;
}

/// <summary>
///     The whole tool, minus <c>Main</c>.
///     <para>
///         <b>Everything is injected so the run is testable without a process.</b>
///         A CLI's exit codes are behaviour like any other, and the only way to
///         cover them cheaply is to call this class directly with a fake core
///         (<c>LyricSyncCommandTests</c>). Spawning the executable would need the
///         340 MB model to be installed on whatever machine runs the gate.
///     </para>
/// </summary>
public sealed class LyricSyncCommand
{
    private readonly TextWriter _out;
    private readonly TextWriter _error;
    private readonly Func<LyricSyncArguments, ILyricSyncCore> _core;
    private readonly Func<LyricSyncArguments, IProgress<string>?, CancellationToken, Task<bool>> _ensureModels;

    /// <param name="stdout">Where the report goes.</param>
    /// <param name="stderr">Where warnings and failures go — never mixed into the report.</param>
    /// <param name="core">Builds the alignment seam for these arguments.</param>
    /// <param name="ensureModels">
    ///     Fetches whatever is missing; returns false when alignment cannot run
    ///     afterwards. Separate from <paramref name="core" /> because it is the
    ///     only step that touches the network.
    /// </param>
    public LyricSyncCommand(
        TextWriter stdout,
        TextWriter stderr,
        Func<LyricSyncArguments, ILyricSyncCore> core,
        Func<LyricSyncArguments, IProgress<string>?, CancellationToken, Task<bool>> ensureModels)
    {
        _out = stdout;
        _error = stderr;
        _core = core;
        _ensureModels = ensureModels;
    }

    /// <summary>Parses and runs. Returns the process exit code.</summary>
    public async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken ct = default)
    {
        var parse = LyricSyncArgumentParser.Parse(args);
        if (!parse.Ok)
        {
            await _error.WriteLineAsync(parse.Error).ConfigureAwait(false);
            return LyricSyncExit.BadInput;
        }

        var arguments = parse.Arguments!;
        if (arguments.ShowHelp) { await _out.WriteAsync(HelpText).ConfigureAwait(false); return LyricSyncExit.Ok; }
        if (arguments.ShowAbout) { await _out.WriteAsync(AboutText).ConfigureAwait(false); return LyricSyncExit.Ok; }

        return await RunAsync(arguments, ct).ConfigureAwait(false);
    }

    /// <summary>Runs an already-parsed command line.</summary>
    public async Task<int> RunAsync(LyricSyncArguments arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        if (!File.Exists(arguments.AudioPath))
        {
            return await Fail($"Audio file not found: '{arguments.AudioPath}'.", LyricSyncExit.BadInput)
                .ConfigureAwait(false);
        }

        if (!File.Exists(arguments.LyricsPath))
        {
            return await Fail($"Lyrics file not found: '{arguments.LyricsPath}'.", LyricSyncExit.BadInput)
                .ConfigureAwait(false);
        }

        if (arguments.StemPath != null && !File.Exists(arguments.StemPath))
        {
            return await Fail($"Vocal stem not found: '{arguments.StemPath}'.", LyricSyncExit.BadInput)
                .ConfigureAwait(false);
        }

        var lyrics = await File.ReadAllTextAsync(arguments.LyricsPath, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(lyrics))
        {
            return await Fail($"'{arguments.LyricsPath}' has no lyrics in it.", LyricSyncExit.BadInput)
                .ConfigureAwait(false);
        }

        var status = new Progress<string>(m => _error.WriteLine(m));

        if (!await _ensureModels(arguments, status, ct).ConfigureAwait(false))
        {
            return await Fail(
                "The alignment model is not available. See --help for where it is installed.",
                LyricSyncExit.ModelUnavailable).ConfigureAwait(false);
        }

        var core = _core(arguments);
        if (!core.IsModelInstalled)
        {
            return await Fail(
                $"No alignment model in '{core.ModelDirectory}'.", LyricSyncExit.ModelUnavailable)
                .ConfigureAwait(false);
        }

        var result = await core.AlignAsync(lyrics, arguments.AudioPath, new LyricSyncOptions
        {
            VocalStemPath = arguments.StemPath,
            ForceEnglishModel = arguments.Language switch
            {
                LyricSyncLanguage.English => true,
                LyricSyncLanguage.Romaji => false,
                _ => null
            },
            AllowEnsemble = !arguments.NoEnsemble
        }, status, ct).ConfigureAwait(false);

        foreach (var warning in result.Warnings) await _error.WriteLineAsync(warning).ConfigureAwait(false);

        if (!result.Ran) return await FailRun(result.Error!).ConfigureAwait(false);

        var syllables = SpanEnds(result.Syllables, arguments.SpanWordGap, result.Envelope);
        var outputBase = arguments.OutputBase ?? DefaultOutputBase(arguments.AudioPath);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputBase))!);

        var written = new List<string>();
        if (arguments.Format is LyricSyncFormat.RsXml or LyricSyncFormat.Both)
        {
            var path = outputBase + "_vocals.xml";
            RsVocalsSecondsWriter.WriteTo(path, syllables, arguments.Note);
            written.Add(path);
        }

        if (arguments.Format is LyricSyncFormat.Lrc or LyricSyncFormat.Both)
        {
            var path = outputBase + ".lrc";
            await File.WriteAllTextAsync(
                path, LrcSecondsWriter.Write(syllables), new UTF8Encoding(false), ct).ConfigureAwait(false);
            written.Add(path);
        }

        if (arguments.Json)
        {
            var path = outputBase + ".lyricsync.json";
            await File.WriteAllTextAsync(path, Json(result, syllables), new UTF8Encoding(false), ct)
                .ConfigureAwait(false);
            written.Add(path);
        }

        await Report(result, syllables, written).ConfigureAwait(false);
        return LyricSyncExit.Ok;
    }

    /// <summary>
    ///     Maps the seam's failure to an exit code.
    ///     <para>
    ///         Only the codes the README promises are produced. A decode failure is
    ///         its own code because it is the one the user can act on — install
    ///         ffmpeg, or convert to <c>.wav</c>.
    ///     </para>
    /// </summary>
    private async Task<int> FailRun(string error)
    {
        if (error.StartsWith(ILyricSyncCore.DecodeFailedPrefix, StringComparison.Ordinal))
        {
            return await Fail(
                error[ILyricSyncCore.DecodeFailedPrefix.Length..] +
                " (ffmpeg on PATH, or beside this executable in a 'tools' folder, decodes everything;" +
                " without it only .wav and .ogg work.)",
                LyricSyncExit.AudioDecodeFailed).ConfigureAwait(false);
        }

        return error switch
        {
            "no-model" => await Fail("The alignment model is not installed.", LyricSyncExit.ModelUnavailable)
                .ConfigureAwait(false),
            "no-audio" => await Fail("The audio file could not be opened.", LyricSyncExit.BadInput)
                .ConfigureAwait(false),
            "no-stem" => await Fail("The vocal stem could not be opened.", LyricSyncExit.BadInput)
                .ConfigureAwait(false),
            "no-lyrics" => await Fail("There are no lyrics to align.", LyricSyncExit.BadInput)
                .ConfigureAwait(false),
            _ => await Fail(error, LyricSyncExit.ModelUnavailable).ConfigureAwait(false)
        };
    }

    private async Task<int> Fail(string message, int code)
    {
        await _error.WriteLineAsync(message).ConfigureAwait(false);
        return code;
    }

    private async Task Report(
        LyricSyncResult result, IReadOnlyList<LyricSyncSyllable> syllables, IReadOnlyList<string> written)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture,
            $"{syllables.Count} syllables over {result.Sheet.LineCount} phrases, ");
        sb.Append(CultureInfo.InvariantCulture,
            $"{result.AudioSeconds:0.0} s of the {result.AudioSource} ")
            .Append('(').Append(Path.GetFileName(result.AudioPath)).Append(").\n");
        sb.Append(CultureInfo.InvariantCulture,
            $"Model {result.Model}, onset bias {result.OnsetBiasSeconds * 1000.0:+0;-0;0} ms.\n");
        sb.Append(CultureInfo.InvariantCulture,
            $"{result.LowConfidenceCount} low-confidence, {result.InterpolatedCount} interpolated.\n");
        foreach (var path in written) sb.Append("Wrote ").Append(path).Append('\n');

        await _out.WriteAsync(sb.ToString()).ConfigureAwait(false);

        if (result.LowConfidenceCount * 4 > syllables.Count && syllables.Count > 0)
        {
            await _error.WriteLineAsync(
                $"More than a quarter of the syllables ({result.LowConfidenceCount}) are low-confidence. " +
                "If this song has no separated vocal stem, making one and passing --stem is the single " +
                "biggest improvement available.").ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Turns the CTC end — which is a FLOOR, the last frame the model could
    ///     still hear the syllable — into a sung length.
    ///     <para>
    ///         The ends come from <see cref="LyricSyllableEnds" />, as the app's
    ///         (<c>LyricAutoSyncService.BuildPhrases</c>) do, so a stem's voice
    ///         decides them here too (<see cref="VocalEndRefiner" />). Two inputs
    ///         differ: the app spans to the next onset minus the chart's own
    ///         note-spacing minimum, a fraction of a MEASURE that needs a beat grid,
    ///         and with no grid the gap is a fixed <see cref="MinGapSeconds" />; and
    ///         the CLI never spans a phrase break. Within a word it always spans,
    ///         across a word boundary only when asked, never shortening what was
    ///         heard, never past <see cref="MaxSyllableSeconds" />.
    ///     </para>
    /// </summary>
    internal const double MinGapSeconds = 0.030;

    /// <inheritdoc cref="MinGapSeconds" />
    internal const double MaxSyllableSeconds = 4.0;

    /// <inheritdoc cref="MinGapSeconds" />
    internal static List<LyricSyncSyllable> SpanEnds(
        IReadOnlyList<LyricSyncSyllable> syllables, bool spanWordGap, VocalEnvelope? envelope = null)
    {
        ArgumentNullException.ThrowIfNull(syllables);

        var ends = LyricSyllableEnds.Compute(
            [.. syllables.Select(s => new LyricEndSyllable(
                s.StartSeconds, s.EndSeconds, s.LineIndex, s.ContinuesWord || (spanWordGap && !s.EndsLine)))],
            (_, nextStart) => nextStart - MinGapSeconds,
            MaxSyllableSeconds, envelope, VocalEndOptions.Default);
        return [.. syllables.Select((s, i) => s with { EndSeconds = ends[i] })];
    }

    /// <summary>Beside the audio, so a chart folder stays self-contained.</summary>
    internal static string DefaultOutputBase(string audioPath) =>
        Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(audioPath)) ?? ".",
            Path.GetFileNameWithoutExtension(audioPath));

    private static string Json(LyricSyncResult result, IReadOnlyList<LyricSyncSyllable> syllables) =>
        JsonSerializer.Serialize(new
        {
            audio = result.AudioPath,
            audioSource = result.AudioSource,
            audioSeconds = result.AudioSeconds,
            model = result.Model,
            ensemble = result.IsEnsemble,
            onsetBiasSeconds = result.OnsetBiasSeconds,
            lowConfidenceCount = result.LowConfidenceCount,
            interpolatedCount = result.InterpolatedCount,
            warnings = result.Warnings,
            syllables = syllables.Select(s => new
            {
                index = s.Index,
                text = s.Text,
                line = s.LineIndex,
                continuesWord = s.ContinuesWord,
                endsLine = s.EndsLine,
                start = s.StartSeconds,
                end = s.EndSeconds,
                confidence = s.Confidence,
                interpolated = s.IsInterpolated
            })
        }, new JsonSerializerOptions { WriteIndented = true });

    /// <summary>
    ///     Usage. The licence block is not decoration: the MMS-FA weights are
    ///     <b>non-commercial</b>, which a user has to be told before they chart for
    ///     money.
    /// </summary>
    internal const string HelpText = """
        dotcharter-lyricsync — time a song's lyrics, syllable by syllable.

        Usage:
          dotcharter-lyricsync <audio> <lyrics.txt> [options]

        The lyrics file is plain text: a NEWLINE ends a phrase, a SPACE ends a word,
        and a '-' splits a word into syllables. Rocksmith's own '-'/'+' suffixes are
        accepted too, so a line copied out of a chart pastes straight back in.

          Mi-zu ta-ma-ri ni a-shi wo tsuk-kon-de

        Options:
          --stem <vocals.wav>   Listen to an already-separated vocal stem. This is
                                worth a LOT (see --about); the tool never separates
                                one itself. Without it, a 'stems/vocals.*' file
                                beside the audio is found automatically.
          --out <base>          Output path without an extension. Default: beside
                                the audio, named after it.
          --format rsxml|lrc|both   Default rsxml.
          --model-dir <dir>     Models root to use instead of the default ones.
          --lang auto|en|romaji Override the language router. Default auto.
          --no-ensemble         Run one model instead of blending two (faster,
                                measurably less accurate).
          --span-word-gap       Hold the last syllable of a word until the next word
                                starts. Syllables inside a word always do this.
          --note <0-255>        The 'note' attribute in the vocals XML. Default 254
                                (no pitch); Editor on Fire writes 255.
          --json                Also dump the syllable table with confidences.
          --about               The measured accuracy figures.
          --help                This.

        Exit codes: 0 ok, 2 bad input, 3 model missing or failed, 4 audio decode failed.

        Output times are SECONDS of the audio file. DotCharter itself additionally
        snaps them to the chart's beat grid; this tool has no beat grid and does not.

        Models are ~226 MB (multilingual) and ~189 MB (English, optional) and are
        downloaded once into a 'models' folder beside this executable.
        %APPDATA%\DotCharter\models\ is searched too, for a hand-installed copy.

        Licences:
          Alignment weights (MMS-FA, facebook/mms-300m-1130-forced-aligner):
            CC-BY-NC 4.0 — NON-COMMERCIAL use only.
          English head (facebook/wav2vec2-large-960h-lv60-self): Apache 2.0.
          This tool: GPL-3.0-or-later.

        """;

    /// <summary>
    ///     The measured figures, copied from
    ///     <c>docs/agents/analysis/lyric-alignment.md</c>. <b>Every number here came
    ///     from a corpus run and names its corpus</b> — the two disagree by twelve
    ///     points and quoting one as "the" accuracy is how the optimistic figure
    ///     shipped for several releases.
    /// </summary>
    internal const string AboutText = """
        dotcharter-lyricsync — measured accuracy.

        Onsets within 100 ms of where a human charter put them, two models blended,
        listening to a separated VOCAL STEM:

          romaji    91.3%   (4 hand-synced arrangements, 2,036 syllables; p90 92 ms)
          English   79.2%   (40 official Rocksmith vocal arrangements, ~14,300
                             syllables; p90 292 ms)

        The two corpora disagree because of how they are MIXED, not because of the
        language: the romaji set is four charts with sparse, vocal-forward mixes.

        A vocal stem is worth more than any setting. Same English corpus, one model,
        within 100 ms:

          mix          66.2%    (p90 5,862 ms)
          vocal stem   75.5%    (p90 1,082 ms)

        Paired per song the stem wins 36, loses 2, ties 2. The median barely moves;
        what collapses is the tail, and the tail is what you notice.

        Blending the two heads rather than choosing one buys English 76.7% -> 79.2%
        and pulls its worst song from -38.9 pp to -5.9 pp; romaji 90.5% -> 91.3%,
        4 wins 0 losses. It costs a second pass over the audio.

        Some songs are simply unreadable: a shouted vocal can score 0% within 100 ms
        on every model. That is a limit of speech-trained alignment, not a setting
        you have got wrong.

        """;
}
