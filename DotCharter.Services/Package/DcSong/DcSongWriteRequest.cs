namespace DotCharter.Services.Package.DcSong;

/// <summary>What to put in a <c>.dcsong</c>.</summary>
public sealed record DcSongWriteRequest
{
    /// <summary>The chart. Not modified: the writer packages a clone.</summary>
    public required ChartProject Project { get; init; }

    /// <summary>The song audio the chart was charted against.</summary>
    public required string AudioPath { get; init; }

    /// <summary>Where the package goes. Replaced atomically if it exists.</summary>
    public required string OutputPath { get; init; }

    /// <summary>FLAC instead of Opus: a package for charting rather than playing.</summary>
    public bool Lossless { get; init; }

    /// <summary>Where the preview clip starts; null picks a quarter into the song.</summary>
    public double? PreviewStartMs { get; init; }

    /// <summary>A PNG or JPEG cover. Null falls back to the project's own cover art
    /// (<see cref="ChartData.CoverArtPath" />, resolved against
    /// <see cref="ProjectDirectory" />), which is skipped with a warning when it is
    /// not usable. An image given HERE that is not usable fails the write.</summary>
    public string? CoverPath { get; init; }

    /// <summary>A PNG or JPEG banner, or null for none.</summary>
    public string? BannerPath { get; init; }

    /// <summary>The project's folder, for a relative cover-art path.</summary>
    public string? ProjectDirectory { get; init; }

    /// <summary>Stems to include. Empty by default: they roughly triple the size.</summary>
    public IReadOnlyList<DcSongStemSource> Stems { get; init; } = [];

    /// <summary>The song key; null derives one from artist and title.</summary>
    public string? SongKey { get; init; }

    /// <summary>Indexes into the project's <see cref="ChartProject.Arrangements" /> to package;
    /// null packages every arrangement. Vocal tracks always travel with the chart.</summary>
    public IReadOnlyCollection<int>? ArrangementIndexes { get; init; }
}

/// <summary>One stem: a name (<c>[a-z0-9_-]{1,32}</c>) and an audio file. An
/// <c>.opus</c> file is stored as it is; anything else is encoded to Opus.</summary>
public sealed record DcSongStemSource(string Name, string SourcePath);

/// <summary>The stages of a write, in the order they run.</summary>
public enum DcSongWriteStage
{
    DecodingAudio,
    EncodingAudio,
    EncodingPreview,
    EncodingStems,
    WritingChart,
    WritingPackage
}

/// <summary>
///     Progress of a write. The service never names a stage in a language: the
///     dialog resolves <see cref="ResourceKey" />, a computed key family registered
///     in <c>LocalizationSweepTests.EveryComputedKeyFamily_IsCompleteInEveryLanguage</c>.
/// </summary>
public sealed record DcSongWriteProgress(int Step, int TotalSteps, DcSongWriteStage Stage)
{
    public double Fraction => TotalSteps == 0 ? 0 : (double)Step / TotalSteps;

    public string ResourceKey => KeyFor(Stage);

    public static string KeyFor(DcSongWriteStage stage) => $"DcSong_WriteStage_{stage}";
}

/// <summary>A written package: where, what its manifest says, how big, and what to tell the user.</summary>
public sealed record DcSongWriteResult(
    string OutputPath,
    DcSongManifest Manifest,
    long PackageBytes,
    IReadOnlyList<DcSongWriteWarning> Warnings);
