using System.Text;

namespace DotCharter.Services.Parsing.GuitarPro;

/// <summary>
///     Normalizes a tab file's track name for display: interior whitespace runs
///     collapse to one space and the ends are trimmed. Real GP files carry the
///     double space IN their bytes — the Ave Mujica reference GP5's second track
///     is literally <c>"Lead (6  String Drop D)"</c> on the wire (an authoring
///     tool's template with an empty middle term), and that name flowed verbatim
///     onto <c>Arrangement.Name</c> and into the export dialog. Applied at every
///     track-name READ (both GP importers' track lists, the gpif arrangement
///     parser, the Songsterr converter) so the import dialog's name-based
///     track↔arrangement matching always compares normalized against normalized.
/// </summary>
public static class GpTrackNames
{
    /// <summary>Collapses whitespace runs (spaces, tabs, anything
    /// <see cref="char.IsWhiteSpace(char)"/>) to a single space and trims.
    /// Null and whitespace-only collapse to the empty string.</summary>
    public static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";

        var sb = new StringBuilder(name.Length);
        var pendingSpace = false;
        foreach (var c in name)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }
}
