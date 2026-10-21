using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Services.Infrastructure;

namespace DotCharter.Tests.Presentation.Dialogs;

/// <summary>
///     A MIDI file is read whole before it is parsed, so its length is checked
///     first, through the same seam as the read. The file system here reports
///     the size and fails the test if a byte is read.
/// </summary>
public sealed class MidiImportSizeCapTests
{
    [Fact]
    public async Task AFileOverTheCap_IsRefusedWithAFileTooLargeMessage_AndNeverRead()
    {
        var state = new ProjectState { ActiveProject = new ChartProject { Bpm = 120 } };
        var files = new OversizedFileWriter(ImportSizeLimits.MidiFileBytes + 1);
        var statusBar = new RecordingStatusBarService();
        var dialogs = new ScriptedDialogs(_ => throw new InvalidOperationException("no dialog should open"));
        var launcher = new MidiImportLauncher(dialogs, new FakeFileStorageService(), new FakeConfigurationService(),
            statusBar, state, files, new DialogBasics(FakeLocalization.English(), new FakeAppLogger()));

        var opened = await launcher.OpenAsync(Path.Combine(Path.GetTempPath(), "huge.mid"));

        Assert.False(opened);
        Assert.Equal(0, files.Reads);
        Assert.Empty(dialogs.Opened);
        Assert.Equal(StatusSeverity.Error, state.MainDisplaySeverity);
        Assert.Contains("too large", state.MainDisplayMessage, StringComparison.Ordinal);
        Assert.Contains("huge.mid", state.MainDisplayMessage, StringComparison.Ordinal);
        Assert.Contains("complete:" + MidiImportLauncher.ReadTaskId, statusBar.Log);
    }

    [Fact]
    public void TheCapIsGenerousForARealScore() =>
        Assert.True(ImportSizeLimits.MidiFileBytes >= 32L * 1024 * 1024);
}
