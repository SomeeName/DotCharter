using DotCharter.Services.Export.DlcBuilder;
using DotCharter.Services.Export.GuitarPro;
using DotCharter.Services.Parsing.Eof;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Export;

/// <summary>
///     The <c>.eof</c> and the <c>.rs2dlc</c> are the two companions that are
///     <b>somebody else's project file</b> — the whole reason
///     <c>Export/CompanionOwnership</c> exists — and
///     <c>SaveCompanionService</c> rewrites both inside the worker of every
///     project save. Both were written with a bare <c>File.WriteAllBytes</c> /
///     <c>File.WriteAllText</c>, which TRUNCATES the destination before writing:
///     a crash, a kill or a power cut in that window left the charter's file
///     empty, and neither is regenerable from anything DotCharter holds.
///     <para>
///         The ownership stamp is a different guarantee — it stops us
///         overwriting a file we do not own. It does nothing about truncating
///         one we do.
///     </para>
///     <para>
///         <b>Why a share-delete reader is the discriminator.</b> The
///         destination is held open for the whole call with
///         <c>FileShare.Read | FileShare.Delete</c> — read sharing, delete
///         sharing, and NO write sharing. A truncating writer must open the
///         destination for WRITE and is denied. An atomic writer never opens it
///         at all: it writes a sibling temp file, flushes it, and renames over
///         the target, which <c>AtomicFile.ReplaceWithRetry</c> does through
///         <c>SetFileInformationByHandle(FileRenameInfoEx, POSIX_SEMANTICS)</c>
///         — the same semantics <c>AtomicFileTests
///         .ReplaceWithRetry_SucceedsWhileAShareDeleteReaderHoldsTheDestination</c>
///         pins directly. So this asserts the structural property ("the
///         destination is never opened for truncation"), not a timing.
///     </para>
/// </summary>
public sealed class CompanionWritesAreAtomicTests
{
    [Fact]
    public void ExportingAnEof_NeverTruncatesTheFileItIsReplacing()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "song.eof");
        var previous = new byte[] { 1, 2, 3, 4, 5 };
        File.WriteAllBytes(path, previous);

        var project = new ChartProject();
        project.Arrangements.Add(NewArrangement());

        using (HoldWithoutWriteSharing(path))
        {
            var result = EofExporter.ExportToFile(project, path);
            Assert.NotEmpty(result.Bytes);
        }

        var written = File.ReadAllBytes(path);
        Assert.NotEmpty(written);
        Assert.NotEqual(previous, written);
    }

    [Fact]
    public void WritingAnRs2Dlc_NeverTruncatesTheFileItIsReplacing()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "Artist_Title.rs2dlc");
        const string previous = "{\"handEdited\":true}";
        File.WriteAllText(path, previous);

        var dto = new Rs2DlcProjectDto { DlcKey = "TestKey" };

        using (HoldWithoutWriteSharing(path))
        {
            Rs2DlcWriter.WriteTo(path, dto);
        }

        var written = File.ReadAllText(path);
        Assert.Contains("TestKey", written, StringComparison.Ordinal);
        Assert.DoesNotContain("handEdited", written, StringComparison.Ordinal);
    }

    /// <summary>
    ///     <c>AtomicFile.WriteAllText</c> creates the directory, so dropping the
    ///     explicit <c>Directory.CreateDirectory</c> from
    ///     <see cref="Rs2DlcWriter.WriteTo" /> must not have cost that behaviour
    ///     — the DLC Builder bundle folder does not exist on a first save.
    /// </summary>
    [Fact]
    public void WritingAnRs2Dlc_StillCreatesAMissingFolder()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "DTRS XML", "Artist_Title.rs2dlc");

        Rs2DlcWriter.WriteTo(path, new Rs2DlcProjectDto { DlcKey = "TestKey" });

        Assert.True(File.Exists(path));
    }

    /// <summary>
    ///     The `.gp` export is not a companion — the charter picks the path — but
    ///     it is the same truncating write over a file they may well be replacing
    ///     from a previous export, and it is not regenerable once it is empty.
    /// </summary>
    [Fact]
    public void ExportingAGp_NeverTruncatesTheFileItIsReplacing()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "song.gp");
        var previous = new byte[] { 1, 2, 3, 4, 5 };
        File.WriteAllBytes(path, previous);

        var arrangement = NewArrangement();
        List<BeatMarker> beats = [new() { Time = 0, IsMeasure = true }, new() { Time = 0.5 }];

        using (HoldWithoutWriteSharing(path))
        {
            GpExporter.Export(arrangement, arrangement.Levels[0], beats, path, "Title", "Artist");
        }

        var written = File.ReadAllBytes(path);
        Assert.NotEmpty(written);
        Assert.NotEqual(previous, written);
    }

    /// <summary>
    ///     Read + delete sharing, deliberately no WRITE sharing: an atomic
    ///     rename is allowed through, a truncating open is not.
    /// </summary>
    private static FileStream HoldWithoutWriteSharing(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);

    private static Arrangement NewArrangement()
    {
        var arrangement = new Arrangement { Name = "Lead", ArrangementType = ArrangementType.Lead };
        arrangement.Levels.Add(new ChartLevel());
        return arrangement;
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"dotcharter-companion-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => BestEffortDelete.Directory(Path);
    }
}
