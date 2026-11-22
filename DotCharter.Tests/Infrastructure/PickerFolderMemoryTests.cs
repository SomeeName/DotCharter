using System.Text.Json;
using DotCharter.Tone.Audio.Reamp;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.Tone.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     The remembered file-picker folders (#832): each new setting survives a save and reload,
///     a pick moves the folder, and a folder deleted since falls back to the dialog's default
///     without an error.
/// </summary>
public sealed class PickerFolderMemoryTests : IDisposable
{
    private static readonly string[] NewSettings =
    [
        "LastToneFileDir", "LastPsarcToneDir", "LastNamCaptureDir", "LastIrDir", "LastPresetDir", "LastReampDiDir",
        "LastProfileDir", "LastProfilePictureDir", "LastDiagnosticsDir", "LastBackupDir", "LastLyricsDir",
        "LastChartDiffDir", "LastConversionOutputDir", "LastDcSongDir"
    ];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "dc-pickerfolders-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => BestEffortDelete.Directory(_root);

    [Fact]
    public void Every_new_setting_round_trips_through_the_config_json()
    {
        var config = new AppConfig();
        foreach (var name in NewSettings)
        {
            var property = typeof(AppConfig).GetProperty(name);
            Assert.NotNull(property);
            property.SetValue(config, Path.Combine(_root, name));
        }

        var back = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(config))!;
        foreach (var name in NewSettings)
            Assert.Equal(Path.Combine(_root, name), typeof(AppConfig).GetProperty(name)!.GetValue(back));
    }

    [Fact]
    public void A_folder_that_exists_is_the_start_folder_and_a_deleted_one_is_not()
    {
        Directory.CreateDirectory(_root);
        var config = new FakeConfigurationService();
        config.AppConfig.LastIrDir = _root;
        Assert.Equal(_root, config.StartFolder(c => c.LastIrDir));
        Assert.Equal(_root, config.StartDirectory(c => c.LastIrDir));

        config.AppConfig.LastIrDir = Path.Combine(_root, "gone");
        Assert.Null(config.StartFolder(c => c.LastIrDir));
        Assert.Null(config.StartDirectory(c => c.LastIrDir));
        Assert.Null(config.StartFolder(c => string.Empty));
        Assert.Null(((IRigConfigService?)null).StartFolder(c => c.LastIrDir));
    }

    [Fact]
    public void Remembering_a_pick_stores_the_folder_of_the_file_and_ignores_a_cancel()
    {
        var config = new FakeConfigurationService();
        config.RememberFileFolder(Path.Combine(_root, "a", "x.nam"), (c, d) => c.LastNamCaptureDir = d);
        Assert.Equal(Path.Combine(_root, "a"), config.AppConfig.LastNamCaptureDir);

        config.RememberFileFolder(null, (c, d) => c.LastNamCaptureDir = d);
        config.RememberFileFolder(string.Empty, (c, d) => c.LastNamCaptureDir = d);
        Assert.Equal(Path.Combine(_root, "a"), config.AppConfig.LastNamCaptureDir);
    }

    [Fact]
    public async Task The_reamp_import_opens_where_the_last_one_was_and_moves_the_folder_on_a_pick()
    {
        var takes = Path.Combine(_root, "takes");
        var first = Path.Combine(_root, "first");
        var second = Path.Combine(_root, "second");
        Directory.CreateDirectory(first);
        var config = new FakeConfigurationService();
        config.AppConfig.LastReampDiDir = first;
        var files = new FakeFileStorageService { NextOpenFile = Path.Combine(second, "di.wav") };
        using var vm = new RigReampViewModel(null, new DiTakeLibrary(takes), FakeLocalization.English(), config, files: files)
        {
            ManualTick = true
        };

        await vm.ImportCommand.ExecuteAsync(null);
        Assert.Equal(first, files.RequestedStartDirectories.Single());
        Assert.Equal(second, config.AppConfig.LastReampDiDir);

        // The remembered folder is gone: the dialog gets no start folder, and nothing throws.
        await vm.ImportCommand.ExecuteAsync(null);
        Assert.Equal([first, null], files.RequestedStartDirectories);
    }
}
