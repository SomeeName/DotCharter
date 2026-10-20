using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     The third sibling of the solo bug, and it arrives through the mixer's
///     CROSS-TRACK EXCLUSION rather than through the strip that was muted.
///     <para>
///         <c>StemTrackViewModel.SetMutedForRun</c> is careful not to write
///         <c>Stem.Muted</c> — that is what <see cref="StemRunScopedMuteTests" />
///         pins. But un-muting a stem also fired
///         <c>StemMixerViewModel.OnTrackMuteChanged</c>, whose whole job is to
///         silence the OTHER side of the pair, and it does that through
///         <c>SetMutedSilently</c>, which calls <c>PersistOriginalMixMuted</c>. So
///         <c>PlayModeViewModel.RestoreBackingTrackMutes</c> putting the guitar
///         stem back at the end of a run wrote <c>OriginalMixMuted = true</c> into
///         the project — permanently, and saved.
///     </para>
///     <para>
///         The reachable state is a project loaded with the original mix audible
///         AND a stem audible. Nothing re-applies the exclusion at load
///         (<c>BuildStrips</c> reads both flags straight out of the project), so it
///         is perfectly saveable — and one run of "mute my instrument" then took
///         the user's original mix away for good.
///     </para>
/// </summary>
public sealed class StemRunScopedMuteExclusionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "dotcharter-runscoped-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => BestEffortDelete.Directory(_root);

    /// <summary>A whole run of the backing-track mute, start to finish, over the
    /// state that reaches the bug.</summary>
    [Fact]
    public async Task A_backing_track_run_never_persists_the_original_mixs_mute()
    {
        var (project, path, audio, ui, vm) = await MixerWithAudibleOriginalAndStems();

        var guitar = ui.Snapshot(vm).Single(t => t.Name == "guitar");
        var master = ui.Snapshot(vm).Single(t => t.IsMasterMix);

        // ApplyBackingTrackMutes → … → RestoreBackingTrackMutes.
        guitar.SetMutedForRun(true);
        guitar.SetMutedForRun(false);

        Assert.False(ui.Read(() => project.OriginalMixMuted));
        Assert.False(ui.Read(() => master.IsMuted));
        Assert.False(audio.MasterMixMuted);
        _ = path;
    }

    /// <summary>
    ///     …and the exclusion is untouched for a mute the USER makes. Skipping it
    ///     there would let the original mix and the stems play on top of each other,
    ///     which is the doubling the exclusion exists to prevent.
    /// </summary>
    [Fact]
    public async Task An_ordinary_un_mute_still_silences_and_persists_the_other_side()
    {
        var (project, _, audio, ui, vm) = await MixerWithAudibleOriginalAndStems();

        var guitar = ui.Snapshot(vm).Single(t => t.Name == "guitar");
        var master = ui.Snapshot(vm).Single(t => t.IsMasterMix);

        // The user mutes the stem and then un-mutes it deliberately.
        guitar.IsMuted = true;
        guitar.IsMuted = false;

        Assert.True(ui.Read(() => master.IsMuted));
        Assert.True(ui.Read(() => project.OriginalMixMuted));
        Assert.True(audio.MasterMixMuted);
    }

    // ---- fixtures -----------------------------------------------------------

    /// <summary>The saveable state that reaches the bug: the original mix audible
    /// and a stem audible at the same time.</summary>
    private async Task<(ChartProject Project, string Path, FakeAudioPlaybackService Audio,
        GatedInlineDispatcher Ui, StemMixerViewModel Vm)> MixerWithAudibleOriginalAndStems()
    {
        var dir = Path.Combine(_root, "run");
        Directory.CreateDirectory(Path.Combine(dir, "stems"));

        var project = new ChartProject { OriginalMixMuted = false };
        foreach (var name in new[] { "guitar", "drums" })
        {
            File.WriteAllBytes(Path.Combine(dir, "stems", name + ".wav"), [0]);
            project.Stems.Add(new Stem
            {
                Name = name,
                Path = Path.Combine("stems", name + ".wav"),
                Local = true,
                Volume = 1.0,
                Muted = false
            });
        }

        var path = Path.Combine(dir, "song.rscp");
        var audio = new FakeAudioPlaybackService();
        var ui = new GatedInlineDispatcher();
        var state = new ProjectState { CurrentProjectPath = path, ActiveProject = project };
        var vm = new StemMixerViewModel(state, audio, FakeLocalization.KeysOnly(), ui: ui);

        // Settle on the STRIPS, never on a count — BuildStrips inserts the master
        // last, so `Tracks.Count > 1` goes true before it exists.
        await SettleAsync(() =>
        {
            var tracks = ui.Snapshot(vm);
            return tracks.Any(t => t.IsMasterMix) && tracks.Any(t => t.Name == "guitar");
        });

        return (project, path, audio, ui, vm);
    }

    private static async Task SettleAsync(Func<bool> until)
    {
        for (var i = 0; i < 400 && !until(); i++) await Task.Delay(5);
        Assert.True(until(), "the mixer never finished its rebuild");
        for (var i = 0; i < 40; i++) await Task.Yield();
    }
}
