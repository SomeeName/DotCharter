using DotCharter.Tone.Audio.Abstractions;
using DotCharter.Services.Ai.Match;
using DotCharter.Services.Audio.SilentTransport;
using DotCharter.Services.Session;
using DotCharter.Tone.Audio.Dsp.Presets;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.UI.ViewModels.PlayMode;
using DotCharter.UI.ViewModels.PlayMode.ToneSections;

namespace DotCharter.Tests.Services.Session;

public sealed partial class ProjectSessionAttachAudioTests
{
    private sealed class SectionAudioPlaybackService : FakeAudioPlaybackService, IAudioPlaybackService
    {
        public SectionAudioPlaybackService()
        {
            TotalAudioLengthMs = 1000;
            WaveformResult = WaveformSamples.FromBasePeaks([0.5f], 2);
        }

        public string CurrentAudioPath => CompletedLoadPath ?? "";
    }

    private static ToneDesignViewModel SectionPanel(Harness h, Action? applied = null, Action? saved = null)
    {
        var rig = new DcRigPreset([]);
        var preset = new DspTonePreset("s", "Section", DspToneCategory.Clean, "", rig, DspToneOrigin.User);
        var panel = new ToneDesignViewModel(static () => null, _ => applied?.Invoke(), () => preset,
            (_, _, _, _) =>
            {
                saved?.Invoke();
                return preset;
            }, FakeLocalization.English(), static id => id);
        var host = PlayModeToneRigViewModel.Detached(h.State, h.Transport, null, h.Audio);
        host.UseRigForTests(TestRigBuilders.Pedalboard(new FakeDspChainService(),
            new FakeDspTonePresetService(), FakeLocalization.English(), toneDesignPanel: _ => panel));
        Assert.NotNull(host.AmpFxRig);
        return panel;
    }

    private static ToneSectionRowViewModel AddChosenSection(ToneDesignViewModel panel)
    {
        var row = panel.Sections.AddForTests("Clean", ToneStems.Guitar, TimeSpan.Zero, TimeSpan.FromSeconds(20));
        row.PublishCandidates(new ToneMatchResult(
            [new ToneMatchCandidate(new DcRigPreset([]), 0.2, "test")], 0.2, TimeSpan.Zero, []), panel);
        Assert.True(panel.HasResult);
        return row;
    }

    [Fact]
    public async Task Generated_silent_transport_does_not_make_first_attach_a_song_replacement()
    {
        var (h, dir) = CreateAudiolessProject(audio: new SectionAudioPlaybackService());
        var silent = new SilentTransportSession(h.Audio, h.State, h.Transport, h.Logger,
            new SilentAudioTrack(h.Logger, Path.Combine(dir, "silence")), h.History);
        await silent.LoadAsync(20_000);
        Assert.EndsWith("silence-30s.wav", h.Audio.CompletedLoadPath);
        using var panel = SectionPanel(h);
        var row = AddChosenSection(panel);

        Assert.True(await h.Session.AttachAudioAsync(CreateSourceAudio("first.wav")));

        Assert.Same(row, Assert.Single(panel.Sections.Sections));
        Assert.Single(row.Candidates);
        Assert.True(panel.HasResult);
    }

    [Fact]
    public async Task Same_path_replacement_clears_sections_even_when_clear_arrives_after_load()
    {
        var ui = new QueuedAudioDispatcher();
        var dir = Directory.CreateDirectory(Path.Combine(_tempDir, "same-path")).FullName;
        var h = new Harness(new ChartProject { AudioFileName = "" }, Path.Combine(dir, "project.rscp"), uiDispatcher: ui, audio: new SectionAudioPlaybackService());
        Assert.True(await h.Session.AttachAudioAsync(CreateSourceAudio("a.wav")));
        ui.Drain();
        using var panel = SectionPanel(h);
        var row = AddChosenSection(panel);
        var path = h.Audio.CompletedLoadPath;
        Assert.True(await h.Session.AttachAudioAsync(CreateSourceAudio("b.wav")));
        Assert.Equal(path, h.Audio.CompletedLoadPath);
        // Deliver the loaded notification first, then the delayed background clear.
        h.State.NotifyAudioLoaded();
        ui.Drain();

        Assert.Empty(panel.Sections.Sections);
        Assert.Empty(row.Candidates);
        Assert.False(panel.HasResult);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Same_song_renotification_or_reload_keeps_sections_and_result(bool reload)
    {
        var (h, _) = CreateAudiolessProject(audio: new SectionAudioPlaybackService());
        Assert.True(await h.Session.AttachAudioAsync(CreateSourceAudio("a.wav")));
        using var panel = SectionPanel(h);
        var row = AddChosenSection(panel);

        if (reload)
        {
            await h.Session.ReloadProjectAudioAsync();
        }
        else
        {
            h.State.NotifyAudioCleared();
            h.State.NotifyAudioLoaded();
        }

        Assert.Same(row, Assert.Single(panel.Sections.Sections));
        Assert.Single(row.Candidates);
        Assert.True(panel.HasResult);
    }
    [Fact]
    public async Task Auditioning_transport_media_does_not_replace_the_logical_song_owner()
    {
        var (h, _) = CreateAudiolessProject(audio: new SectionAudioPlaybackService());
        Assert.True(await h.Session.AttachAudioAsync(CreateSourceAudio("song.wav")));
        using var panel = SectionPanel(h);
        var row = AddChosenSection(panel);
        await h.Session.LoadAudioFile(CreateSourceAudio("preview.wav"), false);
        Assert.Equal("song.wav", h.State.ActiveProject!.AudioFileName);
        Assert.EndsWith("preview.wav", h.Audio.CompletedLoadPath);
        Assert.Same(row, Assert.Single(panel.Sections.Sections));
        Assert.True(panel.HasResult);
    }

    [Fact]
    public async Task Logical_audio_detachment_clears_sections_while_transport_still_has_old_media()
    {
        var (h, _) = CreateAudiolessProject(audio: new SectionAudioPlaybackService());
        Assert.True(await h.Session.AttachAudioAsync(CreateSourceAudio("song.wav")));
        using var panel = SectionPanel(h);
        var row = AddChosenSection(panel);
        h.State.CommitSongAudio(h.State.ActiveProject!, "");
        h.State.NotifyAudioCleared();
        Assert.NotNull(h.Audio.CompletedLoadPath);
        Assert.Empty(panel.Sections.Sections);
        Assert.Empty(row.Candidates);
        Assert.False(panel.HasResult);
    }

}
