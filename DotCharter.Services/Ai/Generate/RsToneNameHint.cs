using System.Text;

namespace DotCharter.Services.Ai.Generate;

/// <summary>
/// Whether a tone's NAME says something about its sound. Charters
///     often name tones "Clean", "Distortion", "Lead", "Crunch", "Acoustic", "Clean_Chorus" or
///     "Dist2", and such a name is a useful hint to the model; many do not ("Tone A", "Base",
///     "Default", the song's own name), and such a name is noise. Only a descriptive one reaches
///     the prompt (<see cref="RsToneGeneratePrompt.NameHintLine" />), and only as a SOFT hint:
///     the measurements and the chat's instruction win where they disagree.
///     <para>
///         The rule is lexical and deliberately small: the name is split into words (separators,
///         camel case and letter/digit boundaries), every word that is generic (tone, base,
///         default, preset, patch, slot, sound, new, untitled, a slot letter, a number, one
///         character) or part of the song title or the arrangement name is dropped, and the name
///         is descriptive when a word is left.
///     </para>
/// </summary>
public static class RsToneNameHint
{
    private static readonly HashSet<string> Generic = new(StringComparer.Ordinal)
    {
        "tone", "tones", "base", "default", "preset", "patch", "slot", "sound", "new", "untitled", "rig", "setting"
    };

    /// <summary>
    ///     The name, trimmed, when it is descriptive; null when it is empty, generic or only the
    ///     song's or the arrangement's own name.
    /// </summary>
    /// <param name="toneName">The tone's name (a placed change's <c>ToneName</c> or a definition's name).</param>
    /// <param name="songTitle">The song title, whose words say nothing about the tone; may be null.</param>
    /// <param name="arrangementName">The arrangement's name ("Lead", "Rhythm"); likewise.</param>
    public static string? Of(string? toneName, string? songTitle, string? arrangementName)
    {
        if (string.IsNullOrWhiteSpace(toneName)) return null;
        var own = new HashSet<string>(Words(songTitle).Concat(Words(arrangementName)), StringComparer.Ordinal);
        var descriptive = Words(toneName).Any(w => w.Length > 1 && !IsNumber(w) && !Generic.Contains(w) && !own.Contains(w));
        return descriptive ? toneName.Trim() : null;
    }

    /// <summary>The lower-case words of <paramref name="text" />: split on anything that is not a
    /// letter or digit, on a lower→upper case change and on a letter↔digit change.</summary>
    internal static IEnumerable<string> Words(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;
        var word = new StringBuilder();
        var previous = '\0';
        foreach (var c in text)
        {
            var split = !char.IsLetterOrDigit(c) ||
                        (word.Length > 0 && char.IsUpper(c) && char.IsLower(previous)) ||
                        (word.Length > 0 && char.IsDigit(c) != char.IsDigit(previous));
            if (split && word.Length > 0)
            {
                yield return word.ToString().ToLowerInvariant();
                word.Clear();
            }

            if (char.IsLetterOrDigit(c)) word.Append(c);
            previous = c;
        }

        if (word.Length > 0) yield return word.ToString().ToLowerInvariant();
    }

    private static bool IsNumber(string word) => word.All(char.IsDigit);
}
