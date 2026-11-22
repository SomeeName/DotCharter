using DotCharter.Services.Audio.StemSeparation.GuitarRoles;
using DotCharter.Services.Audio.Coordination;
using DotCharter.Tone.Audio.Abstractions;
using DotCharter.Tests.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.Services.Session;

public sealed partial class ProjectSessionAttachAudioTests
{
    [Theory]
    [InlineData("volume")]
    [InlineData("mute")]
    [InlineData("solo")]
    [InlineData("original")]
    public async Task PersistedMixerEdits_MarkACleanSessionDirty(string edit)
    {
        var (h, dir) = CreateAudiolessProject();
        AddStem(h, dir);
        using var mixer = await Mixer(h);
        Assert.False(h.Session.IsProjectDirty);
        var stem = mixer.Tracks.Single(t => !t.IsMasterMix);
        switch (edit)
        {
            case "volume": stem.VolumeLinear = 0.25; break;
            case "mute": stem.IsMuted = true; break;
            case "solo": stem.IsSolo = true; break;
            case "original": mixer.Tracks[0].IsMuted = false; break;
        }

        Assert.True(h.Session.IsProjectDirty);
    }

    [Fact]
    public async Task RunMutes_AndMasterVolume_DoNotDirtyTheProject()
    {
        var (h, dir) = CreateAudiolessProject();
        AddStem(h, dir);
        using var mixer = await Mixer(h);
        var stem = mixer.Tracks.Single(t => !t.IsMasterMix);

        stem.SetMutedForRun(true);
        stem.SetMutedForRun(false);
        mixer.Tracks[0].VolumeLinear = 0.25;

        Assert.False(h.Session.IsProjectDirty);
        Assert.False(h.State.ActiveProject!.Stems[0].Muted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MixerRecovery_MarksAdoptionOrUnmuteDirty(bool declared)
    {
        var (h, dir) = CreateAudiolessProject();
        AddStem(h, dir);
        if (declared) h.State.ActiveProject!.Stems[0].Muted = true;
        else h.State.ActiveProject!.Stems.Clear();
        using var mixer = await Mixer(h);

        Assert.True(h.Session.IsProjectDirty);
        Assert.Single(h.State.ActiveProject!.Stems);
        if (declared) Assert.False(h.State.ActiveProject.OriginalMixMuted);
    }

    [Fact]
    public async Task RestoringARunSolo_ReconcilesTheMasterWithoutPersistingTheMute()
    {
        var (h, dir) = CreateAudiolessProject();
        AddStem(h, dir);
        h.State.ActiveProject!.OriginalMixMuted = false;
        h.State.ActiveProject.Stems[0].Solo = true;
        using var mixer = await Mixer(h);
        var stem = mixer.Tracks.Single(t => !t.IsMasterMix);

        stem.SetMutedForRun(true);
        stem.SetMutedForRun(false);
        stem.RestoreSoloFromProject();

        Assert.True(stem.IsSolo);
        Assert.True(mixer.Tracks[0].IsMuted);
        Assert.False(h.State.ActiveProject.OriginalMixMuted);
        Assert.False(h.Session.IsProjectDirty);
    }

    [Fact]
    public async Task MutingTheLastSolo_RestoresTheOriginalMix()
    {
        var (h, dir) = CreateAudiolessProject();
        AddStem(h, dir);
        h.State.ActiveProject!.OriginalMixMuted = false;
        h.State.ActiveProject.Stems[0].Muted = true;
        using var mixer = await Mixer(h);
        var stem = mixer.Tracks.Single(t => !t.IsMasterMix);
        stem.IsSolo = true;
        Assert.True(mixer.Tracks[0].IsMuted);

        stem.IsMuted = true;

        Assert.False(stem.IsSolo);
        Assert.False(mixer.Tracks[0].IsMuted);
        Assert.False(h.Audio.MasterMixMuted);
        Assert.False(h.State.ActiveProject.OriginalMixMuted);
    }

    [Fact]
    public async Task AFailedRoleSplit_MarksTheRemovedSavedSelectionDirty()
    {
        var (h, dir) = CreateAudiolessProject();
        AddStem(h, dir, "guitar");
        h.State.ActiveProject!.GuitarRolesSplit = true;
        var ui = new GatedInlineDispatcher();
        var roles = new GuitarRolesViewModelTests.FakeGuitarRoleStems { DeriveVerdict = GuitarRoleVerdict.Unreadable };
        using var mixer = new StemMixerViewModel(h.State, h.Audio, FakeLocalization.KeysOnly(), ui: ui,
            guitarRoleStems: roles);
        await StemMixerSettle.Async(() => ui.Read(() => !mixer.IsRebuilding && !mixer.GuitarRoles.IsWorking));

        Assert.False(h.State.ActiveProject.GuitarRolesSplit);
        Assert.True(h.Session.IsProjectDirty);
    }

    [Fact]
    public async Task GuitarRoleSelection_MarksEnablingAndDisablingDirty()
    {
        var (h, dir) = CreateAudiolessProject();
        AddStem(h, dir, "guitar");
        var ui = new GatedInlineDispatcher();
        var roles = new GuitarRolesViewModelTests.FakeGuitarRoleStems
        {
            Derived = new GuitarRolePair(Path.Combine(dir, "lead.wav"), Path.Combine(dir, "rhythm.wav"))
        };
        using var mixer = new StemMixerViewModel(h.State, h.Audio, FakeLocalization.KeysOnly(), ui: ui,
            guitarRoleStems: roles);
        await StemMixerSettle.Async(() => ui.Read(() => !mixer.IsRebuilding && mixer.GuitarRoles.CanToggle));
        Assert.False(h.Session.IsProjectDirty);

        mixer.GuitarRoles.IsOn = true;
        await StemMixerSettle.Async(() => ui.Read(() => h.State.ActiveProject!.GuitarRolesSplit));
        Assert.True(h.Session.IsProjectDirty);
        await StemMixerSettle.Async(() => ui.Read(() => !mixer.IsRebuilding && !mixer.GuitarRoles.IsWorking));
        h.Session.MarkProjectClean();

        mixer.GuitarRoles.IsOn = false;

        Assert.False(h.State.ActiveProject!.GuitarRolesSplit);
        Assert.True(h.Session.IsProjectDirty);
        await StemMixerSettle.Async(() => ui.Read(() => !mixer.IsRebuilding));
    }

    [Fact]
    public async Task MasterLoadCompletingAfterInitialStems_DoesNotDecodeTheStemSetAgain()
    {
        var (h, dir) = CreateAudiolessProject();
        AddStem(h, dir);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Audio.AudioLoadGate = gate;
        var load = h.Session.LoadAudioFile(CreateSourceAudio("opening.wav"), generateBeats: false);
        await StemMixerSettle.Async(() => h.Audio.LoadedPaths.Count == 1);
        using var mixer = await Mixer(h);
        Assert.Single(h.Audio.StemLoadCalls);
        Assert.False(load.IsCompleted);

        gate.SetResult();
        await load;
        await StemMixerSettle.Async(() => !mixer.IsRebuilding);

        Assert.Single(h.Audio.StemLoadCalls);
        Assert.Contains("drums", h.Audio.LoadedStemNames);
    }

    [Fact]
    public async Task MasterWaveformPublication_RefreshesTheSelectedMixWithoutReloadingStems()
    {
        var (h, dir) = CreateAudiolessProject();
        AddStem(h, dir);
        h.State.ActiveProject!.OriginalMixMuted = false;
        h.State.WaveformData = WaveformSamples.FromBasePeaks([0.4f], WaveformSamples.GenerationBlockMs);
        h.Audio.WaveformResult = WaveformSamples.FromBasePeaks([0.2f], WaveformSamples.GenerationBlockMs);
        var ui = new GatedInlineDispatcher();
        using var mixer = new StemMixerViewModel(h.State, h.Audio, FakeLocalization.KeysOnly(), ui: ui,
            waveform: new StemWaveformCoordinator(h.Audio, h.State));
        await StemMixerSettle.Async(() => ui.Read(() => !mixer.IsRebuilding));
        ui.Read(() => mixer.WaveformFollowsMix = true);
        await StemMixerSettle.Async(() => h.State.WaveformData!.Mipmaps[0][0] > 0.5f);
        await WaveformSettled();

        await h.Session.LoadAudioFile(CreateSourceAudio("waveform.wav"), generateBeats: false);
        await WaveformSettled();

        // New master 0.2 + cached stem 0.2, rather than the master's 0.2 alone.
        Assert.Equal(0.4f, h.State.WaveformData!.Mipmaps[0][0], 5);
        Assert.Single(h.Audio.StemLoadCalls);

        Task WaveformSettled() => StemMixerSettle.Async(() => ui.Read(() => !mixer.IsRebuilding &&
            !(bool)typeof(StemMixerViewModel).GetField("_waveformRefreshInFlight",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(mixer)!));
    }

    [Fact]
    public async Task LoadingAudioAgain_ReloadsAnUnchangedStemSet()
    {
        var (h, dir) = CreateAudiolessProject();
        AddStem(h, dir);
        using var mixer = await Mixer(h);
        Assert.Single(h.Audio.StemLoadCalls);
        await h.Session.AttachAudioAsync(CreateSourceAudio("again.wav"));
        await StemMixerSettle.Async(() => !mixer.IsRebuilding);

        Assert.Equal(2, h.Audio.StemLoadCalls.Count);
        Assert.Contains("drums", h.Audio.LoadedStemNames);
    }

    [Fact]
    public async Task LoadingAudioDuringAStemLoad_DoesNotRestoreTheObsoleteCache()
    {
        var (h, dir) = CreateAudiolessProject();
        AddStem(h, dir);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Audio.StemLoadGate = gate;
        var ui = new GatedInlineDispatcher();
        using var mixer = new StemMixerViewModel(h.State, h.Audio, FakeLocalization.KeysOnly(), ui: ui);
        await StemMixerSettle.Async(() => h.Audio.StemLoadCalls.Count == 1);

        await h.Session.AttachAudioAsync(CreateSourceAudio("during.wav"));
        h.Audio.StemLoadGate = null;
        gate.SetResult();
        await StemMixerSettle.Async(() => ui.Read(() => !mixer.IsRebuilding));

        Assert.Equal(2, h.Audio.StemLoadCalls.Count);
    }

    private static void AddStem(Harness h, string dir, string name = "drums")
    {
        Directory.CreateDirectory(Path.Combine(dir, "stems"));
        File.WriteAllBytes(Path.Combine(dir, "stems", name + ".wav"), [0]);
        h.State.ActiveProject!.Stems.Add(new Stem { Name = name, Path = Path.Combine("stems", name + ".wav") });
    }

    private static async Task<StemMixerViewModel> Mixer(Harness h)
    {
        var ui = new GatedInlineDispatcher();
        var mixer = new StemMixerViewModel(h.State, h.Audio, FakeLocalization.KeysOnly(), ui: ui);
        await StemMixerSettle.Async(() => ui.Read(() => !mixer.IsRebuilding));
        return mixer;
    }
}
