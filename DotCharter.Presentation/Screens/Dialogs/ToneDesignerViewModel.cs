using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Core.Domain.Project;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Services.Audio.Coordination;
using DotCharter.Services.Editing.Tones;
using DotCharter.Services.State;
using DotCharter.Services.Plugins;
using DotCharter.Tone.Audio.Dsp.Tones;
using DotCharter.Tone.Infrastructure.Configuration;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>
///     The Tone designer, one instance per open, over the active project: its tone definitions (edited through
///     <see cref="ToneEditorViewModel" />) and each arrangement's tone slots, every edit recorded in the history as made
///     (<see cref="ToneDesignerCommits" />), and tones imported from another package (kept in <see cref="DialogMemory" />).
///     Import and generate live in <c>ToneDesignerViewModel.Import.cs</c>.
/// </summary>
public sealed partial class ToneDesignerViewModel : DialogViewModel<bool>, IToneDesignerScreen, ICancelableDialog, IDisposable
{
    private readonly ChartEditDialogServices _edit;
    private readonly IFileStorageService _storage;
    private readonly TonePreviewTargets _preview;
    private readonly ToneDesignerTools _tools;
    private readonly DialogMemory _memory;
    private readonly ChartProject? _project;
    private readonly IChartToneLiveEdit? _liveEdit;
    private readonly RelayCommand _duplicate;
    private readonly RelayCommand _delete;
    private ProjectToneItem? _selectedTone;
    private bool _isPreviewOn;
    private bool _recording;
    private string _importStatus = "";

    public ToneDesignerViewModel(ChartEditDialogServices edit, IFileStorageService storage, TonePreviewTargets preview,
        ToneDesignerTools tools, DialogMemory memory, IPackageFeatures features,
        IChartToneLiveEdit? liveEdit = null)
    {
        CanImportFromPackages = features.ReadsGamePackages;
        _edit = edit;
        _liveEdit = liveEdit;
        _storage = storage;
        _preview = preview;
        preview.TargetChanged += (_, _) => OnSelectionChanged();
        _tools = tools;
        _memory = memory;
        Editor = new ToneEditorViewModel(edit.Localization, () => ProjectTones);
        Editor.Changed += (_, _) =>
        {
            CommitEdit(true);
            ApplyPreviewIfOn();
            PushLiveEdit();
        };

        AddToneCommand = new RelayCommand(AddTone);
        _duplicate = new RelayCommand(DuplicateTone, () => HasSelectedTone);
        _delete = new RelayCommand(DeleteSelectedTone, () => HasSelectedTone);
        GenerateTonesFromAudioCommand = new AsyncRelayCommand(GenerateTonesFromAudioAsync);
        ImportTonesFromPsarcCommand = new AsyncRelayCommand(ImportTonesFromPsarcAsync);
        AddImportedToProjectCommand = new RelayCommand<IImportedTone>(AddImportedToProject);
        UndoCommand = new RelayCommand(() => StepHistory(redo: false));
        RedoCommand = new RelayCommand(() => StepHistory(redo: true));
        CloseCommand = new RelayCommand(() => Finish(true));

        _project = edit.State.ActiveProject;
        if (_project is null)
        {
            ImportStatus = edit.Localization["Status_Export_NoProject"];
            return;
        }

        if (!ReferenceEquals(_memory.ImportedTonesProject, _project))
        {
            _memory.ImportedTones.Clear();
            _memory.ImportedTonesProject = _project;
        }

        foreach (var tone in _memory.ImportedTones) ImportedTones.Add(new ImportedToneItem { Key = tone.Key, Name = tone.Name });
        Editor.SelectedGearSlot = Editor.GearSlots[0];
        RebuildProjectTones(null);
        RebuildArrangementRows();
        _edit.History.HistoryChanged += OnHistoryChanged;
        edit.Logger.Info(AppLogCategory.Project,
            $"Tone designer opened. ProjectTones={ProjectTones.Count}, arrangements={Arrangements.Count}, importedTones={ImportedTones.Count}.");
    }

    public ObservableCollection<ProjectToneItem> ProjectTones { get; } = [];
    public ObservableCollection<ToneDesignerArrangementItem> Arrangements { get; } = [];
    public ObservableCollection<ImportedToneItem> ImportedTones { get; } = [];
    public ToneEditorViewModel Editor { get; }

    IReadOnlyList<IProjectTone> IToneDesignerScreen.ProjectTones => ProjectTones;
    IReadOnlyList<IToneArrangementRow> IToneDesignerScreen.Arrangements => Arrangements;
    IReadOnlyList<IImportedTone> IToneDesignerScreen.ImportedTones => ImportedTones;
    IToneEditor IToneDesignerScreen.Editor => Editor;
    IToneDesignerPreviewTargets IToneDesignerScreen.PreviewTargets => _preview;

    public bool HasProjectTones => ProjectTones.Count > 0;
    public bool HasArrangements => Arrangements.Count > 0;
    public bool HasImportedTones => ImportedTones.Count > 0;
    public IReadOnlyList<string> AuthoredToneKeys => [.. ProjectTones.Select(t => t.Key)];

    public ProjectToneItem? SelectedTone
    {
        get => _selectedTone;
        set
        {
            if (ReferenceEquals(_selectedTone, value)) return;
            CommitPendingEdit();
            _selectedTone = value;
            Editor.Load(value);
            OnPropertyChanged();
            OnSelectionChanged();
            ApplyPreviewIfOn();
        }
    }

    IProjectTone? IToneDesignerScreen.SelectedTone
    {
        get => SelectedTone;
        set => SelectedTone = value as ProjectToneItem;
    }

    public bool HasSelectedTone => SelectedTone is not null;

    public bool CanPreviewNow => _preview.IsAvailable && HasSelectedTone;

    public string ToneActionUnavailableReason =>
        !_preview.IsAvailable ? _edit.Localization["Dialog_Tones_PreviewUnavailable"]
        : HasSelectedTone ? ""
        : _edit.Localization["Dialog_Tones_PickAToneFirst"];

    public bool HasToneActionUnavailableReason => ToneActionUnavailableReason.Length > 0;

    /// <summary>Refused while <see cref="CanPreviewNow" /> is false; the toggle is greyed on the same predicate.</summary>
    public bool IsPreviewOn
    {
        get => _isPreviewOn;
        set
        {
            if (value && !CanPreviewNow) value = false;
            if (!SetProperty(ref _isPreviewOn, value)) return;
            if (value) ApplyPreviewIfOn();
            else if (_preview.Stop()) _edit.Logger.Info(AppLogCategory.Audio, "Tone designer preview off — user chain restored.");
        }
    }

    public string ImportStatus { get => _importStatus; private set => SetProperty(ref _importStatus, value); }

    public ICommand AddToneCommand { get; }
    public ICommand DuplicateToneCommand => _duplicate;
    public ICommand DeleteToneCommand => _delete;
    public ICommand UndoCommand { get; }
    public ICommand RedoCommand { get; }
    public ICommand GenerateTonesFromAudioCommand { get; }
    public ICommand ImportTonesFromPsarcCommand { get; }
    public ICommand AddImportedToProjectCommand { get; }
    public ICommand CloseCommand { get; }

    internal TonePreviewSession Preview => _preview.Clone;

    /// <summary>Esc commits what is buffered exactly as Done does and only answers the default.</summary>
    public bool TryCancel()
    {
        Finish(false);
        return true;
    }

    /// <summary>Ends a running preview (the rig or the game gets its tone back) and lets go of the session.</summary>
    public void Dispose()
    {
        _edit.History.HistoryChanged -= OnHistoryChanged;
        _preview.Dispose();
    }

    /// <summary>Done and Esc alike commit what is buffered: an edit here is never discarded by leaving.</summary>
    private void Finish(bool result)
    {
        if (IsClosed) return;
        try
        {
            CommitPendingEdit();
            CommitSlotAssignments();
        }
        catch (Exception ex)
        {
            // Losing one buffered tone edit is strictly better than a crash on close.
            _edit.Logger.Error(AppLogCategory.Project, "Tone designer flush failed on close.", ex);
        }

        _edit.History.HistoryChanged -= OnHistoryChanged;
        IsPreviewOn = false;
        _liveEdit?.EndToneEditPreview();
        Close(result);
    }

    /// <summary>Records the loaded tone's pending edit (a key edit waits for leaving the field, close or a switch) as an undo step.</summary>
    internal void CommitPendingEdit() => CommitEdit(false);

    /// <param name="tick">One tick of a gesture (a drag, typing): ticks fold into one undo step, and a key edit stays pending.</param>
    private void CommitEdit(bool tick)
    {
        if (_project is null || Editor.Loaded is not { } item || !Editor.IsDirty) return;
        var key = tick ? item.Key : Editor.EffectiveKey();
        if (!tick && !string.Equals(key, Editor.EditKey.Trim(), StringComparison.Ordinal)) Editor.ShowKey(key);

        var tone = Editor.BuildTone(key);
        var json = RsToneJsonWriter.Write(tone);
        Editor.IsDirty = tick && !string.Equals(Editor.EffectiveKey(), item.Key, StringComparison.Ordinal);
        if (json == item.Json && key == item.Key && tone.Name == item.Name) return;

        var definition = new ToneDefinition { Key = key, Name = tone.Name, Json = json };
        var plan = ToneDesignerCommits.PlanEdit(_project, item.Key, key, definition, tick && !Editor.LastChangeDiscrete, Editor, _edit.Logger);
        Record(plan.Action);
        if (plan.Renamed) RetargetSlotRows(item.Key, plan.Key);
        item.Key = plan.Key;
        item.Name = tone.Name;
        item.Json = plan.Json;
        OnTonesChanged();
        _edit.Logger.Info(AppLogCategory.Project, $"Tone designer committed edit of tone '{plan.Key}'.");
    }

    /// <summary>The slot rows as an undo step; typing in one folds into a single step.</summary>
    internal void CommitSlotAssignments()
    {
        if (ToneDesignerCommits.SlotAction(Arrangements) is not { } action) return;
        Record(action);
        _edit.Logger.Info(AppLogCategory.Project, "Tone designer assigned tone slots.");
    }

    /// <summary>The user let go of a control or left a field: the next edit is a new undo step, and a pending key edit is recorded.</summary>
    public void EndEditGesture(bool leavingField = false)
    {
        if (leavingField) CommitPendingEdit();
        _edit.History.EndGesture();
    }

    /// <summary>Runs one history step of the designer's own; the history-changed refresh ignores it, the caller updates the lists.</summary>
    private void Record(IChartAction action)
    {
        _recording = true;
        try
        {
            _edit.History.Do(action);
        }
        finally
        {
            _recording = false;
        }
    }

    /// <summary>An undo, a redo or another writer moved the project: the lists and the editor show what it holds now.</summary>
    private void OnHistoryChanged()
    {
        if (_recording || _project is null || IsClosed) return;
        if (!ToneDesignerCommits.Matches(_project.ToneDefinitions, ProjectTones))
        {
            if (ToneDesignerCommits.UpdateInPlace(_project.ToneDefinitions, ProjectTones))
            {
                Editor.Load(SelectedTone);
                ApplyPreviewIfOn();
            }
            else
            {
                RebuildProjectTones(SelectedTone?.Key);
            }
        }

        if (ToneDesignerCommits.RowsStale(_project, Arrangements)) RebuildArrangementRows();
        OnTonesChanged();
    }

    private void AddTone()
    {
        if (_project is null) return;
        CommitPendingEdit();
        var key = RsToneAuthoring.MakeUniqueKey("tone", ProjectTones.Select(t => t.Key));
        var json = RsToneJsonWriter.Write(RsToneAuthoring.CreateDefaultTone(key, key, RsGearCatalog.Shared));
        Upsert(null, new ToneDefinition { Key = key, Name = key, Json = json });
        _edit.Logger.Info(AppLogCategory.Project, $"Tone designer added tone '{key}'.");
    }

    private void DuplicateTone()
    {
        if (_project is null || SelectedTone is not { } source) return;
        CommitPendingEdit();
        var key = RsToneAuthoring.MakeUniqueKey(source.Key, ProjectTones.Select(t => t.Key));
        var json = RsToneJsonParser.TryParse(source.Json, out var parsed) && parsed is not null
            ? RsToneJsonWriter.Write(parsed with { Key = key, Name = source.Name })
            : RsToneJsonWriter.Write(RsToneAuthoring.CreateDefaultTone(key, source.Name, RsGearCatalog.Shared));
        Upsert(null, new ToneDefinition { Key = key, Name = source.Name, Json = json });
        _edit.Logger.Info(AppLogCategory.Project, $"Tone designer duplicated '{source.Key}' as '{key}'.");
    }

    private void DeleteSelectedTone()
    {
        if (SelectedTone is { } victim) DeleteTone(victim);
    }

    /// <summary>Removes ONLY the definition and its buffered edits (DLC Builder behaviour). Tone changes, slots, the
    /// starting tone and declared names keep the key, ready for a replacement tone or an import under it; export
    /// validation reports the undefined key while no definition exists. ONE undo step.</summary>
    private void DeleteTone(ProjectToneItem victim)
    {
        if (_project is null) return;
        Editor.IsDirty = false;
        var index = ProjectTones.IndexOf(victim);
        Record(new UpsertToneDefinitionAction(_project, victim.Key, null));
        ProjectTones.Remove(victim);
        OnTonesChanged();
        SelectedTone = ProjectTones.Count > 0 ? ProjectTones[Math.Clamp(index, 0, ProjectTones.Count - 1)] : null;
        _edit.Logger.Info(AppLogCategory.Project, $"Tone designer deleted the definition of tone '{victim.Key}'.");
    }

    /// <summary>One step of the app's single history; the history-changed refresh brings the lists and the editor to it.</summary>
    private void StepHistory(bool redo)
    {
        if (_project is null) return;
        CommitPendingEdit();
        if (redo) _edit.History.Redo();
        else _edit.History.Undo();
        OnHistoryChanged();
    }

    /// <summary>One undoable upsert, listed and selected.</summary>
    private void Upsert(string? replaceKey, ToneDefinition definition)
    {
        Record(new UpsertToneDefinitionAction(_project!, replaceKey, definition));
        var item = new ProjectToneItem(definition.Key, definition.Name, definition.Json);
        ProjectTones.Add(item);
        OnTonesChanged();
        SelectedTone = item;
    }

    private void RebuildProjectTones(string? selectKey)
    {
        Editor.IsDirty = false;
        _selectedTone = null;
        ProjectTones.Clear();
        foreach (var def in _project!.ToneDefinitions) ProjectTones.Add(new ProjectToneItem(def.Key, def.Name, def.Json));
        OnTonesChanged();
        SelectedTone = ProjectTones.FirstOrDefault(t => string.Equals(t.Key, selectKey, StringComparison.OrdinalIgnoreCase))
                       ?? ProjectTones.FirstOrDefault();
        if (SelectedTone is null) OnSelectionChanged();
    }

    /// <summary>Rows follow a rename or delete, else <see cref="CommitSlotAssignments" /> writes the old key back on close.</summary>
    private void RetargetSlotRows(string from, string to)
    {
        foreach (var row in Arrangements) row.Retarget(from, to);
    }

    /// <summary>The per-arrangement rows, from the arrangements' CURRENT tone fields.</summary>
    private void RebuildArrangementRows()
    {
        foreach (var old in Arrangements) old.SlotEdited -= CommitSlotAssignments;
        Arrangements.Clear();
        foreach (var row in ToneDesignerCommits.BuildRows(_project!, ProjectTones)) Arrangements.Add(row);
        foreach (var row in Arrangements) row.SlotEdited += CommitSlotAssignments;
        OnPropertyChanged(nameof(HasArrangements));
    }

    private void OnTonesChanged()
    {
        OnPropertyChanged(nameof(ProjectTones));
        OnPropertyChanged(nameof(HasProjectTones));
        OnPropertyChanged(nameof(AuthoredToneKeys));
        foreach (var row in Arrangements) row.RefreshDefinitionMarks();
    }

    private void OnSelectionChanged()
    {
        OnPropertyChanged(nameof(HasSelectedTone));
        OnPropertyChanged(nameof(CanPreviewNow));
        OnPropertyChanged(nameof(ToneActionUnavailableReason));
        OnPropertyChanged(nameof(HasToneActionUnavailableReason));
        _duplicate.NotifyCanExecuteChanged();
        _delete.NotifyCanExecuteChanged();
        if (!CanPreviewNow && _isPreviewOn) IsPreviewOn = false;
    }

    /// <summary>The buffered body goes to the chart's tone playback at once, under the tone's COMMITTED key, so a knob is heard
    /// while it moves. Not while this dialog's own Preview holds the rig: two writers would fight over one rig.</summary>
    private void PushLiveEdit()
    {
        if (_liveEdit is null || _isPreviewOn || Editor.Loaded is not { } item) return;
        _liveEdit.PreviewToneEdit(item.Key, RsToneJsonWriter.Write(Editor.BuildTone(item.Key)));
    }

    private void ApplyPreviewIfOn()
    {
        if (_isPreviewOn && Editor.Loaded is not null) _preview.Apply(Editor.BuildTone(Editor.EffectiveKey()));
    }
}
