namespace DotCharter.Services.Audio.StemSeparation;

/// <summary>
///     The on-disk name of a stem model. It is joined onto the models folder to decide
///     where a download is written and what is loaded or deleted, and a custom model's
///     name comes from <c>custom_models.json</c>, a file that can be handed to the user.
///     So it must be a BARE file name: no directory part, no drive or stream colon, no
///     <c>..</c>, nothing Windows would treat as a device.
/// </summary>
public static class StemModelFileName
{
    /// <summary>What a name that cannot be used collapses to when it must still be joined.</summary>
    private const string Placeholder = "invalid-model-file";

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>True when <paramref name="name" /> is one plain file name that stays inside whatever folder it is joined to.</summary>
    public static bool IsBare(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (name is "." or "..") return false;
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
        if (name.IndexOfAny(['\\', '/', ':']) >= 0) return false;
        // Windows drops a trailing dot or space, so "x." names "x" and ".. " names "..".
        if (name[^1] is '.' or ' ') return false;

        var dot = name.IndexOf('.');
        return !ReservedDeviceNames.Contains(dot < 0 ? name : name[..dot]);
    }

    /// <summary>
    ///     <paramref name="name" /> when it is bare, else its last path segment when that is,
    ///     else a fixed placeholder - always something that joins INSIDE the models folder.
    ///     The belt to <see cref="IsBare" />'s braces: the store drops such a descriptor on
    ///     load, and anything that reaches a path join anyway cannot leave the folder.
    /// </summary>
    public static string Contained(string? name)
    {
        if (IsBare(name)) return name!;
        var text = name ?? string.Empty;
        var segment = text[(text.LastIndexOfAny(['\\', '/']) + 1)..];
        return IsBare(segment) ? segment : Placeholder;
    }
}
