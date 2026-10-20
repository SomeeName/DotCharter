namespace DotCharter.Tone.Audio.Dsp.Tones;

/// <summary>Where a generated tone's evidence came from, and how much to trust it.</summary>
/// <param name="Path">Absolute path to the audio to analyse.</param>
/// <param name="StemName">The stem's name, or empty when this is the full mix.</param>
/// <param name="IsIsolatedGuitar">
///     True only when the audio is a guitar-ish stem. False means the analysis is
///     reading drums and vocals too, and the result must be presented as weaker.
/// </param>
public readonly record struct ToneAudioSourcePick(string Path, string StemName, bool IsIsolatedGuitar);

/// <summary>
///     Picks the audio a tone should be generated FROM.
///     <para>
///         This matters more than the mapping does. Every measurement the tone is
///         built on — how squashed the dynamics are, how flat the spectrum is,
///         where the energy sits — is wrecked by drums and vocals sharing the
///         signal. A cymbal is broadband noise: analyse a full mix and it reads as
///         distortion, and the generator confidently returns a high-gain amp for a
///         clean song.
///     </para>
///     <para>
///         So the order is: an isolated guitar stem, else the stem a 4-source
///         separation folds guitar into, else the mix — and the mix case is
///         flagged rather than silently treated as equivalent.
///     </para>
/// </summary>
public static class ToneAudioSource
{
    /// <summary>
    ///     Stems that can carry guitar, best first.
    ///     <para>
    ///         htdemucs' 6-source model emits a real <c>guitar</c> stem. The
    ///         4-source model does not, and everything that is not drums, bass or
    ///         vocals lands in <c>other</c> — which is mostly guitar in a rock mix
    ///         and is the best available evidence when no guitar stem exists.
    ///         <c>piano</c> is deliberately NOT here: it is a 6-source sibling, so
    ///         if it exists then <c>guitar</c> does too.
    ///     </para>
    /// </summary>
    public static readonly IReadOnlyList<string> GuitarishStems = ["guitar", "other"];

    /// <summary>Stems that must never be analysed for a guitar tone, whatever
    /// else is missing.</summary>
    public static readonly IReadOnlyList<string> NeverStems = ["drums", "bass", "vocals"];

    /// <summary>
    ///     Chooses the source. <paramref name="stemPaths" /> is stem name →
    ///     absolute path; <paramref name="songPath" /> is the full mix.
    ///     Returns <c>null</c> when there is nothing to analyse at all.
    /// </summary>
    public static ToneAudioSourcePick? Pick(
        IReadOnlyDictionary<string, string>? stemPaths, string? songPath)
    {
        if (stemPaths is not null)
        {
            foreach (var name in GuitarishStems)
            {
                if (stemPaths.TryGetValue(name, out var path) && !string.IsNullOrWhiteSpace(path))
                    return new ToneAudioSourcePick(path, name, IsIsolatedGuitar: true);
            }
        }

        if (!string.IsNullOrWhiteSpace(songPath))
            return new ToneAudioSourcePick(songPath, "", IsIsolatedGuitar: false);

        return null;
    }

    /// <summary>
    ///     Whether a stem may be used as guitar evidence. Exposed so a caller
    ///     building its own dictionary cannot accidentally hand us the drums.
    /// </summary>
    public static bool IsGuitarish(string? stemName) =>
        !string.IsNullOrWhiteSpace(stemName) &&
        GuitarishStems.Contains(stemName.Trim(), StringComparer.OrdinalIgnoreCase);
}
