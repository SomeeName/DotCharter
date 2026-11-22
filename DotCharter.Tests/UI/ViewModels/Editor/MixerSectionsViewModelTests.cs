using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Editor.Inspector;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     The Mixer tab's section strip (inspector design D) and the stem strips' resets: Stems is offered only while
///     the song has stems, a tab without them opens on EQ, stems that go away hand their section to EQ, and a reset
///     puts a fader back at unity without touching mute or solo.
/// </summary>
public sealed class MixerSectionsViewModelTests
{
    private static StemMixerViewModel Mixer() =>
        new(new ProjectState(), new FakeAudioPlaybackService(), FakeLocalization.KeysOnly());

    /// <summary>Design D offers Stems on a song without stems too: the section says why and offers "Separate stems".</summary>
    [Fact]
    public void WithoutStems_TheTabOpensOnEq_AndStillOpensStems()
    {
        var stems = Mixer();
        stems.HasStems = false;
        var sections = new MixerSectionsViewModel(stems);

        Assert.True(sections.IsEq);

        sections.ChooseCommand.Execute(MixerSection.Stems);
        Assert.Equal(MixerSection.Stems, sections.Section);
        Assert.Equal(StemPlaybackSource.NoStems, stems.PlaybackSource);
        Assert.Equal([StemPlaybackSource.NoStems], stems.PlaybackChoices.Select(c => c.Source));

        sections.ChooseCommand.Execute(MixerSection.Midi);
        Assert.True(sections.IsMidi);
    }

    [Fact]
    public void StemsThatArrive_OpenTheirSection_OnlyWhenNothingWasChosen()
    {
        var stems = Mixer();
        stems.HasStems = false;
        var untouched = new MixerSectionsViewModel(stems);
        stems.HasStems = true;
        Assert.True(untouched.IsStems);

        stems.HasStems = false;
        var chosen = new MixerSectionsViewModel(stems);
        chosen.ChooseCommand.Execute(MixerSection.Midi);
        stems.HasStems = true;
        Assert.True(chosen.IsMidi);
    }

    [Fact]
    public void StemsThatGoAway_KeepTheirSection_AndSayThereAreNone()
    {
        var stems = Mixer();
        stems.HasStems = true;
        var sections = new MixerSectionsViewModel(stems);
        Assert.True(sections.IsStems);

        var changed = new List<string?>();
        sections.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        stems.HasStems = false;

        Assert.Equal(MixerSection.Stems, sections.Section);
        Assert.Equal(StemPlaybackSource.NoStems, stems.PlaybackSource);
    }

    [Fact]
    public void AStemsReset_PutsTheFaderAtUnity_AndKeepsMuteAndSolo()
    {
        var audio = new FakeAudioPlaybackService();
        var project = new ChartProject();
        project.Stems.Add(new Stem { Name = "drums", Path = "drums.wav" });
        var drums = new StemTrackViewModel(audio, project.Stems[0], FakeLocalization.KeysOnly())
        {
            VolumeLinear = 0.4,
            IsMuted = true
        };

        drums.ResetLevelCommand.Execute(null);

        Assert.Equal(drums.DefaultVolumeLinear, drums.VolumeLinear);
        Assert.True(drums.IsMuted);
        Assert.Equal(drums.DefaultVolumeLinear, project.Stems[0].Volume);
    }

    [Fact]
    public void ResetLevels_PutsEveryStripAtUnity()
    {
        var audio = new FakeAudioPlaybackService();
        var mixer = new StemMixerViewModel(new ProjectState(), audio, FakeLocalization.KeysOnly());
        mixer.Tracks.Clear();
        var project = new ChartProject();
        project.Stems.Add(new Stem { Name = "drums", Path = "drums.wav" });
        project.Stems.Add(new Stem { Name = "bass", Path = "bass.wav" });
        foreach (var stem in project.Stems)
        {
            var track = new StemTrackViewModel(audio, stem, FakeLocalization.KeysOnly()) { VolumeLinear = 1.7 };
            track.AttachOwner(mixer);
            mixer.Tracks.Add(track);
        }

        mixer.ResetLevelsCommand.Execute(null);
        Assert.True(mixer.ResetLevelsConfirm.IsPending);
        Assert.All(mixer.Tracks, track => Assert.Equal(1.7, track.VolumeLinear));
        mixer.ResetLevelsConfirm.ConfirmCommand.Execute(null);

        Assert.All(mixer.Tracks, track => Assert.Equal(track.DefaultVolumeLinear, track.VolumeLinear));
    }

    /// <summary>A strip's reset is a persisted edit: the project is marked changed; Reset levels marks it ONCE for every strip.</summary>
    [Fact]
    public async Task Resets_MarkTheProjectChanged_ResetLevelsOnce()
    {
        var (mixer, state, _, ui) = await WithStems();
        var changes = 0;
        state.ProjectChanged += () => changes++;
        ui.Post(() =>
        {
            foreach (var track in mixer.Tracks) track.VolumeLinear = 1.5;
        });
        changes = 0;

        // both stems moved: Reset levels writes two stems and marks the project ONCE
        ui.Post(() =>
        {
            mixer.ResetLevelsCommand.Execute(null);
            mixer.ResetLevelsConfirm.ConfirmCommand.Execute(null);
        });
        Assert.Equal(1, changes);
        Assert.All(state.ActiveProject!.Stems, stem => Assert.Equal(1.0, stem.Volume));

        var strip = ui.Read(() => mixer.Tracks.First(t => !t.IsMasterMix));
        ui.Post(() => strip.VolumeLinear = 1.5);
        changes = 0;
        ui.Post(() => strip.ResetLevelCommand.Execute(null));
        Assert.Equal(1, changes);
    }

    /// <summary>
    ///     Design D's Playback source: Original mix opens the original strip and silences the stems, Stems the reverse;
    ///     both reach the project and the engine, and a rebuild replays them.
    /// </summary>
    [Fact]
    public async Task ThePlaybackSource_SwitchesTheMutes_PersistsThem_AndARebuildReplaysThem()
    {
        var (mixer, state, audio, ui) = await WithStems();
        Assert.Equal(StemPlaybackSource.Stems, ui.Read(() => mixer.PlaybackSource));

        ui.Post(() => mixer.PlaybackChoice = mixer.PlaybackChoices.Single(c => c.Source == StemPlaybackSource.OriginalMix));

        Assert.False(state.ActiveProject!.OriginalMixMuted);
        Assert.All(state.ActiveProject.Stems, stem => Assert.True(stem.Muted));
        Assert.False(audio.MasterMixMuted);
        Assert.True(ui.Read(() => mixer.PlaysOriginalMix));

        ui.Post(mixer.Rebuild);
        await StemMixerSettle.Async(() => ui.Read(() => !mixer.IsRebuilding && mixer.Tracks.Any(t => t.IsMasterMix)));
        Assert.Equal(StemPlaybackSource.OriginalMix, ui.Read(() => mixer.PlaybackSource));
        Assert.False(audio.MasterMixMuted);

        ui.Post(() => mixer.PlaybackSource = StemPlaybackSource.Stems);
        Assert.True(state.ActiveProject.OriginalMixMuted);
        Assert.True(audio.MasterMixMuted);
        Assert.Contains(state.ActiveProject.Stems, stem => !stem.Muted);
    }

    /// <summary>
    ///     Design D's mini waveform: the strip shows what the timeline shows, heard on the UI dispatcher when the
    ///     coordinator publishes from its worker; the caption says Mixed while it follows the mix, Source otherwise.
    /// </summary>
    [Fact]
    public async Task TheMiniWaveform_FollowsThePublishedWaveform_AndItsCaptionTheMixSwitch()
    {
        var (mixer, state, _, ui) = await WithStems();
        var raised = new List<string?>();
        mixer.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        Assert.Equal("Mixer_Waveform_Source", ui.Read(() => mixer.WaveformCaption));

        var published = DotCharter.Tone.Audio.Abstractions.WaveformSamples.FromBasePeaks([0.5f, 1f], 2.0);
        await Task.Run(() => state.WaveformData = published, TestContext.Current.CancellationToken);
        await StemMixerSettle.Async(() => ui.Read(() => raised.Contains(nameof(StemMixerViewModel.Waveform))));
        Assert.Same(published, ui.Read(() => mixer.Waveform));

        ui.Post(() => mixer.WaveformFollowsMix = true);
        Assert.Equal("Mixer_Waveform_Mixed", ui.Read(() => mixer.WaveformCaption));
        Assert.Contains(nameof(StemMixerViewModel.WaveformCaption), raised);
    }

    private static async Task<(StemMixerViewModel Mixer, ProjectState State, FakeAudioPlaybackService Audio, GatedInlineDispatcher Ui)> WithStems()
    {
        var root = Directory.CreateTempSubdirectory("dotcharter-mixer-sections-").FullName;
        var project = new ChartProject { OriginalMixMuted = true };
        foreach (var name in new[] { "drums", "bass" })
        {
            var path = Path.Combine(root, name + ".wav");
            File.WriteAllBytes(path, [0]);
            project.Stems.Add(new Stem { Name = name, Path = path });
        }

        var state = new ProjectState { ActiveProject = project };
        var audio = new FakeAudioPlaybackService();
        var ui = new GatedInlineDispatcher();
        var mixer = new StemMixerViewModel(state, audio, FakeLocalization.KeysOnly(), ui: ui);
        await StemMixerSettle.Async(() => ui.Read(() => !mixer.IsRebuilding && mixer.HasStems && mixer.Tracks.Any(t => t.IsMasterMix)));
        return (mixer, state, audio, ui);
    }
}
