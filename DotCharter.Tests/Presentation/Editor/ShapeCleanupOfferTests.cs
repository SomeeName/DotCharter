using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Contracts.Editor;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Presentation.Screens.Editor;
using DotCharter.Tests.Presentation.Dialogs;
using DotCharter.Tests.Services.Editing.ChordShapes;
using DotCharter.Tests.UI.ViewModels.Shell;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.UI.ViewModels.Shell;

namespace DotCharter.Tests.Presentation.Editor;

/// <summary>
///     After an import the shapes it left are looked at once: a messy chart gets the Clean up dialog, a tidy one
///     gets nothing at all. The Rocksmith XML door is driven end to end, because it is the import whose launcher
///     reports whether anything was merged.
/// </summary>
public sealed class ShapeCleanupOfferTests : IDisposable
{
    private readonly ChartEditHarness _h = new();
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "dcshapecleanup-" + Guid.NewGuid().ToString("N"));

    public ShapeCleanupOfferTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose() => BestEffortDelete.Directory(_tempDir);

    private ScriptedDialogs Dialogs() => new(
        type => type == typeof(IShapeCleanupScreen) ? new ShapeCleanupViewModel(_h.Services) : NewRsXmlCard(),
        screen =>
        {
            if (screen is IShapeCleanupScreen cleanup) cleanup.CancelCommand.Execute(null);
        });

    private RsXmlImportViewModel NewRsXmlCard() => new(_h.State, _h.Editor, FakeLocalization.English(), new FakeAppLogger());

    private MessyShapesFixture MakeChartMessy()
    {
        var messy = new MessyShapesFixture();
        _h.Project.Arrangements.Add(messy.Arrangement);
        _h.Editor.ActiveArrangement = messy.Arrangement;
        return messy;
    }

    private string WriteShowlightsXml()
    {
        var path = Path.Combine(_tempDir, "showlights.xml");
        File.WriteAllText(path, """
            <?xml version="1.0" encoding="utf-8"?>
            <showlights count="1">
              <showlight time="0.500" note="24" />
            </showlights>
            """);
        return path;
    }

    [Fact]
    public async Task AMessyChart_OpensTheDialog()
    {
        MakeChartMessy();
        var dialogs = Dialogs();

        var offered = await new ShapeCleanupOffer(dialogs, _h.Services).OfferAsync("test import");

        Assert.True(offered);
        Assert.IsType<ShapeCleanupViewModel>(Assert.Single(dialogs.Opened));
    }

    [Fact]
    public async Task ATidyChart_OpensNothing()
    {
        var dialogs = Dialogs();

        var offered = await new ShapeCleanupOffer(dialogs, _h.Services).OfferAsync("test import");

        Assert.False(offered);
        Assert.Empty(dialogs.Opened);
    }

    [Fact]
    public async Task WithoutAnArrangement_OpensNothing()
    {
        _h.Editor.ActiveArrangement = null;
        var dialogs = Dialogs();

        Assert.False(await new ShapeCleanupOffer(dialogs, _h.Services).OfferAsync("test import"));
        Assert.Empty(dialogs.Opened);
    }

    [Fact]
    public async Task DecliningTheDialog_ChangesNothing()
    {
        var messy = MakeChartMessy();

        await new ShapeCleanupOffer(Dialogs(), _h.Services).OfferAsync("test import");

        Assert.Equal(10, messy.Arrangement.ChordTemplates.Count);
        Assert.False(_h.History.CanUndo);
    }

    [Fact]
    public async Task TheRsXmlLauncher_ReportsWhetherAnythingWasMerged()
    {
        RsXmlImportViewModel? card = null;
        var dialogs = new ScriptedDialogs(_ => card = NewRsXmlCard());
        var launcher = new RsXmlImportLauncher(dialogs, new FakeFileStorageService(), new FakeConfigurationService(),
            _h.State, new FakeAppLogger(), FakeLocalization.English());

        Assert.True(await launcher.OpenAsync(WriteShowlightsXml()));

        // A failed read keeps the card up with its reason; closing it answers false.
        var bogus = Path.Combine(_tempDir, "bogus.xml");
        File.WriteAllText(bogus, "<?xml version=\"1.0\" encoding=\"utf-8\"?><nonsense />");
        var pending = launcher.OpenAsync(bogus);
        await card!.Import;
        card.CloseCommand.Execute(null);
        Assert.False(await pending);
    }

    [Fact]
    public async Task AnRsXmlImportDroppedOnAMessyChart_EndsInTheCleanUpDialog()
    {
        MakeChartMessy();
        var opened = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dialogs = new ScriptedDialogs(
            type => type == typeof(IShapeCleanupScreen) ? new ShapeCleanupViewModel(_h.Services) : NewRsXmlCard(),
            screen =>
            {
                if (screen is not IShapeCleanupScreen cleanup) return;
                opened.TrySetResult(true);
                cleanup.CancelCommand.Execute(null);
            });
        var launcher = new RsXmlImportLauncher(dialogs, new FakeFileStorageService(), new FakeConfigurationService(),
            _h.State, new FakeAppLogger(), FakeLocalization.English());
        var imports = new ImportMenuViewModel(new FakeAppLogger(), ImportMenuViewModelTests.Offer(_h.State, new FakeAppLogger()),
            new ShapeCleanupOffer(dialogs, _h.Services), rsXmlImport: launcher);

        imports.OpenRsXmlImport(WriteShowlightsXml());

        // A deadlock detector, not a budget: the import reads off the UI thread, then the offer opens the dialog.
        Assert.True(await opened.Task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken));
        Assert.Contains(dialogs.Opened, d => d is IRsXmlImportScreen);
    }
}
