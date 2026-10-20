using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Core.Domain.Project;
using DotCharter.Core.Utilities;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Services.Editing.Maintenance;
using DotCharter.Tone.Infrastructure.Configuration;
using DotCharter.Tone.Logging;
using DotCharter.Services.Parsing.Midi;
using DotCharter.Services.State;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>
///     The MIDI import dialog. Each preview converts OFF the UI thread and only
///     when its inputs changed; Import reuses the preview's draft.
/// </summary>
public sealed class MidiImportViewModel : DialogViewModel<bool>, IMidiImportScreen, IDialogSetup<MidiImportRequest>,
    ICancelableDialog
{
    private readonly IChartMaintenanceService _maintenance;
    private readonly ProjectState _state;
    private readonly ILocalizationService _localization;
    private readonly IAppLogger _logger;
    private readonly Func<MidiFileData, MidiDraftOptions, MidiChartDraft> _convert;
    private readonly AsyncRelayCommand _import;
    private readonly List<int?> _trackIndices = [];
    private MidiFileData? _file;
    private IReadOnlyList<string> _tracks = [];
    private string _fileName = "";
    private int _selectedTrackIndex = -1;
    private bool _includeDrumChannel;
    private bool _replaceBeatGrid;
    private bool _isBusy;
    private bool _hasDraft;
    private string _summary = "";
    private string _errorMessage = "";
    private bool _suppressRefresh;
    private DraftKey? _cachedKey;
    private DraftAttempt? _cachedAttempt;

    /// <summary>Bumped by every conversion and by Cancel; a conversion that returns stale is discarded.</summary>
    private int _generation;

    /// <summary>The <c>converter</c> is replaceable so a test can count its calls or make one throw.</summary>
    public MidiImportViewModel(IChartMaintenanceService maintenance, ProjectState state,
        ILocalizationService localization, IAppLogger logger,
        Func<MidiFileData, MidiDraftOptions, MidiChartDraft>? converter = null)
    {
        _maintenance = maintenance;
        _state = state;
        _localization = localization;
        _logger = logger;
        _convert = converter ?? MidiToDraftConverter.Convert;
        _import = new AsyncRelayCommand(ImportAsync, () => !IsBusy && HasDraft);
        CancelCommand = new RelayCommand(() => TryCancel());
    }

    public string FileName
    {
        get => _fileName;
        private set => SetProperty(ref _fileName, value);
    }

    public IReadOnlyList<string> Tracks
    {
        get => _tracks;
        private set => SetProperty(ref _tracks, value);
    }

    public int SelectedTrackIndex
    {
        get => _selectedTrackIndex;
        set
        {
            if (value == _selectedTrackIndex) return;
            _selectedTrackIndex = value;
            OnPropertyChanged();
            RefreshDraft();
        }
    }

    public bool IncludeDrumChannel
    {
        get => _includeDrumChannel;
        set
        {
            if (SetProperty(ref _includeDrumChannel, value)) RefreshDraft();
        }
    }

    public bool ReplaceBeatGrid
    {
        get => _replaceBeatGrid;
        set => SetProperty(ref _replaceBeatGrid, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value)) _import.NotifyCanExecuteChanged();
        }
    }

    /// <summary>The current selection drafts at least one note.</summary>
    public bool HasDraft
    {
        get => _hasDraft;
        private set
        {
            if (SetProperty(ref _hasDraft, value)) _import.NotifyCanExecuteChanged();
        }
    }

    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    public ICommand ImportCommand => _import;

    public ICommand CancelCommand { get; }

    /// <summary>The in-flight (or last) preview conversion, for a test to await.</summary>
    public Task DraftRefresh { get; private set; } = Task.CompletedTask;

    /// <summary>Builds the track picker, pre-ticks the grid replacement, and starts the first preview.</summary>
    public void Configure(MidiImportRequest request)
    {
        _file = request.File;
        FileName = request.FileName;
        var labels = new List<string> { _localization["Dialog_MidiImport_AllTracks"] };
        _trackIndices.Add(null);
        for (var i = 0; i < _file.Tracks.Count; i++)
        {
            var track = _file.Tracks[i];
            if (track.Notes.Count == 0) continue;
            var name = string.IsNullOrWhiteSpace(track.Name)
                ? _localization.Format("Dialog_MidiImport_UnnamedTrack", i + 1)
                : track.Name;
            labels.Add($"{name} ({track.Notes.Count})");
            _trackIndices.Add(i);
        }

        Tracks = labels;
        ReplaceBeatGrid = ProjectHasNoChartedSounds();

        // Seeding the selection must not start a refresh of its own, or opening
        // a file races two conversions of the same inputs.
        _suppressRefresh = true;
        SelectedTrackIndex = 0;
        _suppressRefresh = false;
        RefreshDraft();
    }

    /// <summary>Always closes: a conversion is a PREVIEW that writes nothing, and the bump stops one in flight.</summary>
    public bool TryCancel()
    {
        _generation++;
        IsBusy = false;
        if (!IsClosed) _logger.Info(AppLogCategory.Import, $"MIDI import canceled. File='{FileName}'.");
        Close(false);
        return true;
    }

    private void RefreshDraft()
    {
        if (_suppressRefresh) return;
        DraftRefresh = RefreshDraftAsync();
    }

    private async Task RefreshDraftAsync()
    {
        if (await EnsureDraftAsync() is { } attempt) Show(attempt);
    }

    private async Task ImportAsync()
    {
        if (await EnsureDraftAsync() is not { } attempt) return;
        Show(attempt);
        if (attempt.Error != null) return;
        if (attempt.Draft is not { Notes.Count: > 0 } draft)
        {
            _state.SetMessage(_localization["Status_MidiImportNothingFound"]);
            return;
        }

        Close(true);
        _maintenance.ApplyMidiDraft(draft, ReplaceBeatGrid);
    }

    private void Show(DraftAttempt attempt)
    {
        if (attempt.Error != null)
        {
            HasDraft = false;
            Summary = "";
            ErrorMessage = _localization.Format("Dialog_MidiImport_ConversionFailed", attempt.Error);
            return;
        }

        ErrorMessage = "";
        var notes = attempt.Draft?.Notes.Count ?? 0;
        HasDraft = notes > 0;
        Summary = HasDraft
            ? _localization.Format("Dialog_MidiImport_Summary", notes)
            : _localization["Dialog_MidiImport_NothingFound"];
    }

    /// <summary>Converts off the UI thread, only when the inputs changed; null when a newer run took over.</summary>
    private async Task<DraftAttempt?> EnsureDraftAsync()
    {
        if (_file is not { } file) return new DraftAttempt(null, null);

        var openStringMidi = ResolveOpenStringMidi();
        var trackIndex = _selectedTrackIndex >= 0 && _selectedTrackIndex < _trackIndices.Count
            ? _trackIndices[_selectedTrackIndex]
            : null;
        var key = new DraftKey(trackIndex, IncludeDrumChannel, string.Join(",", openStringMidi));
        if (_cachedAttempt != null && key == _cachedKey) return _cachedAttempt;

        var generation = ++_generation;
        var options = new MidiDraftOptions
        {
            TrackIndex = key.TrackIndex,
            IncludeDrumChannel = key.IncludeDrums,
            OpenStringMidi = openStringMidi
        };

        IsBusy = true;
        DraftAttempt attempt;
        try
        {
            attempt = new DraftAttempt(await Task.Run(() => _convert(file, options)), null);
        }
        catch (Exception ex)
        {
            _logger.Error(AppLogCategory.Import, $"MIDI draft conversion failed. File='{FileName}'.", ex);
            attempt = new DraftAttempt(null, $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (generation == _generation) IsBusy = false;
        }

        if (generation != _generation) return null;
        _cachedKey = key;
        _cachedAttempt = attempt;
        return attempt;
    }

    /// <summary>
    ///     The open strings of the arrangement the draft will sit beside, shifted
    ///     by its own tuning — without it every pitch below a standard low E of a
    ///     drop-tuned song is unmappable and silently skipped.
    /// </summary>
    private IReadOnlyList<int> ResolveOpenStringMidi()
    {
        var arrangement = _state.ActiveArrangement;
        if (arrangement == null) return MidiDraftOptions.StandardGuitarMidi;

        var open = BaseOpenStringMidi(arrangement.ArrangementType,
            Math.Clamp(arrangement.StringCount, 4, StandardOpenStrings.MaxStringCount));

        // GetOffsets…, not TryGet…: an unmapped pitch is DROPPED from the draft,
        // so a standard-tuning assumption beats no mapping at all.
        var offsets = arrangement.TuningPitches.Count >= open.Length
            ? arrangement.TuningPitches.Take(open.Length).Select(t => t.Step).ToArray()
            : TuningUtilities.GetOffsetsForTuningType(arrangement.TuningType, open.Length);

        for (var i = 0; i < open.Length; i++) open[i] += i < offsets.Length ? offsets[i] : 0;
        return open;
    }

    private static int[] BaseOpenStringMidi(ArrangementType type, int stringCount)
    {
        if (type == ArrangementType.Bass)
        {
            return stringCount switch
            {
                5 => [23, 28, 33, 38, 43],
                >= 6 => [23, 28, 33, 38, 43, 48],
                _ => [28, 33, 38, 43]
            };
        }

        return stringCount switch
        {
            >= 7 => TuningUtilities.GetStandardMidiPitches(stringCount), // B1 …, F#1 B1 …
            <= 4 => [40, 45, 50, 55],
            5 => [40, 45, 50, 55, 59],
            _ => [40, 45, 50, 55, 59, 64]
        };
    }

    /// <summary>Replacing the grid moves every placed note, so it is pre-ticked only on an uncharted, unlocked grid.</summary>
    private bool ProjectHasNoChartedSounds()
    {
        var project = _state.ActiveProject;
        if (project == null) return true;
        if (project.IsBeatMapLocked) return false;
        return project.Arrangements.All(a => a.Levels.All(l => l.Notes.Count == 0 && l.Chords.Count == 0));
    }

    /// <summary>The inputs a draft is a function of.</summary>
    private sealed record DraftKey(int? TrackIndex, bool IncludeDrums, string OpenStringMidi);

    /// <summary>A conversion outcome; a non-null <c>Error</c> means the converter threw.</summary>
    private sealed record DraftAttempt(MidiChartDraft? Draft, string? Error);
}
