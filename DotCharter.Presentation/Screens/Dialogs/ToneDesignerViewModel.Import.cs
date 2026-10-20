// The Tone designer's tones from elsewhere: another package's definitions, a tone file, the tone rig, and tones generated from the song's own audio.
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Core.Domain.Project;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Services.Editing.Tones;
using DotCharter.Services.State;
using DotCharter.Services.Plugins;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.Audio.Dsp.Tones;
using DotCharter.Tone.Infrastructure.Configuration;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Presentation.Screens.Dialogs;

public sealed partial class ToneDesignerViewModel
{
    public bool CanImportFromPackages { get; }

    /// <summary>Reads a DLC Builder tone file (tone JSON, tone XML or an <c>.rs2dlc</c>) into the project, in every build.</summary>
    public ICommand ImportToneFileCommand => _importToneFile ??= new AsyncRelayCommand(ImportToneFileAsync);

    private AsyncRelayCommand? _importToneFile;

    /// <summary>Opens the review of the closest Rocksmith tone for the rig being played (<see cref="RigImport" />).</summary>
    public ICommand ImportFromToneRigCommand => RigImporter.Command;

    public IRigToneImportReview? RigImport => RigImporter.Review;

    /// <summary>How the review runs a conversion: off the UI thread; a test runs it inline.</summary>
    internal Func<Func<RigToRsToneResult>, Task<RigToRsToneResult>> RigImportRunner { get => RigImporter.Runner; set => RigImporter.Runner = value; }

    private ToneDesignerRigImport? _rigImporter;

    private ToneDesignerRigImport RigImporter => _rigImporter ??= CreateRigImporter();

    private ToneDesignerRigImport CreateRigImporter()
    {
        var importer = new ToneDesignerRigImport(_edit, _preview, _project, new ToneDesignerRigImportHost(
            () => SelectedTone, () => ProjectTones.Select(t => t.Key), () => _edit.State.ActiveArrangement ?? Arrangements.FirstOrDefault()?.Arrangement,
            CommitPendingEdit, key =>
            {
                RebuildProjectTones(key);
                RebuildArrangementRows();
            }, status => ImportStatus = status));
        importer.ReviewChanged += (_, _) => OnPropertyChanged(nameof(RigImport));
        return importer;
    }

    /// <summary>Opens the AI tone creator over the selected tone (<see cref="ToneCreatorViewModel" />).</summary>
    public ICommand CreateWithAiCommand => _createWithAi ??= new AsyncRelayCommand(CreateWithAiAsync);

    private AsyncRelayCommand? _createWithAi;

    /// <summary>
    ///     The creator writes the project itself, as ONE undo step, so everything buffered here is committed first and
    ///     the rows are rebuilt from the project after: a stale slot row would write its old text back on close.
    /// </summary>
    private async Task CreateWithAiAsync()
    {
        if (_project is null) return;
        CommitPendingEdit();
        CommitSlotAssignments();
        IsPreviewOn = false;
        _liveEdit?.EndToneEditPreview();
        string? key;
        try
        {
            key = await _tools.Dialogs.ShowAsync(new ToneCreatorRequest(SelectedTone?.Key, SelectedTone?.Name, _preview));
        }
        catch (InvalidOperationException ex)
        {
            _edit.Logger.Error(AppLogCategory.Project, "The AI tone creator could not open.", ex);
            return;
        }

        if (string.IsNullOrEmpty(key)) return;
        RebuildProjectTones(key);
        RebuildArrangementRows();
        ImportStatus = _edit.Localization.Format("Dialog_ToneCreator_Accepted", key);
    }

    private async Task ImportTonesFromPsarcAsync()
    {
        if (_project is null || !CanImportFromPackages) return;
        var loc = _edit.Localization;
        var path = await _storage.OpenFilePickerAsync(loc["Picker_SelectPsarcTones"],
            [new FilePickerFilter("Rocksmith Package", ["*.psarc"]), FilePickerFilters.AllFiles],
            _edit.Config.LoadAppConfig().LastExportDir);
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            var definitions = await Task.Run(() => PackageTones.Read(path));
            var added = 0;
            foreach (var definition in definitions)
            {
                if (_memory.ImportedTones.Any(t => string.Equals(t.Key, definition.Key, StringComparison.Ordinal))) continue;
                _memory.ImportedTones.Add(new ToneDefinition { Key = definition.Key, Name = definition.Name, Json = definition.Json });
                ImportedTones.Add(new ImportedToneItem { Key = definition.Key, Name = definition.Name });
                added++;
            }

            ImportStatus = loc.Plural("Dialog_Tones_ImportResultOne", "Dialog_Tones_ImportResult", added);
            OnPropertyChanged(nameof(HasImportedTones));
            _edit.Logger.Info(AppLogCategory.Project, $"Tone designer imported {added} tone definition(s) from '{path}'.");
        }
        catch (Exception ex)
        {
            _edit.Logger.Warning(AppLogCategory.Project, $"Tone import failed. Path='{path}', message='{ex.Message}'.");
            ImportStatus = loc.Format("Dialog_Tones_ImportFailed", ex.Message);
        }
    }

    /// <summary>
    ///     Reads a DLC Builder tone file (a tone JSON or XML, or a project's Tones) and adds every
    ///     tone whose key the project does not own, as ONE undo step. Skipped keys are named in the
    ///     status line; a file that cannot be read is said there and logged, never thrown.
    /// </summary>
    private async Task ImportToneFileAsync()
    {
        if (_project is null) return;
        var loc = _edit.Localization;
        var path = await _storage.OpenFilePickerAsync(loc["Picker_SelectToneFile"],
            [new FilePickerFilter(loc["Picker_ToneFiles"], ["*.json", "*.xml", "*.rs2dlc"]), FilePickerFilters.AllFiles],
            _edit.Config.LoadAppConfig().LastExportDir);
        if (string.IsNullOrEmpty(path)) return;

        IReadOnlyList<RsToneFileTone> tones;
        try
        {
            tones = await ToneFileImport.ReadAsync(path);
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
        {
            _edit.Logger.Warning(AppLogCategory.Project, $"Tone file import failed. Path='{path}', message='{ex.Message}'.");
            ImportStatus = loc.Format("Dialog_Tones_ImportFailed", ex.Message);
            return;
        }

        CommitPendingEdit();
        var taken = new HashSet<string>(ProjectTones.Select(t => t.Key.Trim()), StringComparer.OrdinalIgnoreCase);
        var added = new List<ToneDefinition>();
        var skipped = new List<string>();
        foreach (var tone in tones)
        {
            if (taken.Add(tone.Key.Trim())) added.Add(new ToneDefinition { Key = tone.Key, Name = tone.Name, Json = tone.Json });
            else skipped.Add(tone.Key);
        }

        if (added.Count > 0)
        {
            var actions = added.Select(d => (IChartAction)new UpsertToneDefinitionAction(_project, null, d)).ToList();
            _edit.History.Do(actions.Count == 1 ? actions[0] : new CompositeAction(actions) { LabelKey = "History_ImportToneFile" });
            foreach (var d in added) ProjectTones.Add(new ProjectToneItem(d.Key, d.Name, d.Json));
            OnTonesChanged();
            SelectedTone = ProjectTones[^1];
        }

        var parts = new List<string>();
        if (added.Count > 0) parts.Add(loc.Format("Dialog_Tones_FileAdded", string.Join(", ", added.Select(d => d.Key))));
        if (skipped.Count > 0) parts.Add(loc.Format("Dialog_Tones_FileSkipped", string.Join(", ", skipped)));
        ImportStatus = string.Join(" ", parts);
        _edit.Logger.Info(AppLogCategory.Project,
            $"Tone designer read '{path}': added {added.Count} tone(s), skipped {skipped.Count} already in the project.");
    }

    /// <summary>Copies an imported body into the project verbatim, undoably; a key the project already owns is refused.</summary>
    private void AddImportedToProject(IImportedTone? item)
    {
        if (item is null || _project is null) return;
        if (_memory.ImportedTones.FirstOrDefault(t => t.Key == item.Key) is not { } imported) return;
        if (ProjectTones.Any(t => string.Equals(t.Key.Trim(), item.Key.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            ImportStatus = _edit.Localization.Format("Dialog_Tones_AlreadyInProject", item.Key);
            return;
        }

        CommitPendingEdit();
        Upsert(null, new ToneDefinition { Key = imported.Key, Name = imported.Name, Json = imported.Json });
        ImportStatus = _edit.Localization.Format("Dialog_Tones_AddedToProject", item.Key);
        _edit.Logger.Info(AppLogCategory.Project, $"Tone designer added imported tone '{item.Key}' to the project.");
    }

    /// <summary>
    ///     One tone per tone change PLACED on the arrangement the charter is looking at, from the
    ///     audio it governs (<see cref="DotCharter.Services.Editing.Tones.ToneGenerationRun" />). Every
    ///     arrangement at once would author tones into arrangements the charter is not working on.
    /// </summary>
    /// <remarks>
    /// The decode and analysis run in <c>Task.Run</c>, so the window stays live while the
    ///     song is read. The command is busy, and so disabled, while it runs; the edits are written
    ///     back here, on the UI thread.
    /// </remarks>
    private async Task GenerateTonesFromAudioAsync()
    {
        if (_project is null) return;
        CommitPendingEdit();
        var arrangement = _edit.State.ActiveArrangement ?? Arrangements.FirstOrDefault()?.Arrangement;
        if (arrangement is null || arrangement.ToneChanges.Count == 0)
        {
            ImportStatus = _edit.Localization["Dialog_Tones_GenerateNoChanges"];
            return;
        }

        ImportStatus = _edit.Localization["Dialog_Tones_GenerateRunning"];
        var input = ToneGenerationRun.Capture(_project, arrangement, _edit.State.CurrentProjectPath);
        var plan = await Task.Run(() => _tools.Estimate.Analyse(input));
        var outcome = _tools.Estimate.Apply(_project, arrangement, plan);
        RebuildProjectTones(outcome.Generated.Count > 0 ? outcome.Generated[^1].Key : SelectedTone?.Key);
        RebuildArrangementRows();
        ImportStatus = outcome.Describe(_edit.Localization);
    }
}
