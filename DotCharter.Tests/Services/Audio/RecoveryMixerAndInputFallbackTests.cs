using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using DotCharter.Tone.Audio;
using DotCharter.Tone.Audio.Abstractions;
using DotCharter.Tone.Audio.Engine;

namespace DotCharter.Tests.Services.Audio;

public sealed class RecoveryMixerAndInputFallbackTests
{
    [Fact]
    public void PartialRecovery_MergesCurrentMixerEditsAndRetriesMissingStemsUntilExplicitClear()
    {
        using var service = new JuceAudioPlaybackService(FakeLocalization.English());
        var recovery = (MediaRebuildRecovery<JuceAudioPlaybackService.StemMixerSnapshot>)typeof(JuceAudioPlaybackService)
            .GetField("_mediaRebuildRecovery", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
        var request = (MediaLoadRequest)typeof(JuceAudioPlaybackService).GetMethod("RequestMediaLoad",
            BindingFlags.Instance | BindingFlags.NonPublic, null, [typeof(string)], null)!.Invoke(service, ["song.wav"])!;
        SetStem(service, "drums", 0.25, false, false, 125);
        SetStem(service, "vocals", 0.75, true, false, -40);
        var initial = recovery.Capture("song.wav", request, request, service.SnapshotStemMixerLocked())!;
        recovery.Complete(initial, false);

        Teardown(service);
        SetStem(service, "drums", 0.25, false, false, 125);
        service.SetStemVolume("drums", 0.6);
        service.SetStemMute("drums", true);
        service.SetStemSolo("drums", true);
        service.SetMasterMixMuted(false);
        var retry = recovery.Capture("song.wav", request, request, service.SnapshotStemMixerLocked())!;
        var mix = Assert.IsType<JuceAudioPlaybackService.StemMixerSnapshot>(retry.Stems);

        Assert.Equal(2, mix.Stems.Count);
        Assert.False(mix.MasterMixMuted);
        var drums = mix.Stems.Single(s => s.Name == "drums");
        Assert.Equal(0.6, drums.Volume);
        Assert.True(drums.Muted);
        Assert.True(drums.Soloed);
        Assert.Equal(125, drums.OffsetMs);
        Assert.Equal(initial.Stems!.Stems.Single(s => s.Name == "vocals"), mix.Stems.Single(s => s.Name == "vocals"));
        recovery.Complete(retry, false);

        service.ClearStems();
        Assert.Null(recovery.Capture("song.wav", request, request, service.SnapshotStemMixerLocked())!.Stems);
        SetStem(service, "vocals", 1, false, false, 0);
        recovery.Capture("song.wav", request, request, service.SnapshotStemMixerLocked());
        Teardown(service);
        service.ClearStems();
        Assert.Null(recovery.Capture("song.wav", request, request, null)!.Stems);
    }

    [Fact]
    public void UnavailableAsioJacks_UseTheEffectiveWasapiDeviceOrder()
    {
        var config = new AppConfig
        {
            EnableInput = true, AsioInputMono = false, InputChannelsAreSeparateGuitars = true,
            AudioDriver = "ASIO", AsioInputChannelLeft = 3, AsioInputChannelRight = 2
        };
        Assert.Equal(new[] { 1, 0 }, EffectiveLayout(config, 4).Channels.Take(2));
        config.AudioDriver = "WASAPI";
        Assert.Equal(0u, GuitarInputChannels.InputChannelMask(config, 2));
        var fallback = EffectiveLayout(config, 2);
        Assert.Equal(2, fallback.PlayerCount);
        Assert.Equal(new[] { 0, 1 }, fallback.Channels.Take(2));
        Assert.Equal(1, EffectiveLayout(config, 1).PlayerCount);
        Assert.Equal(new[] { 1, 0 }, EffectiveLayout(config, 0).Channels.Take(2));
        config.AsioInputChannelLeft = 1;
        config.AsioInputChannelRight = 0;
        Assert.Equal(new[] { 1, 0 }, EffectiveLayout(config, 2).Channels.Take(2));

        var source = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(),
            "DotCharter.Tone/Audio/JuceAudioPlaybackService.EngineConfig.cs"));
        Assert.Contains("SetLiveInputBaseline(LiveInputSeatLayout.FromInputConfig(config, inputCount))", source);
    }

    private static LiveInputSeatLayout EffectiveLayout(RigConfig config, int inputCount)
    {
        var method = typeof(LiveInputSeatLayout).GetMethod(nameof(LiveInputSeatLayout.FromInputConfig),
            [typeof(RigConfig), typeof(int)]);
        return method is null
            ? LiveInputSeatLayout.FromInputConfig(config)
            : (LiveInputSeatLayout)method.Invoke(null, [config, inputCount])!;
    }

    private static void Teardown(JuceAudioPlaybackService service) =>
        typeof(JuceAudioPlaybackService).GetMethod("FreeAudioLocked", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, null);

    private static void SetStem(JuceAudioPlaybackService service, string name, double volume,
        bool muted, bool soloed, double offset)
    {
        var type = typeof(JuceAudioPlaybackService).GetNestedType("StemEntry", BindingFlags.NonPublic)!;
        var entry = RuntimeHelpers.GetUninitializedObject(type);
        type.GetProperty("Name")!.SetValue(entry, name);
        type.GetProperty("Path")!.SetValue(entry, name + ".wav");
        type.GetProperty("OffsetMs")!.SetValue(entry, offset);
        type.GetField("UserVolume")!.SetValue(entry, volume);
        type.GetField("UserMuted")!.SetValue(entry, muted);
        type.GetField("UserSoloed")!.SetValue(entry, soloed);
        var stems = (IDictionary)typeof(JuceAudioPlaybackService)
            .GetField("_stems", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
        stems[name] = entry;
    }
}
