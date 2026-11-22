namespace DotCharter.Tone.Infrastructure.Storage;

/// <summary>
///     Turns a song/arrangement title into something a file picker can be
///     pre-filled with.
/// </summary>
/// <remarks>
///     The same three-line loop was inlined at nine call sites across the UI and
///     the service layer, three of them character-for-character identical inside
///     the export commands alone. One of them is enough.
/// </remarks>
public static class SafeFileName
{
    private static readonly char[] Invalid = Path.GetInvalidFileNameChars();

    /// <summary>
    ///     Replaces every character illegal in a file name with '_' and trims
    ///     surrounding whitespace. Returns an empty string for null/blank input
    ///     — callers pick their own fallback stem.
    /// </summary>
    public static string Make(string? fileName)
        => string.IsNullOrWhiteSpace(fileName) ? string.Empty : ReplaceInvalid(fileName).Trim();

    /// <summary>
    ///     The invalid-character policy alone: every character illegal in a file name
    ///     becomes '_', nothing is trimmed and there is no fallback. For a caller whose
    ///     names already exist on disk in exactly that spelling.
    /// </summary>
    public static string ReplaceInvalid(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        var buffer = fileName.Length <= 256 ? stackalloc char[fileName.Length] : new char[fileName.Length];
        for (var i = 0; i < fileName.Length; i++)
        {
            var c = fileName[i];
            buffer[i] = Array.IndexOf(Invalid, c) >= 0 ? '_' : c;
        }

        return new string(buffer);
    }
}
