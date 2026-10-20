using DotCharter.Localization;

namespace DotCharter.Services.Ai.Match;

/// <summary>
/// What a <see cref="ToneStems" /> id is called on screen: "guitar" is the stem mixer's
///     own <c>Stem_Guitar</c> (Guitar / Gitarre / ギター). The ids are the separator's file names and
///     the prompt's words, never a caption; a stem this build has no word for prints its id.
/// </summary>
public static class ToneStemNames
{
    /// <summary>The resource key of <paramref name="stem" />'s name, or null when there is none.</summary>
    public static string? KeyOf(string stem) => stem switch
    {
        ToneStems.Guitar => "Stem_Guitar",
        ToneStems.Bass => "Stem_Bass",
        ToneStems.Piano => "Stem_Piano",
        ToneStems.Other => "Stem_Other",
        _ => null
    };

    /// <summary><paramref name="stem" />'s localized name; the id itself for an unknown stem.</summary>
    public static string Display(string stem, ILocalizationService loc)
    {
        ArgumentNullException.ThrowIfNull(stem);
        ArgumentNullException.ThrowIfNull(loc);
        return KeyOf(stem) is { } key ? loc[key] : stem;
    }
}
