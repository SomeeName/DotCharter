using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Services.Export.Audio;

namespace DotCharter.Tests.UI.ViewModels.Dialogs;

public sealed partial class RocksmithExportDialogViewModelTests
{
    [Theory]
    [InlineData(".ogg")]
    [InlineData(".flac")]
    [InlineData(".mp3")]
    public void Working_audio_is_filled_and_export_enabled_with_a_matching_hint(string extension)
    {
        using var keys = TestRocksmithKeys.UseNoKeys();
        var harness = AudioFormatHarness(extension);
        using var vm = harness.Open();

        Assert.Equal(harness.State.CurrentAudioFileName, vm.AudioPath);
        Assert.False(vm.MissingPaths);
        Assert.True(vm.ExportCommand.CanExecute(null), vm.ExportBlockedReason);
        Assert.Contains(extension[1..].ToUpperInvariant(), vm.MainAudioWatermark);
    }

    [Fact]
    public async Task Picked_ogg_is_accepted_and_offered_by_the_audio_picker()
    {
        using var keys = TestRocksmithKeys.UseNoKeys();
        var harness = AudioFormatHarness(".wav");
        using var vm = harness.Open();
        var picked = Path.Combine(_tempDir, "picked.ogg");
        File.WriteAllText(picked, "synthetic audio");
        harness.Storage.NextOpenFile = picked;

        await ((IAsyncRelayCommand)vm.BrowseAudioCommand).ExecuteAsync(null);

        Assert.Equal(picked, vm.AudioPath);
        Assert.True(vm.ExportCommand.CanExecute(null), vm.ExportBlockedReason);
        var filters = Assert.Single(harness.Storage.RequestedFilters)!;
        Assert.Contains("*.ogg", filters[0].Patterns);
        Assert.Equal(ExportAudioFormats.PickerPatterns, filters[0].Patterns);
        Assert.Equal(harness.Edit.Localization["Picker_AudioFiles"], filters[0].Name);
        Assert.Contains("OGG", Assert.Single(harness.Storage.RequestedTitles));
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("ja-JP")]
    public async Task Watermark_and_picker_share_all_formats_in_each_language(string culture)
    {
        using var keys = TestRocksmithKeys.UseNoKeys();
        var harness = AudioFormatHarness(".ogg");
        using var vm = harness.Open();
        await SettleOpenTimeVolumes(vm);
        var notifications = new List<string?>();
        vm.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
        var localization = (FakeLocalization)harness.Edit.Localization;
        localization.SwitchTo(culture);
        await ((IAsyncRelayCommand)vm.BrowseAudioCommand).ExecuteAsync(null);

        Assert.Contains(nameof(vm.MainAudioWatermark), notifications);
        Assert.Contains(ExportAudioFormats.DisplayText, vm.MainAudioWatermark);
        Assert.Contains(ExportAudioFormats.DisplayText, Assert.Single(harness.Storage.RequestedTitles));
        Assert.Equal(localization.Format("Dialog_Export_MainAudioRequired", ExportAudioFormats.DisplayText), vm.MainAudioWatermark);
        Assert.Equal(localization.Format("Picker_SelectExportAudio", ExportAudioFormats.DisplayText), Assert.Single(harness.Storage.RequestedTitles));
    }

    /// <summary>
    ///     #896: opening the dialog measures the song's volumes on the pool, and that measurement sets
    ///     <c>AudioVolume</c> when it ends. Outside the app's UI context its continuation runs on the
    ///     pool, so a test recording notifications must settle it first or it races the recorder.
    /// </summary>
    [Fact]
    public async Task Open_time_volume_measurement_notifies_before_it_settles()
    {
        using var keys = TestRocksmithKeys.UseNoKeys();
        using var release = new ManualResetEventSlim();
        var harness = AudioFormatHarness(".ogg");
        harness.VolumeDecoder = _ =>
        {
            BoundedWait.For(release, "the test to release the held volume decode");
            return AudibleTone();
        };
        using var vm = harness.Open();
        var notifications = new System.Collections.Concurrent.ConcurrentQueue<string?>();
        vm.PropertyChanged += (_, e) => notifications.Enqueue(e.PropertyName);
        Task settled;
        try
        {
            settled = SettleOpenTimeVolumes(vm);
            Assert.False(settled.IsCompleted, "the settle returned while the open-time volume measurement was still held");
        }
        finally
        {
            release.Set();
        }

        await settled;

        Assert.Contains(nameof(vm.AudioVolume), notifications);
    }

    private static Task SettleOpenTimeVolumes(RocksmithExportViewModel vm) =>
        SettleVolumeTask(VolumeTask(vm), "the open-time volume measurement");

    private static PcmAudio AudibleTone()
    {
        var samples = new short[48000];
        for (var i = 0; i < samples.Length; i++) samples[i] = (short)(8000 * Math.Sin(2 * Math.PI * 440 * i / 48000.0));
        return new PcmAudio(samples, 1, 48000);
    }

    private Harness AudioFormatHarness(string extension)
    {
        var working = Path.Combine(_tempDir, "song" + extension);
        File.WriteAllText(working, "synthetic audio");
        var harness = new Harness(CreateValidProject())
        {
            VolumeDecoder = _ => new PcmAudio(new short[4800], 1, 48000),
            VolumeSignature = _ => "synthetic"
        };
        harness.State.CurrentProjectPath = Path.Combine(_tempDir, "project.dcproj");
        harness.State.CurrentAudioFileName = working;
        return harness;
    }
}
