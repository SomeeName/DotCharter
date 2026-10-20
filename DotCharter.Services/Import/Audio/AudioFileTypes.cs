namespace DotCharter.Services.Import.Audio;

/// <summary>
///     The single source of truth for "is this file song audio?".
///     <para>
///     The list is declared once, not inline in
///     (<c>ProjectSessionService.OpenProject</c>, <c>ProjectSessionService.LoadFile</c>
///     and <c>NewProjectViewModel.ChooseAudio</c>), where copies DISAGREE: a
///     drag-drop/open router accepting only <c>.wav</c>/<c>.ogg</c> would let a dropped
///     <c>.mp3</c> fall through to the project loader, throw
///     <see cref="NotSupportedException" /> and surface as "error loading project"
///     — a corrupt-file message for a perfectly good song. The JUCE engine decodes
///     every extension below, so they all belong here.
///     </para>
/// </summary>
public static class AudioFileTypes
{
    /// <summary>Lower-case, dot-prefixed extensions the audio engine can decode.</summary>
    public static IReadOnlyList<string> Extensions { get; } =
        new[] { ".wav", ".ogg", ".mp3", ".flac", ".m4a", ".aac" };

    /// <summary>The same set as file-picker glob patterns (<c>*.wav</c>, …).</summary>
    public static IReadOnlyList<string> PickerPatterns { get; } =
        new[] { "*.wav", "*.ogg", "*.mp3", "*.flac", "*.m4a", "*.aac" };

    /// <summary>True when <paramref name="path" /> names one of the audio
    /// formats the engine decodes. Extension-only (no I/O); never throws.</summary>
    public static bool IsAudioFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        string extension;
        try
        {
            extension = Path.GetExtension(path);
        }
        catch (ArgumentException)
        {
            // Invalid path characters — not something we can attach anyway.
            return false;
        }

        if (string.IsNullOrEmpty(extension)) return false;

        foreach (var candidate in Extensions)
        {
            if (string.Equals(candidate, extension, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
