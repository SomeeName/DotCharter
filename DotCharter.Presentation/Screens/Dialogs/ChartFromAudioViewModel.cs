using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Core.Domain.Project;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Services.Audio.StemSeparation;
using DotCharter.Services.Editing;
using DotCharter.Services.Import.Audio;
using DotCharter.Services.State;
using DotCharter.Tone.Infrastructure.Configuration;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>
///     File ▸ Chart from audio. The analysis runs off the UI thread and writes
///     nothing; Apply adds a new arrangement through the undo history, or holds the
///     draft for a phrase-by-phrase merge (<see cref="ChartFromAudioMerge" />). The stem
///     strip and its Separate stems verb are in <c>ChartFromAudioViewModel.Stems.cs</c>.
/// </summary>
public sealed partial class ChartFromAudioViewModel : DialogViewModel<bool>, IChartFromAudioScreen, ICancelableDialog
{
    private readonly ChartEditDialogServices _edit;
    private readonly IFileStorageService _storage;
    private readonly IExportTargetWriter _files;
    private readonly IChartFromAudioAnalyzer _analyzer;
    private readonly IChartFromAudioStemLocator _stems;
    private readonly ISongStemSeparation _separator;
    private readonly ChartFromAudioMerge _merge;
    private readonly DialogMemory _memory;
    private double _sensitivity = ChartFromAudioAnalyzer.DefaultSensitivity;
    private bool _isBusy;
    private string _status = "";
    private string _draftSummary = "";
    private bool _mergeIntoCurrent;
    private bool _replaceExisting;
    private ChartFromAudioDraft? _draft;
    private ChartProject? _draftProject;
    private Arrangement? _draftTemplate;

    /// <summary>Bumped by whoever takes the dialog away from a run in flight; a stale run drops its result.</summary>
    private int _analysisGeneration;

    public ChartFromAudioViewModel(ChartEditDialogServices edit, IFileStorageService storage, IExportTargetWriter files,
        IChartFromAudioAnalyzer analyzer, ChartFromAudioStemServices stems, ChartFromAudioMerge merge, DialogMemory memory)
    {
        _edit = edit;
        _storage = storage;
        _files = files;
        _analyzer = analyzer;
        _stems = stems.Locator;
        _separator = stems.Separation;
        _merge = merge;
        _memory = memory;
        BrowseAudioCommand = new AsyncRelayCommand(BrowseAudioAsync);
        AnalyzeCommand = new AsyncRelayCommand(AnalyzeAsync);
        ApplyCommand = new RelayCommand(Apply);
        CancelCommand = new RelayCommand(() => TryCancel());
        SeparateStemsCommand = new AsyncRelayCommand(SeparateStemsAsync);
        CancelSeparationCommand = new RelayCommand(CancelSeparation);

        // A path belongs to the project it was chosen for and to no other: a
        // previous song's WAV, pre-filled and plausible, would chart the wrong
        // recording onto this beat grid.
        var project = edit.State.ActiveProject;
        if (!ReferenceEquals(project, memory.AudioPathProject))
        {
            memory.AudioPath = "";
            memory.AudioPathProject = project;
        }

        if (memory.AudioPath.Length == 0
            && edit.State.CurrentAudioFileName.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
        {
            memory.AudioPath = edit.State.CurrentAudioFileName;
        }

        StemCheck = RefreshStemHintAsync(null);
    }

    public string AudioPath
    {
        get => _memory.AudioPath;
        set
        {
            if (_memory.AudioPath == (value ?? "")) return;
            _memory.AudioPath = value ?? "";
            OnPropertyChanged();
            StemCheck = RefreshStemHintAsync(null);
        }
    }

    public double Sensitivity
    {
        get => _sensitivity;
        set => SetProperty(ref _sensitivity, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetProperty(ref _isBusy, value)) OnPropertyChanged(nameof(NothingToApply));
        }
    }

    public string Status
    {
        get => _status;
        set
        {
            if (SetProperty(ref _status, value ?? "")) OnPropertyChanged(nameof(NothingToApply));
        }
    }

    public bool HasDraft => _draft is { Notes.Count: > 0 };

    public int DraftNoteCount => _draft?.Notes.Count ?? 0;

    public string DraftSummary
    {
        get => _draftSummary;
        private set => SetProperty(ref _draftSummary, value);
    }

    public bool NothingToApply => !HasDraft && !IsBusy && Status.Length == 0;

    public bool CanMerge => _merge.TargetLevel(_edit.State.ActiveArrangement) is not null;

    public string MergeTargetText => _edit.State.ActiveArrangement is { } arrangement
        ? _edit.Localization.Format("ChartFromAudio_TargetMerge", arrangement.Name)
        : _edit.Localization["ChartFromAudio_TargetMergeNone"];

    public bool MergeIntoCurrent
    {
        get => _mergeIntoCurrent && CanMerge;
        set
        {
            if (!SetProperty(ref _mergeIntoCurrent, value && CanMerge)) return;
            if (HasDraft) Status = "";
            OnPropertyChanged(nameof(AddAsNewArrangement));
            OnPropertyChanged(nameof(ApplyText));
        }
    }

    public bool AddAsNewArrangement
    {
        get => !MergeIntoCurrent;
        set => MergeIntoCurrent = !value;
    }

    public bool ReplaceExisting
    {
        get => _replaceExisting;
        set
        {
            if (SetProperty(ref _replaceExisting, value) && HasDraft) Status = "";
        }
    }

    public string ApplyText => _edit.Localization[MergeIntoCurrent ? "ChartFromAudio_ApplyMerge" : "ChartFromAudio_Apply"];

    public ICommand BrowseAudioCommand { get; }

    public ICommand AnalyzeCommand { get; }

    public ICommand ApplyCommand { get; }

    public ICommand CancelCommand { get; }

    /// <summary>
    ///     Cancel and Esc close at once, even mid-run: the analysis writes nothing, and its
    /// result drops itself. A stem separation is the app's and runs on.
    /// </summary>
    public bool TryCancel()
    {
        DiscardRunInFlight();
        Finish(false);
        return true;
    }

    private void Finish(bool result)
    {
        LetSeparationRunOn();
        Close(result);
    }

    private async Task BrowseAudioAsync()
    {
        var path = await _storage.OpenFilePickerAsync(_edit.Localization["ChartFromAudio_PickWav"],
            [new FilePickerFilter("WAV", ["*.wav"])], Path.GetDirectoryName(_edit.State.CurrentAudioFileName));
        if (!string.IsNullOrWhiteSpace(path)) AudioPath = path;
    }

    private async Task AnalyzeAsync()
    {
        if (IsBusy) return;
        var beats = _edit.State.Beats.ToList();
        if (beats.Count == 0)
        {
            Status = _edit.Localization["ChartFromAudio_NoBeats"];
            return;
        }

        if (!_files.FileExists(AudioPath))
        {
            Status = _edit.Localization["ChartFromAudio_NoFile"];
            return;
        }

        var generation = ++_analysisGeneration;
        SetDraft(null, null, null);
        IsBusy = true;
        Status = _edit.Localization["ChartFromAudio_Analyzing"];
        var path = AudioPath;
        var template = _edit.State.ActiveArrangement;
        var project = _edit.State.ActiveProject;
        var sensitivity = Sensitivity;
        try
        {
            var draft = await Task.Run(() => _analyzer.Analyze(path, beats, template, sensitivity));
            if (generation != _analysisGeneration)
            {
                _edit.Logger.Info(AppLogCategory.Import,
                    $"Chart-from-audio analysis result dropped: cancelled or restarted. File='{path}'.");
                return;
            }

            SetDraft(draft, project, template);
            Status = HasDraft ? "" : _edit.Localization["ChartFromAudio_NothingFound"];
            _edit.Logger.Info(AppLogCategory.Import,
                $"Chart-from-audio analysis done. File='{path}', listened to {(draft.StemName is { } s ? "the separated " + s + " stem" : "the file itself")}, " +
                $"notes={DraftNoteCount}, minConfidence={draft.MinConfidence:0.00}.");
        }
        catch (Exception ex)
        {
            _edit.Logger.Error(AppLogCategory.Import, $"Chart-from-audio analysis failed. File='{path}'.", ex);
            if (generation == _analysisGeneration)
                Status = _edit.Localization.Format("ChartFromAudio_Failed", ex.Message);
        }
        finally
        {
            if (generation == _analysisGeneration) IsBusy = false;
        }
    }

    /// <summary>Accepts the draft as a new arrangement, through the history so it is dirty and undoable.</summary>
    private void Apply()
    {
        if (_draft is not { Notes.Count: > 0 } draft) return;
        var project = _edit.State.ActiveProject;
        if (project == null)
        {
            Status = _edit.Localization["ChartFromAudio_NoProject"];
            _edit.Logger.Info(AppLogCategory.Import, "Chart-from-audio draft not applied: no project is open.");
            return;
        }

        // The frets were spelled from the arrangement active when the analysis
        // STARTED, against that project's beat grid. Stamping whatever is active
        // now would sound every note at the wrong pitch, silently.
        var template = _edit.State.ActiveArrangement;
        if (!ReferenceEquals(project, _draftProject) || !ReferenceEquals(template, _draftTemplate))
        {
            SetDraft(null, null, null);
            Status = _edit.Localization["ChartFromAudio_Stale"];
            _edit.Logger.Info(AppLogCategory.Import,
                "Chart-from-audio draft discarded: the project or arrangement changed during analysis.");
            return;
        }

        if (MergeIntoCurrent && template is not null)
        {
            if (_merge.Hold(project, template, draft, ReplaceExisting) is { } why) Status = why;
            else Finish(true);
            return;
        }

        var arrangement = DraftArrangementFactory.FromTemplate(template, UniqueDraftName(project));
        var level = new ChartLevel();
        foreach (var note in draft.Notes) level.Notes.Add(note);
        arrangement.Levels.Add(level);

        _edit.History.Do(new AddArrangementAction(_edit.State, project, arrangement));
        _edit.State.SetMessage(_edit.Localization.Format("ChartFromAudio_Applied", arrangement.Name, draft.Notes.Count));
        _edit.Logger.Info(AppLogCategory.Import,
            $"Chart-from-audio draft applied. Arrangement='{arrangement.Name}', notes={draft.Notes.Count}.");
        Finish(true);
    }

    private void DiscardRunInFlight()
    {
        _analysisGeneration++;
        SetDraft(null, null, null);
        Status = "";
        IsBusy = false;
    }

    private void SetDraft(ChartFromAudioDraft? draft, ChartProject? project, Arrangement? template)
    {
        _draft = draft;
        _draftProject = project;
        _draftTemplate = template;
        DraftSummary = draft == null
            ? ""
            : draft.StemName is { } stem
                ? _edit.Localization.Format("ChartFromAudio_SummaryFromStem", draft.Notes.Count,
                    draft.AverageConfidence * 100, StemLabel(stem))
                : _edit.Localization.Format("ChartFromAudio_Summary", draft.Notes.Count, draft.AverageConfidence * 100);
        OnPropertyChanged(nameof(HasDraft));
        OnPropertyChanged(nameof(DraftNoteCount));
        OnPropertyChanged(nameof(NothingToApply));
    }

    /// <summary>The stem mixer's own name for a separated source; the raw name for one it has none for.</summary>
    private string StemLabel(string stem) => stem switch
    {
        "guitar" => _edit.Localization["Stem_Guitar"],
        "bass" => _edit.Localization["Stem_Bass"],
        "other" => _edit.Localization["Stem_Other"],
        _ => stem
    };

    private string UniqueDraftName(ChartProject project)
    {
        var baseName = _edit.Localization["ChartFromAudio_DraftName"];
        var name = baseName;
        var i = 2;
        while (project.Arrangements.Any(a => a.Name == name)) name = $"{baseName} {i++}";
        return name;
    }

    /// <summary>The draft arrangement and the switch to it are one undo node.</summary>
    private sealed class AddArrangementAction(ProjectState state, ChartProject project, Arrangement arrangement)
        : IChartAction, ILinkPolicyAction
    {
        /// <summary>Linked measures: cannot change linked content in beats.</summary>
        public LinkPolicy LinkPolicy => LinkPolicy.Retain;

        private readonly Arrangement? _previousActive = state.ActiveArrangement;

        public void Execute()
        {
            project.Arrangements.Add(arrangement);
            state.ActiveArrangement = arrangement;
        }

        public void Undo()
        {
            project.Arrangements.Remove(arrangement);
            state.ActiveArrangement = _previousActive;
        }
    }
}
