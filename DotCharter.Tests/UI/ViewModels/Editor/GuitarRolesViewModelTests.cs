using System.Diagnostics;
using DotCharter.Services.Audio.StemSeparation.GuitarRoles;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     The Guitar roles switch: it is live only for an image that can be split,
///     disabled with its reason otherwise, never hands back the same guitar twice,
///     and a project that wants the split gets it derived again when the files went
///     missing — once.
/// </summary>
public sealed class GuitarRolesViewModelTests
{
    private const string Guitar = @"C:\song\stems\guitar.wav";

    /// <summary>
    ///     The view model posts its worker's answer through this dispatcher, and the
    ///     inline fake runs that post on the worker's POOL thread. The answer is one
    ///     delegate (<c>IsWorking</c> off, then the verdict, the status, the rebuild),
    ///     so every read that may race it goes through the same gate (#528).
    /// </summary>
    private readonly GatedInlineDispatcher _ui = new();

    [Fact]
    public async Task NoGuitarStem_ShowsTheSwitchDisabled_WithItsReason()
    {
        var (vm, _, _) = Build();

        vm.Update(new ChartProject(), hasStems: true, guitarPath: null, rolesActive: false);
        await Settle(vm);

        Assert.True(vm.IsShown);
        Assert.False(vm.CanToggle);
        Assert.Equal("Mixer_GuitarRoles_NoGuitar", vm.Status);
    }

    [Fact]
    public async Task NoStems_HidesTheSwitch()
    {
        var (vm, _, _) = Build();

        vm.Update(new ChartProject(), hasStems: false, guitarPath: null, rolesActive: false);
        await Settle(vm);

        Assert.False(vm.IsShown);
    }

    [Theory]
    [InlineData(GuitarRoleVerdict.Mono, "Mixer_GuitarRoles_Mono")]
    [InlineData(GuitarRoleVerdict.Silent, "Mixer_GuitarRoles_Silent")]
    [InlineData(GuitarRoleVerdict.AllCentred, "Mixer_GuitarRoles_AllCentred")]
    [InlineData(GuitarRoleVerdict.NothingCentred, "Mixer_GuitarRoles_NothingCentred")]
    [InlineData(GuitarRoleVerdict.Unreadable, "Mixer_GuitarRoles_Unreadable")]
    public async Task AnImageThatCannotBeSplit_NeverLooksLive(GuitarRoleVerdict verdict, string reasonKey)
    {
        var (vm, stems, _) = Build();
        stems.Verdict = verdict;

        vm.Update(new ChartProject(), true, Guitar, false);
        await Settle(vm);

        Assert.False(vm.CanToggle);
        Assert.False(vm.IsOn);
        Assert.Equal(reasonKey, vm.Status);
    }

    [Fact]
    public async Task APlausibleImage_EnablesTheSwitch()
    {
        var (vm, stems, _) = Build();
        stems.AssessGate = new TaskCompletionSource();

        vm.Update(new ChartProject(), true, Guitar, false);
        // Not before the check has answered: the check is held, so this read cannot
        // race its answer.
        Assert.False(_ui.Read(() => vm.CanToggle));
        stems.AssessGate.SetResult();
        await Settle(vm);

        Assert.True(vm.CanToggle);
        Assert.False(vm.IsOn);
        Assert.Equal(string.Empty, vm.Status);
    }

    [Fact]
    public async Task TurningItOn_SplitsSetsTheProjectFlagAndRebuildsTheMixer()
    {
        var (vm, stems, rebuilds) = Build();
        var project = new ChartProject();
        vm.Update(project, true, Guitar, false);
        await Settle(vm);

        vm.IsOn = true;
        await Settle(vm);

        Assert.Equal(1, stems.Derives);
        Assert.True(project.GuitarRolesSplit);
        Assert.Single(rebuilds);
        Assert.Equal("Mixer_GuitarRoles_ResultFmt(40%,60%)", string.Concat(vm.Status.Where(c => !char.IsWhiteSpace(c))));

        // The mixer's rebuild then reports the pair as playing.
        vm.Update(project, true, Guitar, rolesActive: true);
        Assert.True(vm.IsOn);
        Assert.True(vm.CanToggle);
    }

    [Fact]
    public async Task ASplitThatFindsNothingToSplit_IsRefused_AndTheSwitchGoesBackOff()
    {
        var (vm, stems, rebuilds) = Build();
        var project = new ChartProject();
        vm.Update(project, true, Guitar, false);
        await Settle(vm);

        stems.DeriveVerdict = GuitarRoleVerdict.AllCentred;
        vm.IsOn = true;
        await Settle(vm);

        Assert.False(vm.IsOn);
        Assert.False(vm.CanToggle);
        Assert.False(project.GuitarRolesSplit);
        Assert.Empty(rebuilds);
        Assert.Equal("Mixer_GuitarRoles_AllCentred", vm.Status);
    }

    [Fact]
    public async Task TurningItOff_ClearsTheFlagAndRebuilds()
    {
        var (vm, _, rebuilds) = Build();
        var project = new ChartProject { GuitarRolesSplit = true };
        vm.Update(project, true, Guitar, rolesActive: true);
        await Settle(vm);

        vm.IsOn = false;

        Assert.False(project.GuitarRolesSplit);
        Assert.Single(rebuilds);
    }

    [Fact]
    public async Task AProjectThatWantsTheSplit_GetsMissingFilesDerivedAgain_Once()
    {
        var (vm, stems, rebuilds) = Build();
        var project = new ChartProject { GuitarRolesSplit = true };

        // Opened with the flag on but no pair on disk (a re-separation, a cleaned folder).
        vm.Update(project, true, Guitar, rolesActive: false);
        await Settle(vm);
        Assert.Equal(1, stems.Derives);
        Assert.Single(rebuilds);

        // The rebuild STILL finds no pair: the files cannot be kept. No second split.
        vm.Update(project, true, Guitar, rolesActive: false);
        await Settle(vm);
        Assert.Equal(1, stems.Derives);
        Assert.False(project.GuitarRolesSplit);
        Assert.False(vm.IsOn);
        Assert.Equal("Mixer_GuitarRoles_Unreadable", vm.Status);
    }

    [Fact]
    public async Task AResultForAProjectThatIsNoLongerOpen_IsDropped()
    {
        var (vm, stems, rebuilds) = Build();
        var first = new ChartProject();
        vm.Update(first, true, Guitar, false);
        await Settle(vm);

        stems.DeriveGate = new TaskCompletionSource();
        vm.IsOn = true;
        var second = new ChartProject();
        vm.Update(second, true, @"C:\other\stems\guitar.wav", false);
        stems.DeriveGate.SetResult();
        var clock = Stopwatch.StartNew();
        while (stems.Derives == 0)
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(60), "The split never ran (deadlock detector).");
            await Task.Delay(1, TestContext.Current.CancellationToken);
        }

        await Settle(vm);

        Assert.False(first.GuitarRolesSplit);
        Assert.Empty(rebuilds);
    }

    /// <summary>
    ///     Waits until the check or split has APPLIED its answer. <c>IsWorking</c> goes
    ///     false on the first line of that apply and the verdict lands after it, on the
    ///     worker's thread: read ungated, a test saw <c>IsWorking</c> off, ran a fixed
    ///     number of yields and then read the "Checking" status the verdict had not yet
    ///     replaced (#528). Read under the gate the apply holds, <c>false</c> means the
    ///     whole apply is done — no yield count.
    /// </summary>
    private async Task Settle(GuitarRolesViewModel vm)
    {
        var clock = Stopwatch.StartNew();
        while (_ui.Read(() => vm.IsWorking))
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(60), "The check or split never finished (deadlock detector).");
            await Task.Delay(1, TestContext.Current.CancellationToken);
        }
    }

    private (GuitarRolesViewModel Vm, FakeGuitarRoleStems Stems, List<int> Rebuilds) Build()
    {
        var stems = new FakeGuitarRoleStems();
        var rebuilds = new List<int>();
        var vm = new GuitarRolesViewModel(stems, FakeLocalization.KeysOnly(), _ui,
            () => rebuilds.Add(rebuilds.Count));
        return (vm, stems, rebuilds);
    }

    internal sealed class FakeGuitarRoleStems : IGuitarRoleStems
    {
        public GuitarRoleVerdict Verdict { get; set; } = GuitarRoleVerdict.Plausible;
        public GuitarRoleVerdict DeriveVerdict { get; set; } = GuitarRoleVerdict.Splittable;
        public GuitarRolePair? Derived { get; set; }
        public TaskCompletionSource? DeriveGate { get; set; }
        public int Derives { get; private set; }

        public TaskCompletionSource? AssessGate { get; set; }
        public int Assessed;

        /// <summary>A held gate that is never released is a deadlock, and it must not
        /// expire silently into the default answer: throw, so the view model reports
        /// the work as failed.</summary>
        private static void AwaitGate(TaskCompletionSource? gate)
        {
            if (gate != null && !gate.Task.Wait(TimeSpan.FromSeconds(60)))
                throw new TimeoutException("The test never released the gate (deadlock detector).");
        }

        public GuitarRoleAssessment Assess(string guitarStemPath, CancellationToken ct = default)
        {
            AwaitGate(AssessGate);
            Interlocked.Increment(ref Assessed);
            return new GuitarRoleAssessment(Verdict, new StereoImageStats(0.3, 0.3, -20, 1000), null);
        }

        public GuitarRolePair? TryGetDerived(string guitarStemPath) => Derived;

        public GuitarRoleDerivation Derive(string guitarStemPath, IProgress<double>? progress = null,
            CancellationToken ct = default)
        {
            AwaitGate(DeriveGate);
            Derives++;
            progress?.Report(0.5);
            var assessment = new GuitarRoleAssessment(DeriveVerdict, new StereoImageStats(0.3, 0.3, -20, 1000), 0.4);
            return DeriveVerdict == GuitarRoleVerdict.Splittable
                ? new GuitarRoleDerivation(assessment, new GuitarRolePair(guitarStemPath + ".lead", guitarStemPath + ".rhythm"))
                : new GuitarRoleDerivation(assessment, null);
        }
    }
}
