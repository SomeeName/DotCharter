using DotCharter.Services.Export.Sheet;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Export.Sheet;

/// <summary>
/// The PDF export must be all-or-nothing. The old call site opened the user's
/// chosen path with <see cref="FileMode.Create"/> — which truncates on OPEN —
/// and only then started rendering, so a failure anywhere downstream left a
/// zero-byte ".pdf" that looked like a finished export, and exporting over an
/// existing PDF destroyed it before its replacement had been produced.
/// </summary>
[Collection("AlphaTab")]
public class SheetPdfAtomicWriteTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "dotcharter-sheetpdf-" + Guid.NewGuid().ToString("N"));

    public SheetPdfAtomicWriteTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        BestEffortDelete.Directory(_dir);
        GC.SuppressFinalize(this);
    }

    private static (ChartProject Project, Arrangement Arr, ChartLevel Level, List<BeatMarker> Beats) Fixture(
        int bars = 2)
    {
        var project = new ChartProject();
        project.Chart.SongName = "Song";
        var arr = new Arrangement { Name = "Lead", StringCount = 6, Tuning = ["E", "A", "D", "G", "B", "e"] };
        var level = new ChartLevel();
        arr.Levels.Add(level);
        project.Arrangements.Add(arr);

        var beats = new List<BeatMarker>();
        for (var i = 0; i < bars * 4; i++)
            beats.Add(new BeatMarker { Time = i * 500, Bpm = 120, BeatsInMeasure = 4, NoteDenominator = 4 });
        for (var i = 0; i < bars * 4; i++)
            level.Notes.Add(new GuitarNote { Position = new FractionalPosition(i), StringIndex = 2, Fret = 5 });

        return (project, arr, level, beats);
    }

    private static string[] StrayTempFiles(string dir) =>
        Directory.GetFiles(dir, "*" + DotCharter.Tone.Infrastructure.Storage.AtomicFile.TempSuffix);

    /// <summary>
    /// THE regression guard: a failing export must leave the previous file
    /// untouched and no temp beside it. An already-cancelled token fails the
    /// render before it reaches AlphaSkia, which makes this deterministic and
    /// display-free.
    /// </summary>
    [Fact]
    public async Task AFailedExport_LeavesTheExistingFileIntact_AndNoTempBehind()
    {
        var (project, arr, level, beats) = Fixture();
        var path = Path.Combine(_dir, "sheet.pdf");
        const string previous = "%PDF-1.4 a perfectly good earlier export";
        await File.WriteAllTextAsync(path, previous, TestContext.Current.CancellationToken);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new SheetPdfExporter(FakeLocalization.English()).ExportToFileAsync(
                project, arr, level, beats, staves: 2, SheetPageFormat.A4, path, cts.Token));

        Assert.Equal(previous, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.Empty(StrayTempFiles(_dir));
    }

    [Fact]
    public async Task AFailedExport_CreatesNoFileAtAll_WhenThereWasNone()
    {
        var (project, arr, level, beats) = Fixture();
        var path = Path.Combine(_dir, "fresh.pdf");

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new SheetPdfExporter(FakeLocalization.English()).ExportToFileAsync(
                project, arr, level, beats, staves: 2, SheetPageFormat.A4, path, cts.Token));

        Assert.False(File.Exists(path), "a failed export must not leave a file behind");
        Assert.Empty(StrayTempFiles(_dir));
    }

    [Fact]
    public async Task ASuccessfulExport_WritesARealPdf_AndCleansUpItsTemp()
    {
        var (project, arr, level, beats) = Fixture(bars: 4);
        var path = Path.Combine(_dir, "ok.pdf");

        await new SheetPdfExporter(FakeLocalization.English()).ExportToFileAsync(
            project, arr, level, beats, staves: 2, SheetPageFormat.A4, path,
            TestContext.Current.CancellationToken);

        Assert.True(File.Exists(path));
        var head = new byte[5];
        await using (var fs = File.OpenRead(path)) _ = await fs.ReadAsync(head, TestContext.Current.CancellationToken);
        Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(head));
        Assert.Empty(StrayTempFiles(_dir));
    }

    /// <summary>
    ///     An export the user already cancelled must not touch the disk at all —
    ///     not even to CREATE the destination folder. It used to run
    ///     <c>Directory.CreateDirectory</c>, open the temp stream and build the
    ///     whole alphaTex document on the calling (UI) thread before the token
    ///     was ever consulted, because the first thing that looked at it was the
    ///     render, four calls in.
    /// </summary>
    [Fact]
    public async Task AnAlreadyCancelledExport_DoesNotEvenCreateTheFolder()
    {
        var (project, arr, level, beats) = Fixture();
        var folder = Path.Combine(_dir, "not-yet");
        var path = Path.Combine(folder, "sheet.pdf");

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new SheetPdfExporter(FakeLocalization.English()).ExportToFileAsync(
                project, arr, level, beats, staves: 2, SheetPageFormat.A4, path, cts.Token));

        Assert.False(Directory.Exists(folder), "a cancelled export must not create its output folder");
    }

    /// <summary>A chart with no beats still produces a valid one-page PDF (the
    /// title block plus "No notation to display"), not a zero-byte file.</summary>
    [Fact]
    public async Task AZeroLengthChart_StillWritesAValidPdf()
    {
        var (project, arr, level, _) = Fixture();
        var path = Path.Combine(_dir, "empty.pdf");

        await new SheetPdfExporter(FakeLocalization.English()).ExportToFileAsync(
            project, arr, level, [], staves: 2, SheetPageFormat.A4, path,
            TestContext.Current.CancellationToken);

        Assert.True(new FileInfo(path).Length > 0);
        Assert.Empty(StrayTempFiles(_dir));
    }
}
