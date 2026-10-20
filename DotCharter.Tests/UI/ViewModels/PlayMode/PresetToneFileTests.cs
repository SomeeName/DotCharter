using DotCharter.Tone.Audio.Dsp.Presets;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.Tone.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI.ViewModels.PlayMode;

/// <summary>
///     The preset browser's tone-FILE exchange: <c>ExportActiveCommand</c> writes
///     the LIVE rig as a JSON tone through the save picker, and
///     <c>ImportCommand</c> reads one, stores it as one of the player's own presets
///     and applies it.
///     <para>
///         Every case asserts the OUTCOME — the bytes on disk, the preset the
///         service was asked to save, the tone that was applied, or the localized
///         status line — never the command having been invoked. A bad file must
///         produce a sentence and nothing else: it may not throw, must not save,
///         and must not apply.
///     </para>
/// </summary>
public sealed class PresetToneFileTests : IDisposable
{
    private readonly List<string> _temp = [];

    private string TempPath(string name)
    {
        var path = Path.Combine(Path.GetTempPath(),
            $"dotcharter-tone-{Guid.NewGuid():N}-{name}");
        _temp.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var path in _temp)
            BestEffortDelete.File(path);
    }

    private static DcRigPreset LiveRig() => new(
    [
        new DcRigModule("delay", new Dictionary<string, float> { ["mix"] = 0.42f })
    ]);

    private static (PresetBrowserViewModel Browser, FakeDspTonePresetService Presets,
        FakeFileStorageService Files) NewBrowser(int applied = 0)
    {
        var presets = new FakeDspTonePresetService();
        var files = new FakeFileStorageService();
        var appliedCount = applied;
        var browser = new PresetBrowserViewModel(presets, FakeLocalization.English(), LiveRig, () => appliedCount++, files: files);
        return (browser, presets, files);
    }

    // ── export ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Export_WritesTheLiveRig_AsAToneFileThatImportsBack()
    {
        var (browser, presets, files) = NewBrowser();
        var path = TempPath("export.json");
        files.NextSaveFile = path;

        // Apply a factory tone first: the exported file takes its identity from
        // the ACTIVE preset, and its SOUND from the live chain.
        browser.ApplyPreset(presets.AllPresets[0]);

        await browser.ExportActiveCommand.ExecuteAsync(null);

        Assert.True(File.Exists(path));
        var parsed = TonePresetJsonFile.Parse(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.True(parsed.Ok);
        Assert.Equal(presets.AllPresets[0].Name, parsed.Preset!.Name);
        // …the live chain's own module and value, not the preset's rig.
        var module = Assert.Single(parsed.Preset.Rig.Modules);
        Assert.Equal("delay", module.ModuleTypeId);
        Assert.Equal(0.42f, module.Params["mix"], 5);

        // The status names the FILE, which is what the player recognises.
        Assert.Contains(Path.GetFileName(path), browser.ImportStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_WithNoActiveTone_StillWritesAFile()
    {
        var (browser, _, files) = NewBrowser();
        var path = TempPath("unnamed.json");
        files.NextSaveFile = path;

        await browser.ExportActiveCommand.ExecuteAsync(null);

        Assert.True(File.Exists(path));
        Assert.True(TonePresetJsonFile.Parse(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)).Ok);
        Assert.True(browser.HasImportStatus);
    }

    [Fact]
    public async Task CancellingTheSavePicker_WritesNothing_AndSaysNothing()
    {
        var (browser, _, files) = NewBrowser();
        files.NextSaveFile = null;

        await browser.ExportActiveCommand.ExecuteAsync(null);

        Assert.False(browser.HasImportStatus);
        Assert.Equal(string.Empty, browser.ImportStatus);
    }

    [Fact]
    public async Task AnUnwritablePath_ReportsAStatus_AndDoesNotThrow()
    {
        var (browser, _, files) = NewBrowser();
        // A directory that does not exist: the write fails at the OS, which is the
        // shape a read-only folder or a vanished network share takes.
        files.NextSaveFile = Path.Combine(Path.GetTempPath(),
            $"dotcharter-missing-{Guid.NewGuid():N}", "tone.json");

        await browser.ExportActiveCommand.ExecuteAsync(null);

        Assert.True(browser.HasImportStatus);
    }

    // ── a card's own tone (the card menu's Export to file…) ─────────────────

    [Fact]
    public async Task ExportCard_WritesThatCardsPreset_NotTheLiveRig()
    {
        var (browser, presets, files) = NewBrowser();
        var path = TempPath("card.json");
        files.NextSaveFile = path;
        var mine = presets.SaveUser("Shelf Tone", DspToneCategory.Lead, "for the solo",
            new DcRigPreset([new DcRigModule("overdrive", new Dictionary<string, float> { ["drive"] = 0.7f })]));
        browser.SelectedRail = browser.Rail.First(r => r.Kind == DotCharter.Tone.UI.Controls.Dsp.PresetFilterKind.User);
        var card = browser.Cards.Single(c => c.Id == mine.Id);
        // a DIFFERENT tone is on the rig: the export must not read the live chain
        browser.ApplyPreset(presets.AllPresets[0]);

        Assert.True(browser.ExportCardCommand.CanExecute(card));
        await browser.ExportCardCommand.ExecuteAsync(card);

        var parsed = TonePresetJsonFile.Parse(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.True(parsed.Ok);
        Assert.Equal("Shelf Tone", parsed.Preset!.Name);
        Assert.Equal(DspToneCategory.Lead, parsed.Preset.Category);
        var module = Assert.Single(parsed.Preset.Rig.Modules);
        Assert.Equal("overdrive", module.ModuleTypeId);          // the card's rig…
        Assert.Equal(0.7f, module.Params["drive"], 5);           // …not LiveRig()'s "delay"
        Assert.Contains(Path.GetFileName(path), browser.ImportStatus, StringComparison.Ordinal);
        // exporting changes nothing in the library
        Assert.Single(presets.Saves);
    }

    [Fact]
    public async Task ExportCard_OfABuiltIn_WritesIt()
    {
        var (browser, presets, files) = NewBrowser();
        var path = TempPath("builtin.json");
        files.NextSaveFile = path;
        var builtIn = browser.Cards.First(c => !c.IsUser);

        await browser.ExportCardCommand.ExecuteAsync(builtIn);

        // (the fake's built-ins have empty rigs, which the parser refuses as a
        // tone — so the file is read as text: it is the built-in's, by name)
        Assert.True(File.Exists(path));
        Assert.Contains(builtIn.Name, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken),
            StringComparison.Ordinal);
        Assert.Empty(presets.Saves);
    }

    [Fact]
    public void WithNoFilePickers_ExportCard_ShipsDisabled()
    {
        var browser = new PresetBrowserViewModel(new FakeDspTonePresetService(), FakeLocalization.English(), LiveRig, () => { });

        Assert.False(browser.ExportCardCommand.CanExecute(browser.Cards[0]));
    }

    // ── import ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Import_StoresTheToneAsAUserPreset_AndAppliesIt()
    {
        var (browser, presets, files) = NewBrowser();
        var path = TempPath("import.json");
        var incoming = new DspTonePreset("somebody-elses-id", "Shared Crunch",
            DspToneCategory.Crunch, "from a friend",
            new DcRigPreset([new DcRigModule("overdrive", new Dictionary<string, float> { ["drive"] = 0.8f })]),
            DspToneOrigin.User);
        await File.WriteAllTextAsync(path, TonePresetJsonFile.Serialize(incoming), TestContext.Current.CancellationToken);
        files.NextOpenFile = path;

        await browser.ImportCommand.ExecuteAsync(null);

        // Saved as the player's OWN preset — through the same path ConfirmSave
        // takes, which mints a fresh id: an imported file may never overwrite a
        // preset the player made.
        var save = Assert.Single(presets.Saves);
        Assert.Equal("Shared Crunch", save.Name);
        Assert.Equal(DspToneCategory.Crunch, save.Category);
        Assert.Null(save.OverwriteId);
        Assert.Equal("overdrive", save.Rig.Modules[0].ModuleTypeId);
        Assert.Equal(0.8f, save.Rig.Modules[0].Params["drive"], 5);

        // …and it is HEARD: an import you cannot hear is a file operation.
        Assert.Contains(presets.UserPresets.Single().Id, presets.Applied);
        Assert.Equal("Shared Crunch", browser.ActivePresetName);
        Assert.Contains("Shared Crunch", browser.ImportStatus);

        // It is selectable in My Tones afterwards.
        Assert.NotNull(browser.SelectedCard);
        Assert.True(browser.SelectedCard!.IsUser);
    }

    [Fact]
    public async Task AToneFileWithNoName_IsNamedAfterTheFile()
    {
        var (browser, presets, files) = NewBrowser();
        var path = TempPath("Garage Fuzz.json");
        await File.WriteAllTextAsync(path,
            "{\"Name\":\"\",\"Modules\":[{\"ModuleTypeId\":\"delay\",\"Params\":{}}]}",
            TestContext.Current.CancellationToken);
        files.NextOpenFile = path;

        await browser.ImportCommand.ExecuteAsync(null);

        // The file name IS what the player recognises, so a nameless tone takes it.
        Assert.Equal(Path.GetFileNameWithoutExtension(path), Assert.Single(presets.Saves).Name);
        Assert.EndsWith("Garage Fuzz", Assert.Single(presets.Saves).Name, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABadFile_SaysSo_AndChangesNothing()
    {
        var (browser, presets, files) = NewBrowser();
        var path = TempPath("broken.json");
        await File.WriteAllTextAsync(path, "this is not a tone", TestContext.Current.CancellationToken);
        files.NextOpenFile = path;

        await browser.ImportCommand.ExecuteAsync(null);

        Assert.True(browser.HasImportStatus);
        Assert.Empty(presets.Saves);
        Assert.Empty(presets.Applied);
    }

    [Fact]
    public async Task AToneNamingAnUnknownModule_SaysWhichOne()
    {
        var (browser, presets, files) = NewBrowser();
        var path = TempPath("future.json");
        await File.WriteAllTextAsync(path,
            "{\"Name\":\"Future\",\"Modules\":[{\"ModuleTypeId\":\"quantumfuzz\",\"Params\":{}}]}",
            TestContext.Current.CancellationToken);
        files.NextOpenFile = path;

        await browser.ImportCommand.ExecuteAsync(null);

        Assert.Contains("quantumfuzz", browser.ImportStatus);
        Assert.Empty(presets.Saves);
    }

    [Fact]
    public async Task AMissingFile_IsReported_NotThrown()
    {
        var (browser, presets, files) = NewBrowser();
        files.NextOpenFile = Path.Combine(Path.GetTempPath(), $"dotcharter-gone-{Guid.NewGuid():N}.json");

        await browser.ImportCommand.ExecuteAsync(null);

        Assert.True(browser.HasImportStatus);
        Assert.Empty(presets.Saves);
    }

    [Fact]
    public async Task CancellingTheOpenPicker_ChangesNothing()
    {
        var (browser, presets, files) = NewBrowser();
        files.NextOpenFile = null;

        await browser.ImportCommand.ExecuteAsync(null);

        Assert.False(browser.HasImportStatus);
        Assert.Empty(presets.Saves);
    }

    [Fact]
    public void WithNoFilePickers_TheToneFileCommandsShipDisabled()
    {
        // A host with no window must not offer a half-working button (the
        // "a capability the app cannot honour never looks live" rule).
        var browser = new PresetBrowserViewModel(
            new FakeDspTonePresetService(), FakeLocalization.English(), LiveRig, () => { });

        Assert.False(browser.CanExchangeToneFiles);
        Assert.False(browser.ExportActiveCommand.CanExecute(null));
        Assert.False(browser.ImportCommand.CanExecute(null));
    }

    [Fact]
    public void TheRigScreen_SurfacesTheSameToneFileCommands()
    {
        var dsp = new FakeDspChainService();
        var files = new FakeFileStorageService();
        var rig = TestRigBuilders.Pedalboard(dsp, new FakeDspTonePresetService(), FakeLocalization.English(), fileStorage: files);
        var screen = new AmpFxRigViewModel(rig);

        Assert.Same(rig.Browser.ExportActiveCommand, screen.ExportToneCommand);
        Assert.Same(rig.Browser.ImportCommand, screen.ImportToneCommand);
        Assert.True(rig.Browser.CanExchangeToneFiles);

        // …and the header's status line follows the browser's.
        Assert.False(screen.HasToneFileStatus);
        rig.Browser.ImportStatus = "written";
        Assert.Equal("written", screen.ToneFileStatus);
        Assert.True(screen.HasToneFileStatus);
    }
}
