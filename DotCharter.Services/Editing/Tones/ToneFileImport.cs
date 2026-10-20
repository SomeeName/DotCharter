using DotCharter.Tone.Audio.Dsp.Tones;

namespace DotCharter.Services.Editing.Tones;

/// <summary>Reads a DLC Builder tone file from disk (the file I/O the Tone designer view model may not do itself).</summary>
public static class ToneFileImport
{
    /// <summary>The tones in the file; <see cref="FormatException" /> when it is not a tone file, <see cref="IOException" /> when it cannot be read.</summary>
    public static async Task<IReadOnlyList<RsToneFileTone>> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return RsToneFileReader.Read(text, RsToneFileReader.FallbackKeyFor(path));
    }
}
