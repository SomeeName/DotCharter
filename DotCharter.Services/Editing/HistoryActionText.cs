using System.Text;

namespace DotCharter.Services.Editing;

/// <summary>
///     Display text for a history action's description — the Edit ▸ History
///     dialog and the toolbar undo/redo hover previews both show it.
/// </summary>
public static class HistoryActionText
{
    /// <summary>Prefix of a description that IS a resx key (a labelled composite).</summary>
    public const string KeyToken = "key:";

    /// <summary>Prefix of a description that is a merge verb ("verb:fret").</summary>
    public const string VerbToken = "verb:";

    /// <summary>
    /// The resx key a history description resolves to: the key itself
    ///     for <see cref="KeyToken" />, <c>History_Verb_&lt;Verb&gt;</c> for a merge
    ///     verb ("anchor-fret" → <c>History_Verb_AnchorFret</c>), and
    ///     <c>History_&lt;Type&gt;</c> for an action type ("MutateNotesAction" →
    ///     <c>History_MutateNotes</c>).
    /// </summary>
    public static string LabelKeyFor(string description)
    {
        if (description.StartsWith(KeyToken, StringComparison.Ordinal)) return description[KeyToken.Length..];
        if (description.StartsWith(VerbToken, StringComparison.Ordinal))
        {
            return "History_Verb_" + string.Concat(description[VerbToken.Length..].Split('-', StringSplitOptions.RemoveEmptyEntries)
                .Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
        }

        return "History_" + StripTypeName(description);
    }

    /// <summary>
    /// What the Undo / Redo lists and Edit ▸ History show for a step:
    ///     the localized label behind <see cref="LabelKeyFor" />, resolved NOW (so a
    ///     language switch relabels existing steps), or — for a step whose key has no
    ///     string — the old humanized type name or verb.
    /// </summary>
    public static string Describe(string description, Func<string, string> localize)
    {
        ArgumentNullException.ThrowIfNull(localize);
        if (string.IsNullOrEmpty(description)) return string.Empty;
        var key = LabelKeyFor(description);
        var text = localize(key);
        if (text.Length > 0 && !string.Equals(text, key, StringComparison.Ordinal)) return text;
        if (description.StartsWith(KeyToken, StringComparison.Ordinal)) return Humanize(key);
        if (description.StartsWith(VerbToken, StringComparison.Ordinal))
        {
            var verb = description[VerbToken.Length..].Replace('-', ' ');
            return verb.Length == 0 ? verb : char.ToUpperInvariant(verb[0]) + verb[1..];
        }

        return Humanize(description);
    }

    private static string StripTypeName(string typeName)
    {
        var backtick = typeName.IndexOf('`');
        if (backtick >= 0) typeName = typeName[..backtick];
        return typeName.EndsWith("Action", StringComparison.Ordinal) ? typeName[..^"Action".Length] : typeName;
    }

    /// <summary>"MutateBeatsAction" → "Mutate beats": sentence case, and an acronym ("FHP") keeps its capitals.</summary>
    public static string Humanize(string typeName)
    {
        var backtick = typeName.IndexOf('`');
        if (backtick >= 0) typeName = typeName[..backtick]; // generic arity suffix
        var name = typeName.EndsWith("Action", StringComparison.Ordinal)
            ? typeName[..^"Action".Length]
            : typeName;
        var sb = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (i > 0 && char.IsUpper(c) && !char.IsUpper(name[i - 1]))
            {
                sb.Append(' ');
                var startsAWord = i + 1 >= name.Length || !char.IsUpper(name[i + 1]);
                if (startsAWord) c = char.ToLowerInvariant(c);
            }

            sb.Append(c);
        }

        return sb.ToString();
    }
}
