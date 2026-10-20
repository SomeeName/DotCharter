using DotCharter.Localization;

using System.Diagnostics;
using DotCharter.Services.Library;

namespace DotCharter.Services.Infrastructure.LibraryDoctor;

/// <summary>
/// Coordinator: projects the scanned <see cref="LibrarySongEntry"/> list into a
/// pure <see cref="LibraryDoctorSnapshot"/> (probing each package once for
/// readability + audio) and hands it to <see cref="LibraryDoctorAnalyzer"/>.
/// All rule logic lives in the analyzer; this class only gathers the inputs.
/// </summary>
public sealed class LibraryDoctorService : ILibraryDoctorService
{
    private readonly IAppLogger _logger;
    private readonly ILocalizationService _localization;
    private readonly ILibraryPackageProbe _probe;

    /// <summary>How often the probe loop is allowed to post progress — the
    /// same 33 ms (one 30 Hz frame) the PSARC scan uses.</summary>
    public const int ProgressIntervalMs = 33;

    public LibraryDoctorService(IAppLogger logger, ILocalizationService localization, ILibraryPackageProbe probe)
    {
        _logger = logger;
        _localization = localization;
        _probe = probe;
    }

    public Task<LibraryDoctorReport> RunAsync(
        IReadOnlyList<LibrarySongEntry> songs,
        IProgress<LibraryDoctorProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            if (songs.Count == 0) return LibraryDoctorReport.Empty;

            // Probe each distinct package exactly once — a multi-song archive
            // shares one readability + audio verdict across its songs.
            var distinctPaths = songs
                .Select(s => s.PackagePath)
                .Where(p => !string.IsNullOrEmpty(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var probeByPath = new Dictionary<string, PackageProbeResult>(StringComparer.OrdinalIgnoreCase);
            var unreadable = new List<LibraryDoctorUnreadablePackage>();

            // The IProgress is constructed on the UI thread, so every Report is a
            // dispatcher post raising PropertyChanged on a bound ProgressBar. One
            // per package meant 2,000 posts in a burst for a warm library — the
            // exact bug the library scan had already fixed with this
            // gate. The LAST package always
            // reports, so the bar always reaches 100%.
            var lastProgress = Stopwatch.StartNew();

            for (var i = 0; i < distinctPaths.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var path = distinctPaths[i];
                var result = _probe.Probe(path);
                probeByPath[path] = result;
                if (!result.Readable)
                    unreadable.Add(new LibraryDoctorUnreadablePackage(path, result.FailureReason));

                if (progress != null &&
                    (i == distinctPaths.Count - 1 || lastProgress.ElapsedMilliseconds >= ProgressIntervalMs))
                {
                    lastProgress.Restart();
                    progress.Report(new LibraryDoctorProgress(i + 1, distinctPaths.Count));
                }
            }

            var doctorSongs = songs.Select(song =>
            {
                var audio = AudioAvailability.Unknown;
                if (!string.IsNullOrEmpty(song.PackagePath) &&
                    probeByPath.TryGetValue(song.PackagePath, out var probe))
                {
                    audio = probe.Audio;
                }

                return new LibraryDoctorSong
                {
                    PsarcPath = song.PackagePath,
                    SongKey = song.SongKey,
                    Charter = song.Charter,
                    Version = song.Version,
                    Title = song.Title,
                    Artist = song.Artist,
                    TuningSummary = song.TuningSummary,
                    LoadableArrangementCount = song.Arrangements
                        .Count(a => !string.IsNullOrWhiteSpace(a.SngPath)),
                    Audio = audio
                };
            }).ToList();

            var snapshot = new LibraryDoctorSnapshot
            {
                Songs = doctorSongs,
                UnreadablePackages = unreadable
            };

            var report = LibraryDoctorAnalyzer.Analyze(snapshot, _localization);
            _logger.Info(AppLogCategory.Import,
                $"Library doctor finished. Songs={report.SongsScanned}, packages={report.PackagesScanned}, " +
                $"issues={report.TotalIssueCount}.");
            return report;
        }, cancellationToken);
    }
}
