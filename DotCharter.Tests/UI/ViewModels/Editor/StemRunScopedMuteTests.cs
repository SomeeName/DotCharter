using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     Run-scoped mutes (the guitarless backing mix). The whole point of
///     <see cref="StemTrackViewModel.SetMutedForRun" /> is that it must NOT touch the
///     domain <c>Stem.Muted</c> flag: that field is serialized into the .rscp, so a
///     gameplay mute that reached it would survive a crash mid-run and leave the
///     player's guitar stem permanently muted in their saved project.
/// </summary>
public sealed class StemRunScopedMuteTests
{
    private static (FakeAudioPlaybackService Audio, StemTrackViewModel Vm, Stem Stem) Build(string name = "guitar")
    {
        var audio = new FakeAudioPlaybackService();
        var stem = new Stem { Name = name, Path = name + ".wav", Volume = 1.0 };
        return (audio, new StemTrackViewModel(audio, stem, FakeLocalization.KeysOnly()), stem);
    }

    [Fact]
    public void A_run_scoped_mute_silences_the_audio_engine()
    {
        var (audio, vm, _) = Build();

        vm.SetMutedForRun(true);

        Assert.True(vm.IsMuted);
        Assert.True(audio.StemMutes["guitar"]);
    }

    [Fact]
    public void A_run_scoped_mute_does_not_write_the_project()
    {
        // THE regression this type exists for.
        var (_, vm, stem) = Build();

        vm.SetMutedForRun(true);

        Assert.True(vm.IsMuted);
        Assert.False(stem.Muted); // <- must stay false: it would be saved into the .rscp
    }

    [Fact]
    public void An_ordinary_mute_still_writes_the_project()
    {
        // The editor's mixer must keep persisting deliberate mutes — only the
        // run-scoped path is exempt.
        var (_, vm, stem) = Build();

        vm.IsMuted = true;

        Assert.True(stem.Muted);
    }

    [Fact]
    public void Releasing_a_run_scoped_mute_leaves_the_project_untouched()
    {
        var (audio, vm, stem) = Build();

        vm.SetMutedForRun(true);
        vm.SetMutedForRun(false);

        Assert.False(vm.IsMuted);
        Assert.False(audio.StemMutes["guitar"]);
        Assert.False(stem.Muted);
    }

    [Fact]
    public void A_run_scoped_mute_over_a_deliberately_muted_stem_does_not_clear_the_saved_mute()
    {
        // The player muted this stem themselves and saved it that way. A run must be
        // able to release its own mute without silently un-muting theirs in the file.
        var (_, vm, stem) = Build();
        vm.IsMuted = true;
        Assert.True(stem.Muted);

        vm.SetMutedForRun(false);

        Assert.False(vm.IsMuted);
        Assert.True(stem.Muted); // the saved intent survives
    }

    [Fact]
    public void A_run_scoped_mute_clears_the_solo_in_audio_but_not_in_the_project()
    {
        var (audio, vm, stem) = Build();
        vm.IsSolo = true;
        Assert.True(stem.Solo);

        vm.SetMutedForRun(true);

        Assert.False(vm.IsSolo);              // mute and solo are exclusive
        Assert.False(audio.StemSolos["guitar"]);
        Assert.True(stem.Solo);               // but the project keeps the player's solo
    }

    [Fact]
    public void Restoring_puts_the_solo_back_in_step_with_the_project()
    {
        var (audio, vm, stem) = Build();
        vm.IsSolo = true;

        vm.SetMutedForRun(true);
        vm.SetMutedForRun(false);
        vm.RestoreSoloFromProject();

        Assert.True(vm.IsSolo);
        Assert.True(audio.StemSolos["guitar"]);
        Assert.True(stem.Solo);
    }
}
