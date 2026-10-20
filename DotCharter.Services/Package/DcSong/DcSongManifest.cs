namespace DotCharter.Services.Package.DcSong;

/// <summary>
///     The parsed <c>manifest.json</c> of a <c>.dcsong</c> package. Field by field
///     it is described in <c>docs/agents/services/dcsong-format.md</c>.
/// </summary>
public sealed record DcSongManifest(
    int FormatMajor,
    int FormatMinor,
    string Writer,
    DcSongInfo Song,
    DcSongAudioInfo Audio,
    IReadOnlyList<DcSongFileEntry> Files)
{
    /// <summary>The one member with <paramref name="role" />, or null.</summary>
    public DcSongFileEntry? Find(DcSongFileRole role) => Files.FirstOrDefault(f => f.Role == role);

    /// <summary>The stem named <paramref name="name" />, or null.</summary>
    public DcSongFileEntry? FindStem(string name) =>
        Files.FirstOrDefault(f => f.Role == DcSongFileRole.Stem && string.Equals(f.StemName, name, StringComparison.Ordinal));

    /// <summary>The stems, in the order the writer added them.</summary>
    public IReadOnlyList<DcSongFileEntry> Stems => Files.Where(f => f.Role == DcSongFileRole.Stem).ToList();
}

/// <summary>What the library shows before anything is opened.</summary>
public sealed record DcSongInfo(
    string Key,
    string ProjectId,
    string Title,
    string Artist,
    string Album,
    string Year,
    string Charter,
    double LengthMs,
    bool HasVocals,
    IReadOnlyList<DcSongArrangementInfo> Arrangements);

/// <summary>One instrument arrangement: its name, its type and its tuning name.</summary>
public sealed record DcSongArrangementInfo(string Name, string Type, string Tuning);

/// <summary>How the song audio is stored and where the preview sits in it.</summary>
/// <param name="Codec">Opus or FLAC.</param>
/// <param name="Lossless">True exactly when the codec is FLAC.</param>
/// <param name="BitrateKbps">The Opus bitrate; null for FLAC.</param>
/// <param name="PreviewStartMs">Where in the song the preview clip was cut.</param>
/// <param name="PreviewLengthMs">How long the preview clip is.</param>
public sealed record DcSongAudioInfo(
    DcSongAudioCodec Codec,
    bool Lossless,
    int? BitrateKbps,
    double PreviewStartMs,
    double PreviewLengthMs);

/// <summary>One member of the package and the SHA-256 of its bytes.</summary>
/// <param name="Path">The member name, forward-slashed.</param>
/// <param name="Role">What the member is.</param>
/// <param name="Size">The member's length in bytes, uncompressed.</param>
/// <param name="Sha256">Lower-case hex, 64 characters.</param>
/// <param name="StemName">Set for <see cref="DcSongFileRole.Stem" /> only.</param>
/// <param name="MediaType">Set for the images: <c>image/png</c> or <c>image/jpeg</c>.</param>
public sealed record DcSongFileEntry(
    string Path,
    DcSongFileRole Role,
    long Size,
    string Sha256,
    string? StemName = null,
    string? MediaType = null);
