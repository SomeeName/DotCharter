using DotCharter.Services.Infrastructure;
using DotCharter.Services.Parsing.Lrc;
using DotCharter.Services.Parsing.UltraStar;

namespace DotCharter.Services.Import.Lyrics;

/// <summary>An UltraStar file read into vocal tracks: one per voice, the first being the main singer.</summary>
public sealed record UltraStarLyricsImport(IReadOnlyList<VocalTrack> Voices, bool IsRelative);

/// <summary>
///     Reads and writes lyric files (LRC, UltraStar) for the lyrics inspector. It returns results and
///     throws typed failures (<see cref="ImportFileTooLargeException" />); the caller owns the picker,
///     the localized status text and the logging.
/// </summary>
public interface ILyricsFileService
{
    /// <summary>A lyrics file as text; refused past <see cref="ImportSizeLimits.LyricsTextFileBytes" />.</summary>
    Task<string> ReadTextAsync(string filePath);

    Task<IReadOnlyList<LyricPhrase>> ReadLrcAsync(string filePath, IList<BeatMarker> beats);

    Task<UltraStarLyricsImport> ReadUltraStarAsync(string filePath, IList<BeatMarker> beats);

    string BuildLrc(VocalTrack track, IList<BeatMarker> beats, string? title, string? artist);

    string BuildUltraStar(VocalTrack track, IList<BeatMarker> beats, double bpm, string? title, string? artist,
        string? audioFileName);

    /// <summary>Atomic, off the calling thread: a crash mid-export leaves the previous file, not an empty one.</summary>
    Task WriteAsync(string path, string text);

    /// <summary>A file-name stem for <paramref name="name" />; "lyrics" when nothing usable is left.</summary>
    string SuggestFileName(string? name);
}

public sealed class LyricsFileService(IExportTargetWriter files, LrcLyricsParser lrcParser) : ILyricsFileService
{
    /// <summary>
    ///     The length is asked of the same seam as the read and BEFORE it, off the caller's thread.
    /// </summary>
    public async Task<string> ReadTextAsync(string filePath)
    {
        var length = await Task.Run(() => files.FileLength(filePath));
        ImportSizeLimits.RequireWithin(length, ImportSizeLimits.LyricsTextFileBytes, "The lyrics file");
        return await files.ReadAllTextAsync(filePath);
    }

    public async Task<IReadOnlyList<LyricPhrase>> ReadLrcAsync(string filePath, IList<BeatMarker> beats)
        => lrcParser.Parse(await ReadTextAsync(filePath), beats);

    public async Task<UltraStarLyricsImport> ReadUltraStarAsync(string filePath, IList<BeatMarker> beats)
    {
        var song = new UltraStarLyricsParser().Parse(await ReadTextAsync(filePath));
        // One track per voice: a duet's second singer goes into a NEW vocal track
        // rather than interleaving with the first in the active one.
        return new UltraStarLyricsImport(UltraStarVocalConverter.ToVocalTracks(song, beats), song.IsRelative);
    }

    public string BuildLrc(VocalTrack track, IList<BeatMarker> beats, string? title, string? artist)
        => new LrcLyricsWriter().Write(track, beats, title, artist);

    public string BuildUltraStar(VocalTrack track, IList<BeatMarker> beats, double bpm, string? title,
        string? artist, string? audioFileName)
    {
        var audio = string.IsNullOrWhiteSpace(audioFileName) ? null : Path.GetFileName(audioFileName);
        var song = UltraStarVocalConverter.ToSong(track, beats, bpm, gapMs: 0, title, artist, audio);
        return new UltraStarLyricsWriter().Write(song);
    }

    // AtomicFile: a bare write truncates first, so a crash mid-export
    // leaves an empty file where the previous export was.
    public Task WriteAsync(string path, string text) => Task.Run(() => AtomicFile.WriteAllText(path, text));

    public string SuggestFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "lyrics";
        var cleaned = SafeFileName.Make(name);
        return cleaned.Length == 0 ? "lyrics" : cleaned;
    }
}
