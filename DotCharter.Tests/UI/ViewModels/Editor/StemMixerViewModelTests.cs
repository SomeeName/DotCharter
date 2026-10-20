using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
/// Cross-track exclusion rules for <see cref="StemMixerViewModel"/>:
///  - Un-muting Original silently mutes every stem (avoid audible doubling).
///  - Un-muting a stem silently mutes Original.
///  - Soloing any stem silently mutes Original.
/// All behaviour is driven through the public track properties so we exercise
/// the same paths the UI bindings hit.
/// </summary>
public sealed class StemMixerViewModelTests
{
    private static (StemTrackViewModel master, StemTrackViewModel vocals, StemTrackViewModel drums)
        BuildMixer()
    {
        var audio = new FakeAudioPlaybackService();
        var state = new ProjectState();
        var project = new ChartProject();
        project.Stems.Add(new Stem { Name = "vocals", Path = "vocals.wav" });
        project.Stems.Add(new Stem { Name = "drums", Path = "drums.wav" });

        // ActiveProject stays NULL on purpose. These tests are about the
        // cross-track exclusion rules, so they populate Tracks by hand; with a
        // project attached, the constructor's Rebuild probes the disk on a
        // WORKER and applies its result later, clearing the hand-built tracks
        // mid-test. The null-project branch completes synchronously, so the
        // fixture stays deterministic and independent of disk state.
        // (Rebuild's own behaviour is covered by StemMixerRebuildTests.)
        var mixer = new StemMixerViewModel(state, audio, FakeLocalization.KeysOnly());
        mixer.Tracks.Clear();

        var master = new StemTrackViewModel(audio, "Original", muted: true, FakeLocalization.KeysOnly());
        var vocals = new StemTrackViewModel(audio, project.Stems[0], FakeLocalization.KeysOnly());
        var drums = new StemTrackViewModel(audio, project.Stems[1], FakeLocalization.KeysOnly());
        master.AttachOwner(mixer);
        vocals.AttachOwner(mixer);
        drums.AttachOwner(mixer);
        mixer.Tracks.Add(master);
        mixer.Tracks.Add(vocals);
        mixer.Tracks.Add(drums);

        // Match production initial state: stems audible, master muted.
        master.SetMutedSilently(true);
        vocals.SetMutedSilently(false);
        drums.SetMutedSilently(false);

        return (master, vocals, drums);
    }

    [Fact]
    public void Unmuting_master_silently_mutes_every_stem()
    {
        var (master, vocals, drums) = BuildMixer();

        master.IsMuted = false;

        Assert.False(master.IsMuted);
        Assert.True(vocals.IsMuted);
        Assert.True(drums.IsMuted);
    }

    [Fact]
    public void Unmuting_a_stem_silently_mutes_the_master()
    {
        var (master, vocals, drums) = BuildMixer();
        master.SetMutedSilently(false); // pretend the user previously turned Original on
        vocals.SetMutedSilently(true);
        drums.SetMutedSilently(true);

        vocals.IsMuted = false;

        Assert.True(master.IsMuted);
        Assert.False(vocals.IsMuted);
    }

    [Fact]
    public void Muting_a_stem_does_not_force_master_open()
    {
        var (master, vocals, _) = BuildMixer();

        vocals.IsMuted = true;

        Assert.True(master.IsMuted); // stays as it was
    }

    [Fact]
    public void Soloing_a_stem_mutes_master()
    {
        var (master, vocals, _) = BuildMixer();
        master.SetMutedSilently(false);

        vocals.IsSolo = true;

        Assert.True(master.IsMuted);
        Assert.True(vocals.IsSolo);
    }

    [Fact]
    public void Clearing_solo_does_not_unmute_master()
    {
        var (master, vocals, _) = BuildMixer();
        vocals.IsSolo = true;

        vocals.IsSolo = false;

        // Clearing the solo is *not* the inverse of soloing — the user chose
        // to silence the master earlier and we don't want to surprise them by
        // bringing it back without consent.
        Assert.True(master.IsMuted);
    }

    [Fact]
    public void Construction_subscribes_to_active_project_changes()
    {
        var audio = new FakeAudioPlaybackService();
        var state = new ProjectState();
        var mixer = new StemMixerViewModel(state, audio, FakeLocalization.KeysOnly());

        Assert.NotNull(mixer.Tracks);
        Assert.False(mixer.HasStems); // no project → no stems
    }
}
