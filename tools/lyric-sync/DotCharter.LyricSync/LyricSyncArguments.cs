using DotCharter.Services.Export.Lyrics;

namespace DotCharter.LyricSync;

/// <summary>Which files the run writes.</summary>
public enum LyricSyncFormat
{
    /// <summary>Rocksmith 2014 <c>&lt;vocals&gt;</c> XML only.</summary>
    RsXml,

    /// <summary>Enhanced (word-timed) LRC only.</summary>
    Lrc,

    /// <summary>Both.</summary>
    Both,
}

/// <summary>How the language router is driven.</summary>
public enum LyricSyncLanguage
{
    /// <summary>Let <c>LyricScriptDetector</c> decide — what the app does.</summary>
    Auto,

    /// <summary>Force the English head when it is installed.</summary>
    English,

    /// <summary>Force the multilingual (MMS-FA) head.</summary>
    Romaji,
}

/// <summary>
///     One parsed command line. A record so a test can build one directly and
///     drive the run without going near <see cref="Environment.GetCommandLineArgs" />.
/// </summary>
/// <param name="AudioPath">The song.</param>
/// <param name="LyricsPath">The sheet: newline = phrase, space = word, <c>-</c> = syllable.</param>
public sealed record LyricSyncArguments(string AudioPath, string LyricsPath)
{
    /// <summary>An already-separated vocal stem to listen to; null asks for one beside the audio.</summary>
    public string? StemPath { get; init; }

    /// <summary>Output path WITHOUT an extension. Null derives it from the audio file.</summary>
    public string? OutputBase { get; init; }

    /// <summary>Which documents to write.</summary>
    public LyricSyncFormat Format { get; init; } = LyricSyncFormat.RsXml;

    /// <summary>Models root to search INSTEAD of the exe-relative and per-user ones.</summary>
    public string? ModelDirectory { get; init; }

    /// <summary>Language routing.</summary>
    public LyricSyncLanguage Language { get; init; } = LyricSyncLanguage.Auto;

    /// <summary>Run one head only, even when both are installed.</summary>
    public bool NoEnsemble { get; init; }

    /// <summary>
    ///     Hold the last syllable of a WORD until the next word starts. Syllables
    ///     INSIDE a word always span; this is the word-to-word boundary, and it is
    ///     the flag DotCharter itself defaults ON.
    /// </summary>
    public bool SpanWordGap { get; init; }

    /// <summary>Also dump the raw syllable table, with confidences, as JSON.</summary>
    public bool Json { get; init; }

    /// <summary>The <c>note</c> attribute every syllable gets in the vocals XML.</summary>
    public int Note { get; init; } = RsVocalsSecondsWriter.PitchlessNote;

    /// <summary>Print usage and exit.</summary>
    public bool ShowHelp { get; init; }

    /// <summary>Print the measured accuracy figures and exit.</summary>
    public bool ShowAbout { get; init; }
}

/// <summary>
///     What <see cref="LyricSyncArgumentParser.Parse" /> produced: arguments, or a
///     message and the exit code to leave with.
/// </summary>
/// <param name="Arguments">Null when <paramref name="Error" /> is set.</param>
/// <param name="Error">Null on success.</param>
public sealed record LyricSyncParse(LyricSyncArguments? Arguments, string? Error)
{
    /// <summary>True when a run should happen.</summary>
    public bool Ok => Arguments != null;
}

/// <summary>
///     Turns <c>string[]</c> into <see cref="LyricSyncArguments" />.
///     <para>
///         <b>Separate from the run so both halves are testable.</b> Argument
///         handling is where a CLI's quiet bugs live — a flag that silently
///         consumes the next positional, an unknown option accepted as a file name
///         — and neither is visible from a test that only drives a whole process.
///         Pinned by <c>LyricSyncCommandTests</c>.
///     </para>
///     <para>
///         <b>Nothing here touches the file system.</b> Existence is the run's
///         business; a parser that stats files cannot be tested without them.
///     </para>
/// </summary>
public static class LyricSyncArgumentParser
{
    /// <summary>Parses, or explains what is wrong.</summary>
    public static LyricSyncParse Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var positional = new List<string>();
        string? stem = null, outBase = null, modelDir = null;
        var format = LyricSyncFormat.RsXml;
        var language = LyricSyncLanguage.Auto;
        bool noEnsemble = false, spanWordGap = false, json = false, help = false, about = false;
        var note = RsVocalsSecondsWriter.PitchlessNote;

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--help" or "-h" or "-?" or "/?":
                    help = true;
                    break;
                case "--about":
                    about = true;
                    break;
                case "--no-ensemble":
                    noEnsemble = true;
                    break;
                case "--span-word-gap":
                    spanWordGap = true;
                    break;
                case "--json":
                    json = true;
                    break;
                case "--stem":
                    if (!TryValue(args, ref i, arg, out stem)) return Missing(arg);
                    break;
                case "--out":
                    if (!TryValue(args, ref i, arg, out outBase)) return Missing(arg);
                    break;
                case "--model-dir":
                    if (!TryValue(args, ref i, arg, out modelDir)) return Missing(arg);
                    break;
                case "--format":
                {
                    if (!TryValue(args, ref i, arg, out var value)) return Missing(arg);
                    format = value!.ToLowerInvariant() switch
                    {
                        "rsxml" => LyricSyncFormat.RsXml,
                        "lrc" => LyricSyncFormat.Lrc,
                        "both" => LyricSyncFormat.Both,
                        _ => (LyricSyncFormat)(-1)
                    };
                    if ((int)format < 0)
                        return new LyricSyncParse(null, $"--format must be rsxml, lrc or both (got '{value}').");
                    break;
                }
                case "--lang":
                {
                    if (!TryValue(args, ref i, arg, out var value)) return Missing(arg);
                    language = value!.ToLowerInvariant() switch
                    {
                        "auto" => LyricSyncLanguage.Auto,
                        "en" or "english" => LyricSyncLanguage.English,
                        "romaji" or "ja" or "multilingual" => LyricSyncLanguage.Romaji,
                        _ => (LyricSyncLanguage)(-1)
                    };
                    if ((int)language < 0)
                        return new LyricSyncParse(null, $"--lang must be auto, en or romaji (got '{value}').");
                    break;
                }
                case "--note":
                {
                    if (!TryValue(args, ref i, arg, out var value)) return Missing(arg);
                    if (!int.TryParse(value, out note) || note is < 0 or > 255)
                        return new LyricSyncParse(null, $"--note must be 0..255 (got '{value}').");
                    break;
                }
                default:
                    // An unknown option must NEVER be taken for a file name. It is
                    // almost always a typo, and accepting it silently is how a run
                    // ends up aligning against the wrong thing.
                    if (arg.StartsWith('-'))
                        return new LyricSyncParse(null, $"Unknown option '{arg}'. Try --help.");
                    positional.Add(arg);
                    break;
            }
        }

        if (help || about)
        {
            return new LyricSyncParse(
                new LyricSyncArguments("", "") { ShowHelp = help, ShowAbout = about }, null);
        }

        if (positional.Count < 2)
        {
            return new LyricSyncParse(null,
                "Usage: dotcharter-lyricsync <audio> <lyrics.txt> [options]. Try --help.");
        }

        if (positional.Count > 2)
        {
            return new LyricSyncParse(null,
                $"Expected 2 file arguments, got {positional.Count}: {string.Join(", ", positional)}.");
        }

        return new LyricSyncParse(
            new LyricSyncArguments(positional[0], positional[1])
            {
                StemPath = stem,
                OutputBase = outBase,
                Format = format,
                ModelDirectory = modelDir,
                Language = language,
                NoEnsemble = noEnsemble,
                SpanWordGap = spanWordGap,
                Json = json,
                Note = note
            },
            null);
    }

    private static bool TryValue(IReadOnlyList<string> args, ref int i, string option, out string? value)
    {
        value = null;
        if (i + 1 >= args.Count) return false;

        // "--out --json" is a forgotten value, not an output called "--json".
        var candidate = args[i + 1];
        if (candidate.StartsWith("--", StringComparison.Ordinal)) return false;

        value = candidate;
        i++;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static LyricSyncParse Missing(string option) =>
        new(null, $"{option} needs a value.");
}
