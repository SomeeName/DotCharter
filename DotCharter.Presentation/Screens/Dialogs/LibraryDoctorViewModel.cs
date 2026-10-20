using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Services.Infrastructure.LibraryDoctor;
using DotCharter.Services.Library;
using DotCharter.Tone.Infrastructure.Configuration;
using DotCharter.Tone.Logging;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>The Library Doctor. One instance per open: the check starts when it is configured.</summary>
public sealed class LibraryDoctorViewModel : DialogViewModel<bool>, ILibraryDoctorScreen,
    IDialogSetup<LibraryDoctorRequest>, ICancelableDialog
{
    private readonly ILibraryDoctorService _doctor;
    private readonly ILocalizationService _localization;
    private readonly IAppLogger _logger;
    private readonly AsyncRelayCommand _run;
    private IReadOnlyList<LibrarySongEntry> _songs = [];
    private IReadOnlyList<ILibraryDoctorGroup> _groups = [];
    private CancellationTokenSource? _cts;
    private bool _hasIssues;
    private bool _isRunning;
    private double _progress;
    private string _summary = "";

    public LibraryDoctorViewModel(ILibraryDoctorService doctor, ILocalizationService localization, IAppLogger logger)
    {
        _doctor = doctor;
        _localization = localization;
        _logger = logger;
        _run = new AsyncRelayCommand(RunAsync, () => !IsRunning);
        CloseCommand = new RelayCommand(() => Stop(true));
    }

    public IReadOnlyList<ILibraryDoctorGroup> Groups
    {
        get => _groups;
        private set => SetProperty(ref _groups, value);
    }

    public bool HasIssues
    {
        get => _hasIssues;
        private set => SetProperty(ref _hasIssues, value);
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value)) _run.NotifyCanExecuteChanged();
        }
    }

    public double Progress
    {
        get => _progress;
        private set => SetProperty(ref _progress, value);
    }

    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    public ICommand RunCommand => _run;

    public ICommand CloseCommand { get; }

    /// <summary>The check the dialog started on open; tests await it.</summary>
    internal Task Scan { get; private set; } = Task.CompletedTask;

    public void Configure(LibraryDoctorRequest request)
    {
        _songs = request.Songs;
        Scan = RunAsync();
    }

    public bool TryCancel()
    {
        Stop(false);
        return true;
    }

    private void Stop(bool answer)
    {
        _cts?.Cancel();
        Close(answer);
    }

    private async Task RunAsync()
    {
        if (IsRunning) return;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        IsRunning = true;
        Progress = 0;
        Groups = [];
        HasIssues = false;
        Summary = _localization["LibraryDoctor_Running"];
        try
        {
            var progress = new Progress<LibraryDoctorProgress>(p => Progress = p.Fraction);
            var report = await _doctor.RunAsync(_songs, progress, token);
            Groups = [.. report.Groups.Select(ToRow)];
            HasIssues = report.TotalIssueCount > 0;
            Summary = report.IsHealthy
                ? _localization.Plural("LibraryDoctor_HealthyOne", "LibraryDoctor_Healthy", report.SongsScanned)
                : _localization.Format("LibraryDoctor_Summary", report.TotalIssueCount, report.SongsScanned,
                    report.PackagesScanned);
            _logger.Info(AppLogCategory.Import,
                $"Library doctor dialog scan complete. Issues={report.TotalIssueCount}, songs={report.SongsScanned}.");
        }
        catch (OperationCanceledException)
        {
            Summary = _localization["LibraryDoctor_Cancelled"];
        }
        catch (Exception ex)
        {
            _logger.Error(AppLogCategory.Import, "Library doctor scan failed.", ex);
            Summary = _localization.Format("LibraryDoctor_Failed", ex.Message);
        }
        finally
        {
            IsRunning = false;
            Progress = 0;
        }
    }

    private ILibraryDoctorGroup ToRow(LibraryIssueGroup group) => new Group(
        _localization[KindTitleKey(group.Kind)],
        group.Count,
        group.Severity == LibraryIssueSeverity.Error,
        group.Severity == LibraryIssueSeverity.Warning,
        [.. group.Issues.Select(ILibraryDoctorIssue (i) => new Issue(i.SongDisplay, Path.GetFileName(i.PsarcPath), i.Detail))]);

    private static string KindTitleKey(LibraryIssueKind kind) => kind switch
    {
        LibraryIssueKind.DuplicatePackage => "LibraryDoctor_Kind_Duplicate",
        LibraryIssueKind.MissingAudio => "LibraryDoctor_Kind_MissingAudio",
        LibraryIssueKind.InvalidTuning => "LibraryDoctor_Kind_InvalidTuning",
        LibraryIssueKind.UnknownTuning => "LibraryDoctor_Kind_UnknownTuning",
        LibraryIssueKind.NoArrangements => "LibraryDoctor_Kind_NoArrangements",
        _ => "LibraryDoctor_Kind_Unreadable"
    };

    private sealed record Group(string Title, int Count, bool IsError, bool IsWarning,
        IReadOnlyList<ILibraryDoctorIssue> Issues) : ILibraryDoctorGroup;

    private sealed record Issue(string SongDisplay, string FileName, string Detail) : ILibraryDoctorIssue;
}
