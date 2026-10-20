using System.Text;

namespace DotCharter.Services.Ai.Generate;

/// <summary>
/// The charter's own words for Tools ▸ Generate tones with AI…: "the verse is a
///     cranked Marshall, the solo adds a wah". They ride in <see cref="ToneGenerateRequest.Context" />
///     beside the project's metadata and reach every window's prompt under the existing
///     CONTEXT header. Pure, so the cap and the cleaning are asserted without a provider.
/// </summary>
public static class AiToneContextText
{
    /// <summary>The longest text kept, in characters. Every window's prompt carries it, so an
    /// unbounded box would multiply a pasted essay by the number of tone changes.</summary>
    public const int MaxLength = 500;

    /// <summary>The label the text carries inside the context line.</summary>
    public const string Label = "the charter's notes: ";

    /// <summary>
    ///     Cleans what was typed: whitespace runs (line breaks included) collapse to one space,
    ///     the ends are trimmed, and the result is cut to <see cref="MaxLength" /> characters
    ///     (never inside a surrogate pair). Null when nothing is left.
    /// </summary>
    /// <param name="text">What the charter typed; may be null.</param>
    public static string? Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var sb = new StringBuilder(Math.Min(text.Length, MaxLength));
        var pendingSpace = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }

            if (pendingSpace) sb.Append(' ');
            pendingSpace = false;
            sb.Append(c);
        }

        if (sb.Length > MaxLength)
        {
            var cut = MaxLength;
            if (char.IsHighSurrogate(sb[cut - 1])) cut--;
            sb.Length = cut;
        }

        var cleaned = sb.ToString().TrimEnd();
        return cleaned.Length == 0 ? null : cleaned;
    }
}
