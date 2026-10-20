using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Core.Utilities;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Services.Audio.Coordination;
using DotCharter.Services.Diagnostics;
using DotCharter.Services.Infrastructure.Documents;
using DotCharter.Services.Session;
using DotCharter.Services.State;
using DotCharter.Services.State.Checkpoints;
using DotCharter.Tone.Infrastructure.Configuration;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>
///     Edit ▸ Checkpoints. One instance per open; the list is read when it
///     opens and re-read after every change. A restore asks on top of this
///     dialog, which stays open while the restore runs.
/// </summary>
public sealed class CheckpointsViewModel : DialogViewModel<bool>, ICheckpointsScreen
{
    private readonly ICheckpointService _checkpoints;
    private readonly IProjectDocumentService _documents;
    private readonly ChartEditDialogServices _edit;
    private readonly IEditorTransport _transport;
    private readonly IProjectSessionService _session;
    private readonly IMessagePrompt _prompt;
    private IReadOnlyList<CheckpointRow> _rows = [];
    private CheckpointRow? _armed;
    private string _newCheckpointName = "";
    private bool _isBusy;

    public CheckpointsViewModel(ICheckpointService checkpoints, IProjectDocumentService documents,
        ChartEditDialogServices edit, IEditorTransport transport, IProjectSessionService session,
        IMessagePrompt prompt)
    {
        _checkpoints = checkpoints;
        _documents = documents;
        _edit = edit;
        _transport = transport;
        _session = session;
        _prompt = prompt;
        CreateCheckpointCommand = new AsyncRelayCommand(CreateAsync);
        RestoreCommand = new AsyncRelayCommand<ICheckpointRow>(RestoreAsync);
        DeleteCommand = new RelayCommand<ICheckpointRow>(Delete);
        CloseCommand = new RelayCommand(() => Close(true));
        RefreshList();
        edit.Logger.Info(AppLogCategory.Project,
            $"Checkpoints dialog opened. Identity='{Identity}', count={_rows.Count}.");
    }

    private ILocalizationService L => _edit.Localization;

    public string NewCheckpointName
    {
        get => _newCheckpointName;
        set => SetProperty(ref _newCheckpointName, value ?? "");
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public ICommand CreateCheckpointCommand { get; }

    public bool HasCheckpoints => _rows.Count > 0;

    public IReadOnlyList<ICheckpointRow> Checkpoints => _rows;

    public ICommand RestoreCommand { get; }

    public ICommand DeleteCommand { get; }

    public ICommand CloseCommand { get; }

    /// <summary>The project the checkpoints are keyed on: its saved path, else its library song, else "untitled".</summary>
    private string Identity => CheckpointIdentity.For(_edit.State.CurrentProjectPath, _edit.State.CurrentSongIdentity);

    /// <summary>
    ///     A restore clears the undo tree, but the replace guard only asks about
    ///     a DIRTY project — and dirty is a different question from "is there
    ///     history". This is the case it lets through in silence.
    /// </summary>
    private bool RestoreWouldDiscardHistorySilently =>
        !_session.IsProjectDirty && (_edit.History.CanUndo || _edit.History.CanRedo);

    private async Task CreateAsync()
    {
        var project = _edit.State.ActiveProject;
        if (IsBusy) return;
        if (project == null)
        {
            _edit.State.SetMessage(L["Checkpoints_NoProject"]);
            return;
        }

        IsBusy = true;
        try
        {
            // Captured on the UI thread and deep-cloned, so the background write
            // can never be torn by a concurrent edit — the save's own protection.
            var snapshot = ChartProjectCloner.Clone(_documents.AssembleProject(project, _transport.Bpm,
                _edit.State.CurrentAudioFileName, _edit.Editor.LyricPhrases));
            var songName = project.Chart?.SongName ?? string.Empty;
            var appVersion = DotCharter.Services.Infrastructure.AppVersionInfo.Current;
            var (name, identity) = (NewCheckpointName, Identity);
            var entry = await Task.Run(() => _checkpoints.Create(identity, name, snapshot, songName, appVersion));

            NewCheckpointName = string.Empty;
            RefreshList();
            _edit.State.SetMessage(L.Format("Checkpoints_Created", entry.Name), StatusSeverity.Success);
            _edit.Logger.Info(AppLogCategory.Project,
                $"Created checkpoint '{entry.Name}' (id={entry.Id}) for identity '{identity}'.");
        }
        catch (Exception ex)
        {
            _edit.Logger.Error(AppLogCategory.Project, "Creating checkpoint failed.", ex);
            _edit.State.SetMessage(L.Format("Checkpoints_CreateFailed", ex.Message), StatusSeverity.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RestoreAsync(ICheckpointRow? row)
    {
        if (row is not CheckpointRow item || IsBusy) return;
        Disarm();

        // Restoring REPLACES the open project and clears its undo history, so
        // both questions are asked first — and a "no" to either leaves the list
        // where it was, re-read because the store may have moved during the await.
        if (!await _session.MayReplaceOpenProjectAsync())
        {
            _edit.Logger.Info(AppLogCategory.Project,
                $"Checkpoint restore cancelled — the user kept the unsaved project. Checkpoint='{item.Name}'.");
            RefreshList();
            return;
        }

        if (RestoreWouldDiscardHistorySilently && !await ConfirmDiscardHistoryAsync())
        {
            _edit.Logger.Info(AppLogCategory.Project,
                $"Checkpoint restore cancelled — the user kept the edit history. Checkpoint='{item.Name}'.");
            RefreshList();
            return;
        }

        IsBusy = true;
        try
        {
            var identity = Identity;
            var project = await Task.Run(() => _checkpoints.Restore(identity, item.Entry));

            // The old undo tree holds actions bound to the old graph.
            _edit.Editor.ApplyProject(project, resetHistory: true);
            // The audio REFERENCE is part of the checkpoint, and it is what the
            // next save writes — a restore that kept the current one would put
            // the post-checkpoint audio straight back on disk.
            _edit.State.CurrentAudioFileName = project.AudioFileName ?? "";
            _transport.Bpm = project.Bpm;
            // DIRTY on purpose: the restored project deliberately differs from the file on disk.
            _session.MarkProjectDirty();
            _edit.Editor.RequestRedraw();

            _edit.State.SetMessage(L.Format("Checkpoints_Restored", item.Name), StatusSeverity.Success);
            _edit.Logger.Info(AppLogCategory.Project, $"Restored checkpoint '{item.Name}' (id={item.Entry.Id}).");
            Close(true);
        }
        catch (Exception ex)
        {
            _edit.Logger.Error(AppLogCategory.Project, $"Restoring checkpoint '{item.Name}' failed.", ex);
            _edit.State.SetMessage(L.Format("Checkpoints_RestoreFailed", ex.Message), StatusSeverity.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private Task<bool> ConfirmDiscardHistoryAsync() => _prompt.ShowAsync(
        L["Checkpoints_RestoreDiscardsHistoryTitle"], L["Checkpoints_RestoreDiscardsHistoryMessage"],
        MessageDialogKind.Warning, confirmation: true);

    /// <summary>
    ///     Two clicks: the first arms the row and relabels its button, the
    ///     second deletes. Arming another row or re-reading the list disarms.
    ///     Restore is the recoverable neighbour and it asks; this one cannot be undone.
    /// </summary>
    private void Delete(ICheckpointRow? row)
    {
        if (row is not CheckpointRow item || IsBusy) return;
        if (!ReferenceEquals(_armed, item))
        {
            Disarm();
            _armed = item;
            item.IsConfirmingDelete = true;
            return;
        }

        Disarm();
        try
        {
            _checkpoints.Delete(Identity, item.Entry.Id);
            RefreshList();
            _edit.State.SetMessage(L.Format("Checkpoints_Deleted", item.Name));
            _edit.Logger.Info(AppLogCategory.Project, $"Deleted checkpoint '{item.Name}' (id={item.Entry.Id}).");
        }
        catch (Exception ex)
        {
            _edit.Logger.Error(AppLogCategory.Project, $"Deleting checkpoint '{item.Name}' failed.", ex);
            _edit.State.SetMessage(L.Format("Checkpoints_DeleteFailed", ex.Message), StatusSeverity.Error);
        }
    }

    private void Disarm()
    {
        _armed?.IsConfirmingDelete = false;
        _armed = null;
    }

    private void RefreshList()
    {
        Disarm();
        _rows = [.. _checkpoints.List(Identity).Select(e => new CheckpointRow(e, L))];
        OnPropertyChanged(nameof(Checkpoints));
        OnPropertyChanged(nameof(HasCheckpoints));
    }

    /// <summary>One saved checkpoint. Its Delete caption follows the two-click latch.</summary>
    internal sealed class CheckpointRow(CheckpointEntry entry, ILocalizationService localization)
        : ObservableObject, ICheckpointRow
    {
        private bool _isConfirmingDelete;

        public CheckpointEntry Entry { get; } = entry;

        public string Name { get; } = entry.Name;

        public string Detail { get; } = entry.CreatedUtc == default
            ? entry.SongName
            : string.IsNullOrWhiteSpace(entry.SongName)
                ? Stamp(entry.CreatedUtc)
                : $"{entry.SongName} · {Stamp(entry.CreatedUtc)}";

        public bool IsConfirmingDelete
        {
            get => _isConfirmingDelete;
            set
            {
                if (SetProperty(ref _isConfirmingDelete, value)) OnPropertyChanged(nameof(DeleteCaption));
            }
        }

        public string DeleteCaption => IsConfirmingDelete
            ? localization["Checkpoints_DeleteConfirm"]
            : localization["Checkpoints_Delete"];

        private static string Stamp(DateTimeOffset createdUtc) =>
            createdUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
    }
}
