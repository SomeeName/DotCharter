using DotCharter.Services.Import.Audio;

namespace DotCharter.Services.Export.Audio;

/// <summary>Common export input formats, shared by picker filters and display hints.</summary>
/// <remarks>
///     Working-audio formats come from <see cref="AudioFileTypes" />. The package builder also
///     accepts pre-encoded WEM, and <see cref="PcmAudioLoader" /> handles the additional container
///     aliases and ffmpeg inputs below. Actual codecs depend on the installed native decoder
///     and ffmpeg; ffmpeg can accept further formats. This catalog never gates decoding, so
///     file pickers must keep their All files option.
/// </remarks>
public static class ExportAudioFormats
{
    public static IReadOnlyList<string> Extensions { get; } =
        AudioFileTypes.Extensions.Concat([".wem", ".aif", ".aiff", ".aifc", ".bwf", ".oga", ".wma", ".opus"])
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public static IReadOnlyList<string> PickerPatterns { get; } =
        Extensions.Select(extension => "*" + extension).ToArray();

    public static string DisplayText { get; } = string.Join(" / ",
        Extensions.Select(extension => extension.TrimStart('.').ToUpperInvariant()));
}
