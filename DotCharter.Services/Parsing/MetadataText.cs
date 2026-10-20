using System.Text;

namespace DotCharter.Services.Parsing;

/// <summary>
///     Cleans one metadata STRING that arrived from outside — an audio file's
///     tags, a package manifest, a binary chart file — before it becomes part of
///     the project.
/// </summary>
/// <remarks>
///     <para>
///     <b>The .rscp is XML, and XML 1.0 has no way to spell a control
///     character.</b> <c>RscpChartSerializer.Save</c> writes through an
///     <c>XmlWriter</c> whose <c>CheckCharacters</c> is the default <c>true</c>,
///     so a single NUL — which ID3 text frames are NUL-terminated and routinely
///     carry one trailing copy of, and which ffprobe hands straight back inside
///     its JSON — makes the writer THROW. The import succeeds, the chart opens,
///     and then every save of that project fails with an
///     <c>ArgumentException</c> about a hexadecimal value, naming no field.
///     </para>
///     <para>
///     A leading byte-order mark is the same class of invisible passenger and
///     does not even throw: U+FEFF is a format character, not whitespace, so
///     <c>Trim()</c> keeps it — and the artist then sorts, groups and
///     de-duplicates as a name nobody typed.
///     </para>
///     <para>
///     What survives: everything printable, plus the tab / newline / carriage
///     return XML does allow. Whitespace is trimmed at the ends, exactly as the
///     callers already did.
///     </para>
/// </remarks>
public static class MetadataText
{
    /// <summary>
    ///     <paramref name="value" /> with XML-illegal control characters and
    ///     zero-width formatting characters removed and the ends trimmed;
    ///     <c>""</c> for null, blank or all-junk input.
    /// </summary>
    public static string Clean(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";

        StringBuilder? cleaned = null;
        for (var i = 0; i < value.Length; i++)
        {
            if (IsKeepable(value[i]))
            {
                cleaned?.Append(value[i]);
                continue;
            }

            // First offender: copy what we have kept so far, then skip it.
            cleaned ??= new StringBuilder(value.Length).Append(value, 0, i);
        }

        return (cleaned?.ToString() ?? value).Trim();
    }

    /// <summary>Code points spelled numerically rather than as character
    /// literals, so the rule survives a tool that re-encodes this file.</summary>
    private const int Delete = 0x007F;
    private const int C1BlockEnd = 0x009F;
    private const int ZeroWidthSpace = 0x200B;
    private const int RightToLeftMark = 0x200F;
    private const int ByteOrderMark = 0xFEFF;

    private static bool IsKeepable(char c)
    {
        // The three control characters XML 1.0 permits.
        if (c is '\t' or '\n' or '\r') return true;

        // Everything else below 0x20, plus DEL and the C1 block, is illegal in
        // XML 1.0 or unprintable.
        if (c is < ' ' or >= (char)Delete and <= (char)C1BlockEnd) return false;

        // Zero-width and bidi formatting characters (U+200B..U+200F): invisible,
        // and they make a name that LOOKS identical compare, sort and group as a
        // different one. U+FEFF is the byte-order mark riding inside a UTF-8
        // text frame.
        if (c is >= (char)ZeroWidthSpace and <= (char)RightToLeftMark) return false;
        return c != ByteOrderMark;
    }
}
