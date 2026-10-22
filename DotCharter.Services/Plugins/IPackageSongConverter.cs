// IPackageSongConverter: the seam a plugin fills to turn game packages into .dcsong files; the report types say what a run did.
namespace DotCharter.Services.Plugins;

/// <summary>How a conversion run writes.</summary>
public sealed record PackageConversionOptions
{
    /// <summary>FLAC instead of Opus (<c>DcSongWriteRequest.Lossless</c>).</summary>
    public bool Lossless { get; init; }

    /// <summary>Leave a song whose output file already exists alone.</summary>
    public bool SkipExisting { get; init; }
}

/// <summary>What became of one song (or of a package that never yielded one).</summary>
public enum PackageConversionStatus
{
    Converted,
    Skipped,
    Failed
}

/// <summary>One song's outcome. <see cref="SongKey" /> is null when the PACKAGE failed before any
/// song was read; <see cref="Error" /> is set for <see cref="PackageConversionStatus.Failed" />.</summary>
public sealed record PackageConversionItem(
    string PackagePath,
    string? SongKey,
    string? OutputPath,
    PackageConversionStatus Status,
    string? Error);

/// <summary>A whole run: how many packages were found and every song's outcome, in package order.</summary>
public sealed record PackageConversionReport(int Packages, IReadOnlyList<PackageConversionItem> Songs)
{
    public int Converted => Songs.Count(s => s.Status == PackageConversionStatus.Converted);

    public int Skipped => Songs.Count(s => s.Status == PackageConversionStatus.Skipped);

    public int Failed => Songs.Count(s => s.Status == PackageConversionStatus.Failed);
}

/// <summary>
///     Converts game packages into <c>.dcsong</c> files in one step: the chart, the song audio
///     and the cover of every song a package holds. Registered by a plugin that reads packages
///     (<see cref="IPackageFeatures.ReadsGamePackages" />); a build without one has none, and
///     the File ▸ Import row and the palette entry that drive it are absent. A bad package or
///     song is REPORTED (<see cref="PackageConversionStatus.Failed" /> with the reason) and the
///     run goes on; only cancellation escapes.
/// </summary>
public interface IPackageSongConverter
{
    /// <summary>The file-name patterns the converter accepts (<c>*.ext</c>), for a file picker.</summary>
    IReadOnlyList<string> FilePatterns { get; }

    /// <summary>
    ///     Converts every package <paramref name="inputs" /> names (a file, or a folder: every package
    ///     under it). The <c>.dcsong</c> goes into <paramref name="outputDirectory" /> (created if
    ///     missing), or beside its package when that is null. <paramref name="progress" /> gets each
    ///     song's outcome as it lands.
    /// </summary>
    Task<PackageConversionReport> ConvertAsync(
        IReadOnlyList<string> inputs,
        string? outputDirectory,
        PackageConversionOptions? options = null,
        IProgress<PackageConversionItem>? progress = null,
        CancellationToken cancellationToken = default);
}
