using DotCharter.Services.Library;
using DotCharter.Services.Library.Cache;
using DotCharter.Services.Library.Index;
using DotCharter.Tests.Services.Library;
using DotCharter.Tone.Infrastructure.Storage;
using static DotCharter.Tests.Services.Package.DcSong.DcSongTestKit;

namespace DotCharter.Tests.Services.Package.DcSong;

/// <summary>
///     The Library scan reads <c>.dcsong</c> packages beside <c>.psarc</c> archives (#708): the
///     same scan, the same SQLite index, its own source kind, the row the chart and manifest give,
///     a damaged or tampered package skipped with its reason, and the index serving an unchanged
///     package and re-reading a changed one. Hand-built packages, a temp index and a temp cache:
///     nothing here touches the user's library, index or caches.
/// </summary>
public sealed partial class DcSongLibraryScanTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dotcharter_dcsong_library_").FullName;
    private readonly FakeAppLogger _logger = new();
    private readonly FakeDecoder _decoder = new(available: true);

    public DcSongLibraryScanTests() => Directory.CreateDirectory(LibraryDir);

    public void Dispose() => BestEffortDelete.Directory(_dir);

    private string LibraryDir => Path.Combine(_dir, "library");

    private string P(string name) => Path.Combine(LibraryDir, name);

    private LibraryService Service(ILibraryIndex? index = null, List<string>? parsed = null) =>
        new(_logger, TestLibraries.Sources(_logger, Cache(), DcSongLibraryKit.Source(Path.Combine(_dir, "cache"), _decoder, _logger)), index)
        {
            ScanParallelismForTests = 1,
            ScanParseInterceptorForTests = (path, parse) =>
            {
                lock (_logger) parsed?.Add(path);
                return parse();
            }
        };

    private LibraryAudioCacheStore Cache() => new(_logger, Path.Combine(_dir, "cache"));

    private static string[] Describe(IEnumerable<LibrarySongEntry> songs) =>
    [
        .. songs.Select(s =>
            $"{s.PackagePath}|{s.SongKey}|{s.SourceKind}|{s.SongBank}|{s.Title}|{s.TuningSummary}|{s.CapoSummary}|{s.Difficulty}|" +
            string.Join(",", s.Arrangements.Select(a => $"{a.ArrangementName}:{a.SngPath}:{a.RouteMask}:{a.Tuning}:{a.Capo}:{a.NoteCount}")))
    ];
}
