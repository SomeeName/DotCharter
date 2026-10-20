using DotCharter.Services.Audio.StemSeparation.Cache;
using DotCharter.Services.Parsing.Rscp;

namespace DotCharter.Services.Package.DcSong;

/// <summary>What opening a <c>.dcsong</c> as a project wrote.</summary>
/// <param name="ProjectPath">The new <c>project.rscp</c>, absolute.</param>
/// <param name="ProjectDirectory">The NEW folder it sits in.</param>
/// <param name="Title">The song title, for the status line.</param>
/// <param name="AudioLossy">True for an Opus package: the project's WAV is a decode of
/// lossy audio, so chart-from-audio and stem separation work best from a FLAC package.</param>
/// <param name="Stems">The packaged stems written into the project, by name.</param>
/// <param name="StemsSkipped">Packaged stems left out because ffmpeg was not there to
/// decode them (a FLAC package opens without ffmpeg; its Opus stems cannot).</param>
public sealed record DcSongUnpackResult(
    string ProjectPath,
    string ProjectDirectory,
    string Title,
    bool AudioLossy,
    IReadOnlyList<string> Stems,
    IReadOnlyList<string> StemsSkipped);

/// <summary>
///     Turns a <c>.dcsong</c> into a project folder that opens like any other
/// (File ▸ Open of a package; format: <c>docs/agents/services/dcsong-format.md</c>
///     § Opening as a project).
///     <para>
///         The folder is ALWAYS new: <see cref="NewProjectFolder.Create" /> under the
///         base folder the user picked, named "Artist - Title" and numbered when that
///         is taken, so opening a package twice never lands on the first copy. Every
///         byte comes out of <see cref="DcSongPackage" />, which reads the untrusted
///         archive member by member, bounded and checksum-verified; the file names
///         written here are FIXED (<c>project.rscp</c>, <c>song.*</c>, <c>cover.*</c>,
///         <c>banner.*</c>, <c>stems/&lt;name&gt;.wav</c> with a name the format limits to
///         <c>[a-z0-9_-]</c>), never taken from the archive.
///     </para>
///     <para>
///         The audio engine does not decode Opus, so an Opus song (and every stem) is
///         decoded to a 48 kHz WAV by ffmpeg; a FLAC song is copied as it is. Without
///         ffmpeg an Opus package is refused BEFORE any folder is made
///         (<see cref="DcSongReadFailure.DecoderMissing" />), and a FLAC package opens
///         without its stems. A failure part-way removes what this call wrote.
///     </para>
/// </summary>
public sealed class DcSongProjectUnpacker
{
    public const string ProjectFileName = "project.rscp";

    private readonly IAppLogger _logger;
    private readonly Func<IStemEncoder> _decoder;

    /// <param name="logger">The log sink.</param>
    /// <param name="configuredFfmpegPath">Reads <c>AppConfig.FfmpegPath</c>, again on every
    /// open, so an ffmpeg installed while the app runs is found.</param>
    public DcSongProjectUnpacker(IAppLogger logger, Func<string?> configuredFfmpegPath)
        : this(logger, () => new FfmpegStemEncoder(logger, configuredFfmpegPath: configuredFfmpegPath))
    {
    }

    /// <summary>Test seam: a decoder that answers without ffmpeg.</summary>
    internal DcSongProjectUnpacker(IAppLogger logger, Func<IStemEncoder> decoder)
    {
        _logger = logger;
        _decoder = decoder;
    }

    /// <summary>
    ///     Unpacks <paramref name="packagePath" /> into a new folder under
    ///     <paramref name="baseDirectory" />. Throws <see cref="DcSongReadException" />
    ///     for a package it cannot open; <see cref="IOException" /> /
    ///     <see cref="UnauthorizedAccessException" /> when the folder cannot be written.
    /// </summary>
    public async Task<DcSongUnpackResult> UnpackAsync(string packagePath, string baseDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        using var package = DcSongPackage.Open(packagePath);
        var decoder = _decoder();
        var lossless = package.IsLossless;
        if (!lossless && !decoder.IsExternalEncoderAvailable)
        {
            throw new DcSongReadException(DcSongReadFailure.DecoderMissing,
                "the package's audio is Opus, and no ffmpeg was found to decode it.");
        }

        var project = package.LoadChart(_logger);
        var info = package.Manifest.Song;
        var title = FirstNonBlank(project.Chart.SongName, info.Title, Path.GetFileNameWithoutExtension(packagePath));
        var artist = FirstNonBlank(project.Chart.Artist, info.Artist, "Unknown Artist");

        var folderName = FolderName(artist, title);
        // NewProjectFolder may hand back an existing EMPTY folder (one the user made in
        // the picker); a failed open must leave that one standing.
        var existing = ExistingFolders(baseDirectory, folderName);
        var projectDir = NewProjectFolder.Create(baseDirectory, folderName);
        var madeHere = !existing.Contains(Path.GetFullPath(projectDir));
        var written = new List<string>();
        var scratch = Directory.CreateTempSubdirectory("dotcharter_dcsong_open_").FullName;
        try
        {
            project.AudioFileName = await WriteAudioAsync(package, decoder, projectDir, scratch, written, cancellationToken);
            project.Chart.CoverArtPath = WriteImage(package.ReadCover(), projectDir, "cover", written) ?? "";
            WriteImage(package.ReadBanner(), projectDir, "banner", written);
            var (stems, skipped) = await WriteStemsAsync(package, project, decoder, projectDir, scratch, written, cancellationToken);

            var projectPath = Path.Combine(projectDir, ProjectFileName);
            written.Add(projectPath);
            new RscpChartSerializer(_logger).Save(projectPath, project);
            _logger.Info(AppLogCategory.Project,
                $".dcsong opened as a project. Package='{packagePath}', projectDir='{projectDir}', codec={package.Manifest.Audio.Codec}, " +
                $"audio='{project.AudioFileName}', cover='{project.Chart.CoverArtPath}', stems=[{string.Join(", ", stems)}], " +
                $"skippedStems=[{string.Join(", ", skipped)}], arrangements={project.Arrangements.Count}.");
            return new DcSongUnpackResult(projectPath, projectDir, title, !lossless, stems, skipped);
        }
        catch
        {
            RemoveWritten(projectDir, written, madeHere);
            throw;
        }
        finally
        {
            BestEffortDelete.Directory(scratch);
        }
    }

    /// <summary>"Artist - Title" with every character a file name cannot hold replaced,
    /// the rule the other create-project flows use.</summary>
    internal static string FolderName(string artist, string title)
    {
        var name = $"{artist.Trim()} - {title.Trim()}";
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        name = name.Trim().TrimEnd('.');
        return name.Length == 0 ? "DotCharter Song" : name;
    }

    private static async Task<string> WriteAudioAsync(
        DcSongPackage package, IStemEncoder decoder, string projectDir, string scratch, List<string> written, CancellationToken ct)
    {
        if (package.IsLossless)
        {
            const string flacName = "song.flac";
            var flac = Path.Combine(projectDir, flacName);
            written.Add(flac);
            using (var audio = package.OpenAudio()) CopyToNewFile(audio, flac);
            return flacName;
        }

        const string wavName = "song.wav";
        var opus = Path.Combine(scratch, "song.opus");
        using (var audio = package.OpenAudio()) CopyToNewFile(audio, opus);
        var wav = Path.Combine(projectDir, wavName);
        written.Add(wav);
        await DecodeAsync(decoder, opus, wav, "the song audio", ct);
        return wavName;
    }

    private async Task<(List<string> Stems, List<string> Skipped)> WriteStemsAsync(
        DcSongPackage package, ChartProject project, IStemEncoder decoder, string projectDir, string scratch,
        List<string> written, CancellationToken ct)
    {
        var stems = new List<string>();
        var skipped = new List<string>();
        foreach (var entry in package.Manifest.Stems)
        {
            var name = entry.StemName!;
            // The chart names each packaged stem by its member path (DcSongChart.ForPackage).
            var declared = project.Stems.FirstOrDefault(s => string.Equals(s.Path, entry.Path, StringComparison.Ordinal));
            if (!decoder.IsExternalEncoderAvailable)
            {
                if (declared != null) project.Stems.Remove(declared);
                skipped.Add(name);
                continue;
            }

            var opus = Path.Combine(scratch, name + ".opus");
            using (var stream = package.OpenStem(name)) CopyToNewFile(stream, opus);
            // A child of the folder NewProjectFolder just handed us, so it is ours.
            var stemsDir = new DirectoryInfo(projectDir).CreateSubdirectory("stems");
            var relative = Path.Combine("stems", name + ".wav");
            var wav = Path.Combine(stemsDir.FullName, name + ".wav");
            written.Add(wav);
            await DecodeAsync(decoder, opus, wav, $"stem '{name}'", ct);
            File.Delete(opus);

            declared ??= AddStem(project, name);
            declared.Path = relative;
            declared.Local = true;
            stems.Add(name);
        }

        if (skipped.Count > 0)
        {
            _logger.Warning(AppLogCategory.Project,
                $".dcsong stems left out: ffmpeg was not found to decode them. Stems=[{string.Join(", ", skipped)}].");
        }
        return (stems, skipped);
    }

    private static Stem AddStem(ChartProject project, string name)
    {
        var stem = new Stem { Name = name };
        project.Stems.Add(stem);
        return stem;
    }

    private static async Task DecodeAsync(IStemEncoder decoder, string encoded, string wav, string what, CancellationToken ct)
    {
        try
        {
            await decoder.DecodeToWavAsync(encoded, StemCodec.Opus, wav, ct).ConfigureAwait(false);
        }
        catch (StemDecodeUnavailableException ex)
        {
            throw new DcSongReadException(DcSongReadFailure.DecodeFailed, $"ffmpeg could not decode {what}: {ex.Message}", ex);
        }

        if (!File.Exists(wav))
            throw new DcSongReadException(DcSongReadFailure.DecodeFailed, $"ffmpeg wrote no file for {what}.");
    }

    /// <summary>Writes an image under a fixed name; returns that name, or null when the
    /// package has none.</summary>
    private static string? WriteImage(DcSongImage? image, string projectDir, string baseName, List<string> written)
    {
        if (image == null) return null;
        var name = baseName + (image.MediaType == DcSongImages.Jpeg ? ".jpg" : ".png");
        var path = Path.Combine(projectDir, name);
        written.Add(path);
        File.WriteAllBytes(path, image.Bytes);
        return name;
    }

    private static void CopyToNewFile(Stream source, string path)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        source.CopyTo(file);
    }

    /// <summary>The folders under <paramref name="baseDirectory" /> that
    /// <see cref="NewProjectFolder.Create" /> could pick for <paramref name="folderName" />
    /// and that exist already (the name and its numbered forms all start with it).</summary>
    private static HashSet<string> ExistingFolders(string baseDirectory, string folderName)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(baseDirectory)) return existing;
        foreach (var dir in Directory.EnumerateDirectories(baseDirectory, folderName + "*")) existing.Add(Path.GetFullPath(dir));
        return existing;
    }

    /// <summary>A failed open removes what it wrote, and the folder when this call made it
    /// and that leaves it empty; anything else was not ours and stays.</summary>
    private void RemoveWritten(string projectDir, List<string> written, bool madeHere)
    {
        foreach (var path in written) BestEffortDelete.File(path);
        foreach (var dir in madeHere ? new[] { Path.Combine(projectDir, "stems"), projectDir } : [Path.Combine(projectDir, "stems")])
        {
            try
            {
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.Warning(AppLogCategory.Project, $".dcsong open: could not remove '{dir}' after a failure: {ex.Message}");
            }
        }
    }

    private static string FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? "";
}
