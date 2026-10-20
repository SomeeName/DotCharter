using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Services.Infrastructure.Recovery;
using DotCharter.Tone.Infrastructure.Configuration;
using DotCharter.Tone.Logging;
using DotCharter.Services.Session;
using DotCharter.Services.State;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>
///     The startup crash-recovery prompt. Rows are PICKED and the footer's one
///     primary restores the pick, so Enter gets the newest crash back.
/// </summary>
public sealed class RecoveryViewModel
    : DialogViewModel<bool>, IRecoveryScreen, IDialogSetup<RecoveryRequest>, ICancelableDialog
{
    private readonly ICrashRecoveryService _crashRecovery;
    private readonly IProjectSessionService _session;
    private readonly ProjectState _state;
    private readonly ILocalizationService _localization;
    private readonly IAppLogger _logger;
    private List<RecoverySessionRow> _rows = [];
    private IRecoverySession? _selected;
    private bool _isBusy;

    public RecoveryViewModel(ICrashRecoveryService crashRecovery, IProjectSessionService session, ProjectState state,
        ILocalizationService localization, IAppLogger logger)
    {
        _crashRecovery = crashRecovery;
        _session = session;
        _state = state;
        _localization = localization;
        _logger = logger;
        var recover = new AsyncRelayCommand(() => RecoverAsync(_selected as RecoverySessionRow),
            () => _selected is not null && !_isBusy);
        RecoverSelectedCommand = recover;
        DiscardCommand = new RelayCommand<IRecoverySession>(Discard);
        DiscardAllCommand = new RelayCommand(DiscardAll);
        AskLaterCommand = new RelayCommand(() => TryCancel());
    }

    public string Subtitle => _localization.Plural("Recovery_SubtitleOne", "Recovery_SubtitleFmt", _rows.Count);

    public IReadOnlyList<IRecoverySession> Sessions => _rows;

    public IRecoverySession? SelectedSession
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value)) ((AsyncRelayCommand)RecoverSelectedCommand).NotifyCanExecuteChanged();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value)) ((AsyncRelayCommand)RecoverSelectedCommand).NotifyCanExecuteChanged();
        }
    }

    public ICommand RecoverSelectedCommand { get; }

    public ICommand DiscardCommand { get; }

    public ICommand DiscardAllCommand { get; }

    public ICommand AskLaterCommand { get; }

    public void Configure(RecoveryRequest request)
    {
        _rows = [.. request.Sessions.Select(s => new RecoverySessionRow(s, _localization))];
        _selected = _rows.FirstOrDefault();
        _logger.Info(AppLogCategory.Project,
            $"Recovery dialog opened with {_rows.Count} session(s): {string.Join("; ", _rows.Select(s => s.DisplayName))}.");
    }

    /// <summary>Esc is "Ask later" — every file stays for the next launch. Not while a restore runs.</summary>
    public bool TryCancel()
    {
        if (IsBusy) return false;
        _logger.Info(AppLogCategory.Project, $"Recovery dialog dismissed with {_rows.Count} session(s) kept for later.");
        Close(false);
        return true;
    }

    private async Task RecoverAsync(RecoverySessionRow? row)
    {
        if (row == null || IsBusy) return;
        IsBusy = true;
        try
        {
            var meta = row.Session.Metadata;
            var snapshotPath = row.Session.SnapshotPath;
            _logger.Info(AppLogCategory.Project,
                $"Recovering crash snapshot '{snapshotPath}' (original='{meta.OriginalProjectPath}').");
            await _session.LoadFile(snapshotPath);

            // LoadFile reports failure through the status line; success is the snapshot becoming the current path.
            if (!string.Equals(_state.CurrentProjectPath, snapshotPath, StringComparison.OrdinalIgnoreCase))
            {
                _logger.Warning(AppLogCategory.Project, $"Recovery load failed for '{snapshotPath}' — leaving snapshot on disk.");
                _state.SetMessage(_localization.Format("Recovery_RecoverFailed", row.DisplayName), StatusSeverity.Error);
                return;
            }

            // Save goes to the ORIGINAL file (empty for untitled: Save As), with the original audio reference.
            _state.CurrentProjectPath = meta.OriginalProjectPath ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(meta.OriginalAudioFileName))
                _state.CurrentAudioFileName = meta.OriginalAudioFileName;
            _session.AppConfig.LastProjectPath = meta.OriginalProjectPath ?? string.Empty;
            _session.SaveAppConfig();
            // DIRTY on purpose: the original file does not contain the recovered
            // work, so a clean session would close with no prompt and lose it.
            _session.MarkProjectDirty();

            Remove(row);
            _state.SetMessage(_localization.Format("Recovery_Recovered", row.DisplayName), StatusSeverity.Success);
            _logger.Info(AppLogCategory.Project,
                $"Recovered '{row.DisplayName}'. CurrentProjectPath='{_state.CurrentProjectPath}' (snapshot stays until the next manual save).");
        }
        catch (Exception ex)
        {
            _logger.Error(AppLogCategory.Project, $"Recovery failed for '{row.DisplayName}'.", ex);
            _state.SetMessage(_localization.Format("Recovery_RecoverFailed", ex.Message), StatusSeverity.Error);
        }
        finally
        {
            IsBusy = false;
            CloseIfEmpty();
        }
    }

    private void Discard(IRecoverySession? session)
    {
        if (session is not RecoverySessionRow row || IsBusy) return;
        _crashRecovery.DiscardSession(row.Session);
        Remove(row);
        CloseIfEmpty();
    }

    /// <summary>The whole set in one gesture: three snapshots from one crash are one decision, not three.</summary>
    private void DiscardAll()
    {
        if (IsBusy) return;
        _logger.Info(AppLogCategory.Project, $"Discarding all {_rows.Count} recoverable session(s) at the user's request.");
        foreach (var row in _rows) _crashRecovery.DiscardSession(row.Session);
        _rows = [];
        SelectedSession = null;
        OnPropertyChanged(nameof(Sessions));
        OnPropertyChanged(nameof(Subtitle));
        CloseIfEmpty();
    }

    private void Remove(RecoverySessionRow row)
    {
        _rows = [.. _rows.Where(r => !ReferenceEquals(r, row))];
        OnPropertyChanged(nameof(Sessions));
        OnPropertyChanged(nameof(Subtitle));
        if (ReferenceEquals(_selected, row)) SelectedSession = _rows.FirstOrDefault();
    }

    private void CloseIfEmpty()
    {
        if (_rows.Count == 0) Close(true);
    }

    private sealed class RecoverySessionRow : IRecoverySession
    {
        public RecoverySessionRow(RecoverableSession session, ILocalizationService l)
        {
            Session = session;
            var meta = session.Metadata;
            var unsaved = l["Recovery_UnsavedProject"];
            var fileName = Path.GetFileNameWithoutExtension(meta.OriginalProjectPath);
            DisplayName = session.IsUntitled
                ? string.IsNullOrWhiteSpace(meta.SongName) ? unsaved : $"{unsaved} — {meta.SongName}"
                : string.IsNullOrWhiteSpace(meta.SongName) ||
                  string.Equals(meta.SongName, fileName, StringComparison.OrdinalIgnoreCase)
                    ? fileName
                    : $"{fileName} — {meta.SongName}";
            IsUntitled = session.IsUntitled;
            KindLabel = l[session.IsUntitled ? "Recovery_Kind_NeverSaved" : "Recovery_Kind_Project"];
            PathLine = session.IsUntitled ? l["Recovery_NoProjectFile"] : meta.OriginalProjectPath;
            SavedAt = meta.TimestampUtc == default
                ? string.Empty
                : meta.TimestampUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        }

        public RecoverableSession Session { get; }

        public string DisplayName { get; }

        public bool IsUntitled { get; }

        public string KindLabel { get; }

        public string PathLine { get; }

        public string SavedAt { get; }
    }
}
