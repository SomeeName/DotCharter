using DotCharter.Presentation.Screens.Settings;
using DotCharter.Services.Gameplay.Profiles;
using DotCharter.Presentation.Shared;
using DotCharter.Services.Audio.LivePlay;
using DotCharter.Services.Editing.Catalog;
using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.Services.Session;
using DotCharter.Tone.UI.ViewModels.PlayMode;
using static DotCharter.Tests.Presentation.Settings.SplitTabStubs;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     Each tone-side and backup picker (#832) opens in the folder of its last pick and
///     moves that folder on a new pick. One test per picker, so unwiring one fails one test.
/// </summary>
[Collection("LocalizationSingleton")]
public sealed class PickerFolderWiringTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dc-pickerwiring-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => BestEffortDelete.Directory(_root);

    private string Folder(string name) => Directory.CreateDirectory(Path.Combine(_root, name)).FullName;

    private static IReadOnlyList<DspModuleType> Catalog() =>
        FakeDspChainService.DefaultCatalog()
            .Append(new DspModuleType("namamp", "NAM Amp", [])
            { StringParams = [new DspStringParamInfo("modelPath", "NAM Model", "*.nam;*.aidax")] })
            .Append(new DspModuleType("irloader", "IR Loader", [])
            { StringParams = [new DspStringParamInfo("irPath", "Impulse Response", "*.wav")] })
            .ToList();

    private static async Task AssertPedalPick(string moduleId, Func<AppConfig, string> read, Action<AppConfig, string> seed,
        string first, string second)
    {
        var dsp = new FakeDspChainService(Catalog());
        var node = dsp.AddNode(moduleId)!;
        var config = new FakeConfigurationService();
        seed(config.AppConfig, first);
        var files = new FakeFileStorageService { NextOpenFile = Path.Combine(second, "x.bin") };
        var info = dsp.Catalog.Single(m => m.Id == moduleId).StringParams.Single();
        var param = new PedalStringParamViewModel(dsp, node.NodeId, info, string.Empty, files, FakeLocalization.English())
        {
            PickerConfig = config
        };

        await param.PickFileCommand.ExecuteAsync(null);

        Assert.Equal(first, files.RequestedStartDirectories.Single());
        Assert.Equal(second, read(config.AppConfig));
    }

    [Fact]
    public async Task The_NAM_capture_picker_opens_in_the_last_capture_folder_and_moves_it() =>
        await AssertPedalPick("namamp", c => c.LastNamCaptureDir, (c, d) => c.LastNamCaptureDir = d,
            Folder("c1"), Folder("c2"));

    [Fact]
    public async Task The_IR_picker_opens_in_the_last_IR_folder_and_moves_it() =>
        await AssertPedalPick("irloader", c => c.LastIrDir, (c, d) => c.LastIrDir = d, Folder("i1"), Folder("i2"));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_preset_import_and_export_open_in_the_last_preset_folder_and_move_it(bool export)
    {
        var first = Folder("p1");
        var second = Folder("p2");
        var config = new FakeConfigurationService();
        config.AppConfig.LastPresetDir = first;
        var target = Path.Combine(second, "tone.json");
        var files = new FakeFileStorageService { NextOpenFile = target, NextSaveFile = target };
        var vm = new PresetBrowserViewModel(new FakeDspTonePresetService(), FakeLocalization.KeysOnly(),
            () => new DotCharter.Tone.Audio.Dsp.RsToneConversion.DcRigPreset([]), () => { }, config, files: files);

        if (export) await vm.ExportActiveCommand.ExecuteAsync(null);
        else await vm.ImportCommand.ExecuteAsync(null);

        Assert.Equal(first, files.RequestedStartDirectories.Single());
        Assert.Equal(second, config.AppConfig.LastPresetDir);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_backup_export_and_restore_open_in_the_last_backup_folder_and_move_it(bool export)
    {
        var first = Folder("b1");
        var second = Folder("b2");
        var config = new FakeConfigurationService();
        config.AppConfig.LastBackupDir = first;
        var files = new FakeFileStorageService
        {
            NextOpenFile = Path.Combine(second, "b.zip"), NextSaveFile = Path.Combine(second, "b.zip")
        };
        var players = Recorder<IPlayerProfileService>();
        var projectSession = Recorder<IProjectSessionService>();
        var stores = new ImportedConfigStores(config, players, Recorder<IPracticeProfileService>(),
            Recorder<IPhraseCatalogService>(), projectSession);
        var vm = new BackupMigrateViewModel(config, new RecordingLocalization(), files, new FakeBackup(), stores,
            new SettingsSession(), new FakeAppLogger());

        if (export) await vm.ExportAsync();
        else await vm.ImportAsync();

        Assert.Equal(first, files.RequestedStartDirectories.Single());
        Assert.Equal(second, config.AppConfig.LastBackupDir);
    }
}
