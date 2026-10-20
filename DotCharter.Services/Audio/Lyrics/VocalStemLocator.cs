namespace DotCharter.Services.Audio.Lyrics;

/// <summary>
///     Finds a vocal stem that stem separation has ALREADY produced for a song,
///     and never produces one itself.
///     <para>
///         <b>That restraint is the whole design.</b> Aligning against an isolated
///         voice is decisively better than aligning against the mix (see
///         <see cref="LyricAutoSyncService" /> for the measurement), but separating
///         costs seconds-to-minutes and holds the GPU, and the user asked for
///         lyrics to be timed — not for a separation to start behind their back.
///         So the win is taken for free wherever the stems already exist, and the
///         mix is used everywhere else.
///     </para>
///     <para>
///         Stems live in a <c>stems/</c> folder beside the song audio — written
///         there both by a fresh separation and by the never-regenerate
///         <c>StemCacheCoordinator</c> expanding a cached archive, so a song
///         separated in an earlier session is found after a restart.
///     </para>
/// </summary>
public static class VocalStemLocator
{
    /// <summary>The folder separation writes into, beside the song audio.</summary>
    public const string StemsFolderName = "stems";

    /// <summary>
    ///     Preferred first. Lossless wins because it is what the accuracy A/B was
    ///     measured on; the compressed forms are accepted because the durable stem
    ///     cache stores Opus, so refusing them would mean the feature only ever
    ///     worked in the session that ran the separation.
    /// </summary>
    private static readonly string[] AudioExtensions =
        [".wav", ".flac", ".opus", ".ogg", ".mp3", ".m4a", ".aac"];

    /// <summary>
    ///     Picks the vocal stem out of a set of candidate files, or <c>null</c>.
    ///     Pure — the file system is the caller's problem, which is what makes the
    ///     naming rules below testable.
    /// </summary>
    public static string? PickVocalStem(IEnumerable<string> candidateFiles)
    {
        ArgumentNullException.ThrowIfNull(candidateFiles);

        string? best = null;
        var bestRank = int.MaxValue;

        foreach (var file in candidateFiles)
        {
            if (string.IsNullOrWhiteSpace(file)) continue;

            var extension = Path.GetExtension(file);
            var rank = Array.FindIndex(AudioExtensions,
                e => string.Equals(e, extension, StringComparison.OrdinalIgnoreCase));
            if (rank < 0) continue;

            if (!IsVocalStemName(Path.GetFileNameWithoutExtension(file))) continue;

            if (rank >= bestRank) continue;
            best = file;
            bestRank = rank;
        }

        return best;
    }

    /// <summary>
    ///     Whether a stem's bare file name names the VOICE.
    ///     <para>
    ///         <b>This is deliberately an exact-name test and not a
    ///         <c>Contains("vocal")</c>.</b> Every "split the vocals out" tool also
    ///         emits the complement, and it is conventionally called
    ///         <c>no_vocals</c> — which contains the substring. Matching loosely
    ///         would hand the aligner the one stem with the singing removed, and
    ///         the failure would not look like a bug: alignment would still
    ///         succeed, still return every syllable, and simply place them badly.
    ///     </para>
    /// </summary>
    internal static bool IsVocalStemName(string? bareName)
    {
        if (string.IsNullOrWhiteSpace(bareName)) return false;

        var name = bareName.Trim().ToLowerInvariant();
        if (name is "vocals" or "vocal") return true;

        // Some pipelines prefix the song or the model ("song_vocals", "htdemucs-vocals").
        // The complement never spells itself that way, but guard anyway — being
        // wrong here is silent, so the check is cheap insurance.
        if (name.Contains("no_vocal", StringComparison.Ordinal) ||
            name.Contains("no-vocal", StringComparison.Ordinal) ||
            name.Contains("novocal", StringComparison.Ordinal) ||
            name.Contains("instrumental", StringComparison.Ordinal))
        {
            return false;
        }

        return name.EndsWith("_vocals", StringComparison.Ordinal)
               || name.EndsWith("-vocals", StringComparison.Ordinal)
               || name.EndsWith("_vocal", StringComparison.Ordinal)
               || name.EndsWith("-vocal", StringComparison.Ordinal);
    }

    /// <summary>
    ///     Resolves the vocal stem sitting beside <paramref name="mixAudioPath" />,
    ///     or <c>null</c> when there is none. Never throws — a missing folder, an
    ///     ACL or a racing delete is a plain "no stem", and the caller falls back
    ///     to the mix.
    /// </summary>
    public static string? TryLocate(string? mixAudioPath)
    {
        if (string.IsNullOrWhiteSpace(mixAudioPath)) return null;

        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(mixAudioPath));
            if (string.IsNullOrEmpty(directory)) return null;

            var stemsDirectory = Path.Combine(directory, StemsFolderName);
            if (!Directory.Exists(stemsDirectory)) return null;

            var picked = PickVocalStem(
                Directory.EnumerateFiles(stemsDirectory, "*", SearchOption.TopDirectoryOnly));

            return picked != null && File.Exists(picked) ? picked : null;
        }
        catch
        {
            return null;
        }
    }
}
