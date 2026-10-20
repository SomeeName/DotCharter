using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
/// Behavioural coverage for <see cref="StemTrackViewModel"/>: per-track
/// mute/solo exclusion, audio-engine fan-out, dB label formatting and the
/// special-purpose master ("Original") strip.
/// </summary>
public sealed class StemTrackViewModelTests
{
    private static (FakeAudioPlaybackService audio, StemTrackViewModel vm, Stem stem) BuildStem(string name = "vocals")
    {
        var audio = new FakeAudioPlaybackService();
        var stem = new Stem { Name = name, Path = name + ".wav", Volume = 1.0 };
        var vm = new StemTrackViewModel(audio, stem, FakeLocalization.KeysOnly());
        return (audio, vm, stem);
    }

    [Fact]
    public void Stem_constructor_initialises_from_domain_object()
    {
        var (_, vm, stem) = BuildStem("drums");
        Assert.Equal("drums", vm.Name);
        Assert.False(vm.IsMasterMix);
        Assert.Equal(stem.Volume, vm.VolumeLinear);
        Assert.False(vm.IsMuted);
        Assert.False(vm.IsSolo);
    }

    [Fact]
    public void Stem_constructor_clamps_invalid_volumes()
    {
        var audio = new FakeAudioPlaybackService();
        var stem = new Stem { Name = "x", Volume = -1.0 };
        var vm = new StemTrackViewModel(audio, stem, FakeLocalization.KeysOnly());
        Assert.Equal(1.0, vm.VolumeLinear); // negative collapses to default
    }

    [Fact]
    public void Master_constructor_starts_muted()
    {
        var audio = new FakeAudioPlaybackService();
        var master = new StemTrackViewModel(audio, "Original", muted: true, FakeLocalization.KeysOnly());
        Assert.True(master.IsMasterMix);
        Assert.True(master.IsMuted);
        Assert.Equal(1.0, master.VolumeLinear);
    }

    [Fact]
    public void Mute_pushes_state_to_audio_engine_and_domain_object()
    {
        var (audio, vm, stem) = BuildStem();
        vm.IsMuted = true;
        Assert.True(audio.StemMutes[vm.Name]);
        Assert.True(stem.Muted);
    }

    [Fact]
    public void Volume_pushes_clamped_state_to_audio_engine()
    {
        var (audio, vm, stem) = BuildStem();
        vm.VolumeLinear = 10.0; // out of range
        Assert.Equal(4.0, audio.StemVolumes[vm.Name]); // clamped to ceiling
        Assert.Equal(4.0, stem.Volume);
    }

    [Fact]
    public void Master_volume_routes_to_song_volume_not_stem_volume()
    {
        var audio = new FakeAudioPlaybackService();
        _ = new StemTrackViewModel(audio, "Original", muted: true, FakeLocalization.KeysOnly())
        {
            VolumeLinear = 0.5
        };
        Assert.Equal(0.5, audio.SongVolume);
        Assert.False(audio.StemVolumes.ContainsKey("Original"));
    }

    [Fact]
    public void Master_mute_routes_to_master_mix_muted()
    {
        var audio = new FakeAudioPlaybackService();
        var master = new StemTrackViewModel(audio, "Original", muted: true, FakeLocalization.KeysOnly())
        {
            IsMuted = false
        };
        Assert.False(audio.MasterMixMuted);
        master.IsMuted = true;
        Assert.True(audio.MasterMixMuted);
    }

    [Fact]
    public void Master_solo_toggle_is_inert()
    {
        var audio = new FakeAudioPlaybackService();
        _ = new StemTrackViewModel(audio, "Original", muted: true, FakeLocalization.KeysOnly())
        {
            IsSolo = true // should be a no-op for the master strip
        };
        Assert.False(audio.StemSolos.ContainsKey("Original"));
    }

    [Fact]
    public void Soloing_a_muted_stem_clears_the_mute()
    {
        var (audio, vm, stem) = BuildStem();
        vm.IsMuted = true;
        Assert.True(audio.StemMutes[vm.Name]);

        vm.IsSolo = true;

        Assert.False(vm.IsMuted);
        Assert.False(audio.StemMutes[vm.Name]);
        Assert.False(stem.Muted);
        Assert.True(audio.StemSolos[vm.Name]);
    }

    [Fact]
    public void Muting_a_soloed_stem_clears_the_solo()
    {
        var (audio, vm, stem) = BuildStem();
        vm.IsSolo = true;
        Assert.True(audio.StemSolos[vm.Name]);

        vm.IsMuted = true;

        Assert.False(vm.IsSolo);
        Assert.False(audio.StemSolos[vm.Name]);
        Assert.False(stem.Solo);
        Assert.True(audio.StemMutes[vm.Name]);
    }

    [Theory]
    [InlineData(0.0, "-∞ dB")]
    [InlineData(1.0, "+0 dB")]
    [InlineData(0.5, "-6 dB")]      // 20*log10(0.5) ≈ -6.02
    [InlineData(2.0, "+6 dB")]      // 20*log10(2)   ≈  6.02
    public void VolumeDbLabel_formats_common_values(double linear, string expected)
    {
        var (_, vm, _) = BuildStem();
        vm.VolumeLinear = linear;
        // Allow either decimal-separator (de-DE vs en-US) and exact integers.
        var label = vm.VolumeDbLabel.Replace(',', '.');
        Assert.Equal(expected, label);
    }

    [Fact]
    public void SetMutedSilently_updates_state_without_clearing_solo()
    {
        var (audio, vm, _) = BuildStem();
        vm.IsSolo = true;
        vm.SetMutedSilently(true);

        Assert.True(vm.IsMuted);
        Assert.True(vm.IsSolo);                 // exclusion bypassed
        Assert.True(audio.StemMutes[vm.Name]);  // engine still notified
    }

    [Fact]
    public void SetSoloSilently_updates_state_without_clearing_mute()
    {
        var (audio, vm, _) = BuildStem();
        vm.IsMuted = true;
        vm.SetSoloSilently(true);

        Assert.True(vm.IsMuted);                // exclusion bypassed
        Assert.True(vm.IsSolo);
        Assert.True(audio.StemSolos[vm.Name]);
    }

    [Fact]
    public void ApplyToEngine_master_pushes_volume_and_mute()
    {
        var audio = new FakeAudioPlaybackService();
        var master = new StemTrackViewModel(audio, "Original", muted: true, FakeLocalization.KeysOnly());
        master.SetMutedSilently(false);
        master.VolumeLinear = 0.75;

        master.ApplyToEngine();

        Assert.False(audio.MasterMixMuted);
        Assert.Equal(0.75, audio.SongVolume);
    }

    [Fact]
    public void ApplyToEngine_stem_pushes_all_three_channels()
    {
        var (audio, vm, _) = BuildStem("guitar");
        vm.SetMutedSilently(true);
        vm.SetSoloSilently(true);
        vm.VolumeLinear = 0.25;

        vm.ApplyToEngine();

        Assert.Equal(0.25, audio.StemVolumes["guitar"]);
        Assert.True(audio.StemMutes["guitar"]);
        Assert.True(audio.StemSolos["guitar"]);
    }
}
