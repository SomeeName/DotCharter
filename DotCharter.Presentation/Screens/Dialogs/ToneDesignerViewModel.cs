using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Core.Domain.Project;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Services.Audio.Coordination;
using DotCharter.Services.Editing.Tones;
using DotCharter.Services.Export.Rocksmith;
using DotCharter.Services.State;
using DotCharter.Services.Plugins;
using DotCharter.Tone.Audio.Dsp.Tones;
using DotCharter.Tone.Infrastructure.Configuration;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>
///     The Tone designer, one instance per open, over the active project: its tone
///     definitions (edited through <see cref="ToneEditorViewModel" />, committed as ONE
///     undo step per tone on a switch or on close), each arrangement's tone slots
///     (committed on close as one undo step) and tones imported from another package
///     (kept in <see cref="DialogMemory" /> for the export). Import and generate live in
///     <c>ToneDesignerViewModel.Import.cs</c>.
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
            ApplyPreviewIfOn();
            PushLiveEdit();
        };

        AddToneCommand = new RelayCommand(AddTone);
        _duplicate = new RelayCommand(DuplicateTone, () => HasSelectedTone);
        _delete = new RelayCommand(DeleteTone, () => HasSelectedTone);
        GenerateTonesFromAudioCommand = new AsyncRelayCommand(GenerateTonesFromAudioAsync);
        ImportTonesFromPsarcCommand = new AsyncRelayCommand(ImportTonesFromPsarcAsync);
        AddImportedToProjectCommand = new RelayCommand<IImportedTone>(AddImportedToProject);
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
    public ICommand GenerateTonesFromAudioCommand { get; }
    public ICommand ImportTonesFromPsarcCommand { get; }
    public ICommand AddImportedToProjectCommand { get; }
    public ICommand CloseCommand { get; }

    internal TonePreviewSession Preview => _preview.Clone;

    /// <summary>Esc commits what is buffered exactly as Done does; it only answers the default.</summary>
    public bool TryCancel()
    {
        Finish(false);
        return true;
    }

    /// <summary>Ends a running preview (the rig or the game gets its tone back) and lets go of the session.</summary>
    public void Dispose() => _preview.Dispose();

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

        IsPreviewOn = false;
        _liveEdit?.EndToneEditPreview();
        Close(result);
    }

    /// <summary>Commits the loaded tone's buffered edit as ONE undoable action; per-knob actions would flood the undo stack.</summary>
    internal void CommitPendingEdit()
    {
        if (_project is null || Editor.Loaded is not { } item || !Editor.IsDirty) return;
        var key = Editor.EffectiveKey();
        if (!string.Equals(key, Editor.EditKey.Trim(), StringComparison.Ordinal)) Editor.ShowKey(key);

        var tone = Editor.BuildTone(key);
        var json = RsToneJsonWriter.Write(tone);
        Editor.IsDirty = false;
        if (json == item.Json && key == item.Key && tone.Name == item.Name) return;

        var definition = new ToneDefinition { Key = key, Name = tone.Name, Json = json };
        if (string.Equals(key, item.Key, StringComparison.Ordinal))
        {
            _edit.History.Do(new UpsertToneDefinitionAction(_project, item.Key, definition));
        }
        else
        {
            // A KEY edit renames the tone everywhere it is stored, body included, as ONE
            // undo step: slots, starting tone, declared names and tone changes follow.
            var plan = ToneKeyEdits.PlanRename(_project, item.Key, key, definition);
            if (!plan.IsReady)
            {
                _edit.Logger.Warning(AppLogCategory.Project, $"Tone designer: renaming '{item.Key}' to '{key}' refused ({plan.Outcome}); the edit stays under the old key.");
                Editor.ShowKey(item.Key);
                definition = new ToneDefinition { Key = item.Key, Name = tone.Name, Json = RsToneJsonWriter.Write(Editor.BuildTone(item.Key)) };
                key = item.Key;
                json = definition.Json;
                _edit.History.Do(new UpsertToneDefinitionAction(_project, item.Key, definition));
            }
            else
            {
                _edit.History.Do(plan.Action!);
                RetargetSlotRows(item.Key, key);
            }
        }

        item.Key = key;
        item.Name = tone.Name;
        item.Json = json;
        OnTonesChanged();
        _edit.Logger.Info(AppLogCategory.Project, $"Tone designer committed edit of tone '{key}'.");
    }

    /// <summary>Edited slot rows as ONE undoable action; an untouched row is never rewritten, so imported orderings survive.</summary>
    internal void CommitSlotAssignments()
    {
        var actions = new List<IChartAction>();
        foreach (var row in Arrangements)
        {
            var a = row.Arrangement;
            if (row.ToneBase.Trim() == a.ToneBase && row.ToneA.Trim() == a.ToneA && row.ToneB.Trim() == a.ToneB &&
                row.ToneC.Trim() == a.ToneC && row.ToneD.Trim() == a.ToneD)
            {
                continue;
            }

            actions.Add(new AssignArrangementTonesAction(a, ToneSlotAssignment.Capture(a),
                ToneSlotAssignment.FromSlots(row.ToneBase, row.ToneA, row.ToneB, row.ToneC, row.ToneD)));
        }

        if (actions.Count == 0) return;
        _edit.History.Do(actions.Count == 1 ? actions[0] : new CompositeAction(actions) { LabelKey = "History_AssignTones" });
        _edit.Logger.Info(AppLogCategory.Project, $"Tone designer assigned tone slots on {actions.Count} arrangement(s).");
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

    /// <summary>
    ///     The victim's buffered edits die with it: nothing is committed first. The tone goes
    /// EVERYWHERE, as one undo step — its slots, declared names and tone changes too —
    ///     so no reference is left naming a body that is gone.
    /// </summary>
    private void DeleteTone()
    {
        if (_project is null || SelectedTone is not { } victim) return;
        Editor.IsDirty = false;
        var index = ProjectTones.IndexOf(victim);
        _edit.History.Do(ToneKeyEdits.BuildDelete(_project, victim.Key) ?? new UpsertToneDefinitionAction(_project, victim.Key, null));
        RetargetSlotRows(victim.Key, "");
        ProjectTones.Remove(victim);
        OnTonesChanged();
        SelectedTone = ProjectTones.Count > 0 ? ProjectTones[Math.Clamp(index, 0, ProjectTones.Count - 1)] : null;
        _edit.Logger.Info(AppLogCategory.Project, $"Tone designer deleted tone '{victim.Key}'.");
    }

    /// <summary>One undoable upsert, listed and selected.</summary>
    private void Upsert(string? replaceKey, ToneDefinition definition)
    {
        _edit.History.Do(new UpsertToneDefinitionAction(_project!, replaceKey, definition));
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

    /// <summary>
    ///     A row slot that named <paramref name="from" /> now names <paramref name="to" /> ("" clears
    ///     it), so the rows agree with the arrangements a rename or delete just rewrote. Without it
    ///     <see cref="CommitSlotAssignments" /> would read the stale row text as an edit and write the
    ///     old key back on close. A row the user edited to something else is left alone.
    /// </summary>
    private void RetargetSlotRows(string from, string to)
    {
        string Swap(string value) => ToneKeyEdits.Same(value, from) && value.Trim().Length > 0 ? to : value;

        foreach (var row in Arrangements)
        {
            row.ToneBase = Swap(row.ToneBase);
            row.ToneA = Swap(row.ToneA);
            row.ToneB = Swap(row.ToneB);
            row.ToneC = Swap(row.ToneC);
            row.ToneD = Swap(row.ToneD);
        }
    }

    /// <summary>The per-arrangement rows, from the arrangements' CURRENT tone fields.</summary>
    private void RebuildArrangementRows()
    {
        Arrangements.Clear();
        foreach (var arrangement in _project!.Arrangements.Where(a => a.ArrangementType != ArrangementType.Vocal))
        {
            var (toneBase, slots) = RsToneSlots.Resolve(arrangement);
            Arrangements.Add(new ToneDesignerArrangementItem
            {
                Name = arrangement.Name,
                TypeLabel = ArrangementTypeLabel.For(arrangement.Name, arrangement.ArrangementType.ToString()),
                Arrangement = arrangement,
                Tones = ProjectTones,
                ToneBase = toneBase,
                ToneA = slots.ElementAtOrDefault(0) ?? "",
                ToneB = slots.ElementAtOrDefault(1) ?? "",
                ToneC = slots.ElementAtOrDefault(2) ?? "",
                ToneD = slots.ElementAtOrDefault(3) ?? ""
            });
        }

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

    /// <summary>
    /// the buffered body goes to the chart's tone playback at once, under the
    ///     tone's COMMITTED key (what the chart names), so a knob or the Volume is heard
    ///     while it moves. Not while this dialog's own ▶ Preview holds the rig — that
    ///     plays the edited tone alone, and two writers would fight over one rig.
    /// </summary>
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
