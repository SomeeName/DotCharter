namespace DotCharter.Services.Editing.ChordShapes;

/// <summary>
/// Pure conversion between the finger-id integer convention used by the
/// chord template model and the single-character text shown in the
/// chord-template editor's per-string finger box.
///
/// Convention:
/// <list type="bullet">
/// <item>0 → "T" (thumb)</item>
/// <item>1..4 → "1".."4"</item>
/// <item>anything else → empty / unset</item>
/// </list>
///
/// This codec centralises what would otherwise be inline in four places inside
/// the chord panel; it pins the
/// "T = thumb" mapping (which has subtly broken before) in one place with
/// tests.
/// </summary>
public static class FingerTextCodec
{
    public const int InvalidFinger = -1;
    public const int ThumbFinger = 0;

    /// <summary>
    /// Parse what the user typed in the finger text box. Returns
    /// <see cref="InvalidFinger"/> when the input doesn't map to a valid
    /// finger (caller treats that as "remove the entry from the dict").
    /// </summary>
    public static int Parse(string? text)
    {
        if (string.IsNullOrEmpty(text)) return InvalidFinger;
        return text.ToUpperInvariant() switch
        {
            "T" => ThumbFinger,
            "1" => 1,
            "2" => 2,
            "3" => 3,
            "4" => 4,
            _ => InvalidFinger
        };
    }

    /// <summary>
    /// Render a stored finger id back into the editor's text format.
    /// Returns an empty string for ids outside 0..4.
    /// </summary>
    public static string Format(int fingerId) => fingerId switch
    {
        ThumbFinger => "T",
        >= 1 and <= 4 => fingerId.ToString(),
        _ => string.Empty
    };
}
