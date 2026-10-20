namespace DotCharter.Core.Utilities;

/// <summary>
///     The section names Rocksmith 2014 actually understands — the complete key
///     set of the game's <c>getSectionUIName</c> lookup, which is what turns a
///     section into a labelled region in-game and in the Riff Repeater.
///     <para>
///         <b>A name outside this list is not a section, it is dead text.</b>
///         Rocksmith has no fallback: an unrecognised key renders as nothing, so
///         a chart authored with invented names ships with a structure the game
///         cannot show. This is easy to do by accident — typing
///         <c>Section='COUNT'</c> by hand, which is a legitimate
///         RS <i>phrase</i> name (the count-in) but not a section at all.
///     </para>
///     <para>
///         PHRASES are deliberately not constrained the same way: a phrase name
///         is the charter's own label for a region (RS uses it for dynamic
///         difficulty, not for display), so "COUNT", "riff1" and "verse2" are all
///         fine there. Only the SECTION field is a closed vocabulary.
///     </para>
///     <para>
///         Stored lower-case because that is the form the exporters write and
///         the importers compare; <see cref="DisplayName" /> is the label to put
///         in front of a human.
///     </para>
/// </summary>
public static class RsSectionCatalog
{
    /// <summary>
    ///     Every valid section name, in Editor on Fire's own order — the order its
    ///     section picker lists them, which is roughly the order a song is built.
    ///     Ported verbatim from <c>eof_rs_predefined_sections[]</c>
    ///     (<c>EOF_SRC/rs.c:31</c>), including the display names below.
    /// </summary>
    public static readonly IReadOnlyList<string> Names =
    [
        "noguitar", "intro", "preverse", "verse", "postvs", "modverse",
        "prechorus", "chorus", "postchorus", "modchorus", "hook",
        "prebrdg", "bridge", "postbrdg", "solo", "outro",
        "ambient", "breakdown", "interlude", "transition", "riff",
        "fadein", "fadeout", "buildup", "variation", "head",
        "modbridge", "melody", "vamp", "silence", "tapping"
    ];

    private static readonly HashSet<string> Lookup = new(Names, StringComparer.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="name" /> (trimmed, case-insensitive)
    /// is a section Rocksmith will render.</summary>
    public static bool IsValid(string? name)
        => !string.IsNullOrWhiteSpace(name) && Lookup.Contains(name.Trim());

    /// <summary>The canonical stored spelling, or null when the name is not a
    /// section at all.</summary>
    public static string? Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var trimmed = name.Trim();
        return Lookup.Contains(trimmed) ? trimmed.ToLowerInvariant() : null;
    }

    /// <summary>
    ///     A human label for a stored name — "prechorus" → "Pre Chorus". These
    ///     are EOF's own display strings, character for character, because that is
    ///     the vocabulary a charter coming from EOF already reads. Unknown names
    ///     are returned as-is so a chart imported from elsewhere still shows what
    ///     it actually contains rather than a blank.
    /// </summary>
    public static string DisplayName(string name) => name switch
    {
        "noguitar" => "No Guitar",
        "preverse" => "Pre Verse",
        "postvs" => "Post Verse",
        "modverse" => "Modulated Verse",
        "prechorus" => "Pre Chorus",
        "postchorus" => "Post Chorus",
        "modchorus" => "Modulated Chorus",
        "prebrdg" => "Pre Bridge",
        "postbrdg" => "Post Bridge",
        "modbridge" => "Modulated Bridge",
        "fadein" => "Fade In",
        "fadeout" => "Fade Out",
        _ => name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name[1..]
    };
}
