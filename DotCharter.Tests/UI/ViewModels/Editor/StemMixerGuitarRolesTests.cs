using DotCharter.Services.Audio.StemSeparation.GuitarRoles;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     The mixer side of Guitar roles: with the project's flag on and a current
///     derived pair, the guitar stem plays as Lead + Rhythm IN ITS PLACE (never
///     beside it, which would double the guitar), and the project's own stem list
///     is never touched.
/// </summary>
public sealed class StemMixerGuitarRolesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dotcharter-guitarroles-mixer-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => BestEffortDelete.Directory(_root);

    [Fact]
    public async Task WithTheFlagOnAndAPair_TheGuitarPlaysAsLeadAndRhythm_InItsPlace()
    {
        var (project, path) = Project("drums", "guitar", "bass");
        project.GuitarRolesSplit = true;
        var roles = new GuitarRolesViewModelTests.FakeGuitarRoleStems
        {
            Derived = new GuitarRolePair(@"C:\x\lead.wav", @"C:\x\rhythm.wav"),
        };
        var (vm, audio, ui) = Mixer(project, path, roles);

        await StemMixerSettle.Async(() => audio.StemLoadCalls.Count >= 1 && ui.Read(() => vm.GuitarRoles!.IsOn));

        Assert.Equal(["drums", GuitarRoleStems.LeadName, GuitarRoleStems.RhythmName, "bass"], audio.StemLoadCalls[^1]);
        var names = ui.Snapshot(vm).Select(t => t.Name).ToArray();
        Assert.Equal(["Original", "drums", GuitarRoleStems.LeadName, GuitarRoleStems.RhythmName, "bass"], names);
        // The project still lists exactly what the separation produced.
        Assert.Equal(["drums", "guitar", "bass"], project.Stems.Select(s => s.Name));
    }

    [Fact]
    public async Task WithTheFlagOff_TheGuitarStemPlays_AndTheSwitchIsOffered()
    {
        var (project, path) = Project("drums", "guitar");
        var roles = new GuitarRolesViewModelTests.FakeGuitarRoleStems
        {
            Derived = new GuitarRolePair(@"C:\x\lead.wav", @"C:\x\rhythm.wav"),
        };
        var (vm, audio, ui) = Mixer(project, path, roles);

        await StemMixerSettle.Async(() => audio.StemLoadCalls.Count >= 1 && ui.Read(() => vm.GuitarRoles!.CanToggle));

        Assert.Equal(["drums", "guitar"], audio.StemLoadCalls[^1]);
        Assert.False(ui.Read(() => vm.GuitarRoles!.IsOn));
    }

    [Fact]
    public async Task TheDerivedStrips_StartFromTheGuitarsOwnFaderAndMute()
    {
        var (project, path) = Project("guitar");
        project.Stems[0].Volume = 0.5;
        project.Stems[0].Muted = true;
        project.GuitarRolesSplit = true;
        var roles = new GuitarRolesViewModelTests.FakeGuitarRoleStems
        {
            Derived = new GuitarRolePair(@"C:\x\lead.wav", @"C:\x\rhythm.wav"),
        };
        var (vm, audio, ui) = Mixer(project, path, roles);

        await StemMixerSettle.Async(() => audio.StemLoadCalls.Count >= 1 && ui.Snapshot(vm).Any(t => t.Name == GuitarRoleStems.LeadName));

        var lead = ui.Snapshot(vm).Single(t => t.Name == GuitarRoleStems.LeadName);
        Assert.Equal(0.5, ui.Read(() => lead.VolumeLinear), 6);
        Assert.True(ui.Read(() => lead.IsMuted));
    }

    [Fact]
    public async Task NoGuitarStem_OffersNoSplit()
    {
        var (project, path) = Project("drums", "bass", "other", "vocals");
        var (vm, audio, ui) = Mixer(project, path, new GuitarRolesViewModelTests.FakeGuitarRoleStems());

        await StemMixerSettle.Async(() => audio.StemLoadCalls.Count >= 1 && ui.Read(() => vm.GuitarRoles!.Status.Length > 0));

        Assert.False(ui.Read(() => vm.GuitarRoles!.CanToggle));
        Assert.Equal("Mixer_GuitarRoles_NoGuitar", ui.Read(() => vm.GuitarRoles!.Status));
    }

    [Fact]
    public async Task ACheckThatFinishesAfterTheMixerIsDisposed_DispatchesNothing()
    {
        // #263's rule for the mixer's own rebuild holds for Guitar roles too: work
        // finishing on a pool thread after Dispose may not reach a dispatcher that
        // can belong to the next app.
        var (project, path) = Project("guitar");
        var roles = new GuitarRolesViewModelTests.FakeGuitarRoleStems { AssessGate = new TaskCompletionSource() };
        var (vm, audio, ui) = Mixer(project, path, roles);
        await StemMixerSettle.Async(() => audio.StemLoadCalls.Count >= 1 && ui.Read(() => vm.GuitarRoles!.IsWorking));

        vm.Dispose();
        roles.AssessGate.SetResult();
        await StemMixerSettle.Async(() => Volatile.Read(ref roles.Assessed) == 1);
        for (var i = 0; i < 20; i++) await Task.Delay(1, TestContext.Current.CancellationToken);

        Assert.True(ui.Read(() => vm.GuitarRoles!.IsWorking), "the check's result was dispatched after Dispose");
        Assert.False(ui.Read(() => vm.GuitarRoles!.CanToggle));
    }

    private static (StemMixerViewModel Vm, FakeAudioPlaybackService Audio, GatedInlineDispatcher Ui) Mixer(
        ChartProject project, string path, IGuitarRoleStems roles)
    {
        var audio = new FakeAudioPlaybackService();
        var state = new ProjectState();
        var ui = new GatedInlineDispatcher();
        var vm = new StemMixerViewModel(state, audio, FakeLocalization.KeysOnly(), ui: ui, guitarRoleStems: roles);
        state.CurrentProjectPath = path;
        state.ActiveProject = project;
        return (vm, audio, ui);
    }

    private (ChartProject Project, string ProjectPath) Project(params string[] stemNames)
    {
        var dir = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "stems"));
        var project = new ChartProject();
        foreach (var name in stemNames)
        {
            File.WriteAllBytes(Path.Combine(dir, "stems", name + ".wav"), [0]);
            project.Stems.Add(new Stem { Name = name, Path = Path.Combine("stems", name + ".wav"), Volume = 1.0 });
        }

        return (project, Path.Combine(dir, "song.rscp"));
    }

}
