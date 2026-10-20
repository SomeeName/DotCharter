using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Services.Infrastructure.LibraryDoctor;
using DotCharter.Services.Library;

namespace DotCharter.Tests.Presentation.Dialogs;

/// <summary>
///     The Library Doctor starts its check as it opens, groups the findings by
///     kind with the severity the view colours by, and Close or Esc stops a
///     check still running — it never lingers past its dialog.
/// </summary>
public sealed class LibraryDoctorViewModelTests
{
    private static readonly LibrarySongEntry Song = new() { SongKey = "Hysteria", Title = "Hysteria", PackagePath = @"D:\dlc\hysteria_p.psarc" };

    private static LibraryDoctorViewModel Open(ILibraryDoctorService doctor)
    {
        var vm = new LibraryDoctorViewModel(doctor, FakeLocalization.English(), new FakeAppLogger());
        vm.Configure(new LibraryDoctorRequest([Song]));
        return vm;
    }

    [Fact]
    public async Task It_checks_the_songs_it_was_opened_on_and_groups_the_findings()
    {
        var doctor = new ScriptedDoctor(new LibraryDoctorReport
        {
            Issues =
            [
                new LibraryDoctorIssue(LibraryIssueKind.DuplicatePackage, LibraryIssueSeverity.Warning, "Muse — Hysteria",
                    @"D:\dlc\hysteria_p.psarc", "Also in hysteria_v2_p.psarc")
            ],
            Groups =
            [
                new LibraryIssueGroup(LibraryIssueKind.DuplicatePackage, LibraryIssueSeverity.Warning,
                [
                    new LibraryDoctorIssue(LibraryIssueKind.DuplicatePackage, LibraryIssueSeverity.Warning,
                        "Muse — Hysteria", @"D:\dlc\hysteria_p.psarc", "Also in hysteria_v2_p.psarc")
                ])
            ],
            SongsScanned = 1,
            PackagesScanned = 1
        });

        var vm = Open(doctor);
        await vm.Scan;

        Assert.Same(Song, Assert.Single(doctor.Songs!));
        var group = Assert.Single(vm.Groups);
        Assert.Equal(FakeLocalization.English()["LibraryDoctor_Kind_Duplicate"], group.Title);
        Assert.Equal((1, true, false), (group.Count, group.IsWarning, group.IsError));
        var issue = Assert.Single(group.Issues);
        Assert.Equal(("Muse — Hysteria", "hysteria_p.psarc"), (issue.SongDisplay, issue.FileName));
        Assert.True(vm.HasIssues);
        Assert.False(vm.IsRunning);
        Assert.Equal(FakeLocalization.English().Format("LibraryDoctor_Summary", 1, 1, 1), vm.Summary);
    }

    [Fact]
    public async Task A_healthy_library_says_so_and_Run_checks_again()
    {
        var doctor = new ScriptedDoctor(new LibraryDoctorReport { SongsScanned = 3 });
        var vm = Open(doctor);
        await vm.Scan;

        Assert.False(vm.HasIssues);
        Assert.Equal(FakeLocalization.English().Format("LibraryDoctor_Healthy", 3), vm.Summary);

        vm.RunCommand.Execute(null);
        await doctor.LastRun;
        Assert.Equal(2, doctor.Runs);
    }

    [Fact]
    public async Task Close_while_checking_stops_the_check_and_answers_true()
    {
        var doctor = new ScriptedDoctor(null);
        var vm = Open(doctor);
        Assert.True(vm.IsRunning);
        Assert.False(vm.RunCommand.CanExecute(null));

        vm.CloseCommand.Execute(null);
        await vm.Scan;

        Assert.True(doctor.Token.IsCancellationRequested);
        Assert.True(ChartEditHarness.Answer(vm.Result));
        Assert.Equal(FakeLocalization.English()["LibraryDoctor_Cancelled"], vm.Summary);
        Assert.False(vm.IsRunning);
    }

    [Fact]
    public async Task Esc_stops_the_check_too_and_answers_false()
    {
        var doctor = new ScriptedDoctor(null);
        var vm = Open(doctor);

        Assert.True(vm.TryCancel());
        await vm.Scan;

        Assert.True(doctor.Token.IsCancellationRequested);
        Assert.False(ChartEditHarness.Answer(vm.Result));
    }

    [Fact]
    public async Task Conforms_to_its_contract()
    {
        var vm = Open(new ScriptedDoctor(new LibraryDoctorReport()));
        await vm.Scan;
        Assert.Empty(ContractConformanceTests.Violations(typeof(ILibraryDoctorScreen), vm));
    }

    /// <summary>Answers <c>report</c> at once, or — when it is null — waits for the token and throws.</summary>
    private sealed class ScriptedDoctor(LibraryDoctorReport? report) : ILibraryDoctorService
    {
        public IReadOnlyList<LibrarySongEntry>? Songs { get; private set; }

        public CancellationToken Token { get; private set; }

        public int Runs { get; private set; }

        public Task LastRun { get; private set; } = Task.CompletedTask;

        public Task<LibraryDoctorReport> RunAsync(IReadOnlyList<LibrarySongEntry> songs,
            IProgress<LibraryDoctorProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            Runs++;
            Songs = songs;
            Token = cancellationToken;
            var run = report != null ? Task.FromResult(report) : WaitForCancel(cancellationToken);
            LastRun = run;
            return run;
        }

        private static async Task<LibraryDoctorReport> WaitForCancel(CancellationToken token)
        {
            await Task.Delay(Timeout.Infinite, token);
            return LibraryDoctorReport.Empty;
        }
    }
}
