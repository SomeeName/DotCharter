using DotCharter.Presentation.Dialogs;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Presentation.Screens.Editor;
using DotCharter.Tests.Presentation.Dialogs;
using DotCharter.UI.ViewModels.Shell;

namespace DotCharter.Tests.UI.ViewModels.Shell;

public sealed class ImportMenuViewModelTests
{
    [Fact]
    public void AnImportWithNoLauncherRegistered_IsLoggedAsAnError_NotSilentlyIgnored()
    {
        var logger = new FakeAppLogger();
        var imports = new ImportMenuViewModel(logger, Offer(new ProjectState(), logger), CleanupOffer());

        imports.ImportGuitarProCommand.Execute(null);
        imports.OpenEofImport("song.eof");

        Assert.Equal(2, logger.Entries.Count(e => e.Level == "ERROR" && e.Category == AppLogCategory.Import));
        Assert.Contains(logger.Entries, e => e.Message.Contains("Guitar Pro import", StringComparison.Ordinal));
        Assert.Contains(logger.Entries, e => e.Message.Contains("Editor on Fire import", StringComparison.Ordinal));
    }

    internal static ShapeCleanupOffer CleanupOffer() => new(new DialogsSlot(), new ChartEditHarness().Services);

    internal static AlignToRecordingOffer Offer(ProjectState state, IAppLogger logger) =>
        new(new DialogsSlot(), state, new FakeAudioPlaybackService(), FakeLocalization.English(), logger);
}
