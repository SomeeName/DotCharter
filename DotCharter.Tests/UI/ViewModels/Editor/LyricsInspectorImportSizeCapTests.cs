using DotCharter.Core.Domain.Vocals;
using DotCharter.Services.Audio.Lyrics;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Services.Infrastructure;
using DotCharter.Services.Parsing.Lrc;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     The UltraStar and LRC pickers offer "All files", so what arrives is
///     whatever the user pointed at. The length is asked of the file-system seam
///     before the text is read, and an oversized file is refused with a message
///     that says so rather than "import failed".
/// </summary>
public sealed class LyricsInspectorImportSizeCapTests
{
    private const string LargeOnDisk = "huge.txt";

    [Fact]
    public async Task AnUltraStarFileOverTheCap_IsRefusedAndNeverRead()
    {
        var (inspector, state, files) = Build(ImportSizeLimits.LyricsTextFileBytes + 1);

        await inspector.ImportUltraStarCommand.ExecuteAsync(null);

        Assert.Equal(0, files.Reads);
        Assert.Equal(StatusSeverity.Error, state.MainDisplaySeverity);
        Assert.Contains("too large", state.MainDisplayMessage, StringComparison.Ordinal);
        Assert.Contains(LargeOnDisk, state.MainDisplayMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnLrcFileOverTheCap_IsRefusedAndNeverRead()
    {
        var (inspector, state, files) = Build(ImportSizeLimits.LyricsTextFileBytes + 1);

        await inspector.ImportLrcCommand.ExecuteAsync(null);

        Assert.Equal(0, files.Reads);
        Assert.Equal(StatusSeverity.Error, state.MainDisplaySeverity);
        Assert.Contains("too large", state.MainDisplayMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFileAtTheCap_IsStillRead()
    {
        var (inspector, _, files) = Build(ImportSizeLimits.LyricsTextFileBytes);

        // The seam throws on the read, which the import reports as a failure; what
        // matters is that the read WAS attempted.
        await inspector.ImportUltraStarCommand.ExecuteAsync(null);

        Assert.Equal(1, files.Reads);
    }

    private static (LyricsInspectorViewModel Inspector, ProjectState State, OversizedFileWriter Files) Build(
        long reportedLength)
    {
        var logger = new FakeAppLogger();
        var state = new ProjectState();
        var history = new HistoryService(logger);
        var selection = new SelectionService();

        var project = new ChartProject();
        for (var i = 0; i < 8; i++)
            project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120, BeatsInMeasure = 4, IsMeasure = i % 4 == 0 });
        var track = new VocalTrack { Name = "Vocals" };
        project.VocalTracks.Add(track);
        state.ActiveProject = project;

        var transport = new TransportViewModel(new FakeAudioPlaybackService(), state,
            new FakeConfigurationService(), FakeLocalization.KeysOnly());
        var editor = new ChartEditorViewModel(state, transport, history, selection,
            new TempoService(logger), new TrackContextRegistry(state, selection), FakeLocalization.KeysOnly())
        {
            CurrentChart = project.Chart,
            ActiveVocalTrack = track
        };

        var files = new OversizedFileWriter(reportedLength);
        var storage = new FakeFileStorageService { NextOpenFile = Path.Combine(Path.GetTempPath(), LargeOnDisk) };
        var inspector = new LyricsInspectorViewModel(editor, history, state, logger, storage,
            new LrcLyricsParser(), new NoopAutoSync(), FakeLocalization.English(), files: files);
        return (inspector, state, files);
    }

    private sealed class NoopAutoSync : ILyricAutoSyncService
    {
        public string ModelDirectory => "";
        public bool IsModelInstalled => false;

        public Task<LyricAutoSyncOutcome> SyncAsync(string lyricsText, string audioPath,
            IList<BeatMarker> beats, LyricAutoSyncOptions? options = null,
            IProgress<string>? progress = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("these tests never reach auto-sync");
    }
}
