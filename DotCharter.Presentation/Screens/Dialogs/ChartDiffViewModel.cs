using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Core.Diff;
using DotCharter.Core.Domain.Events;
using DotCharter.Core.Domain.Project;
using DotCharter.Core.Utilities;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Services.Infrastructure.Configuration;
using DotCharter.Services.Infrastructure.Documents;
using DotCharter.Tone.Infrastructure.Configuration;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.Tone.Logging;
using DotCharter.Services.State;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>
///     Tools ▸ Chart diff. One instance per open. The compared file is a WHOLE
///     second project, so it is let go of before the next one loads and when
///     the dialog closes — a song held for nothing is the one cost here.
/// </summary>
public sealed class ChartDiffViewModel : DialogViewModel<bool>, IChartDiffScreen, ICancelableDialog
{
    private readonly IFileStorageService _storage;
    private readonly IProjectDocumentService _documents;
    private readonly ProjectState _state;
    private readonly IConfigurationService _config;
    private readonly ILocalizationService _l;
    private readonly IAppLogger _logger;
    private IReadOnlyList<Arrangement> _theirs = [];
    private IReadOnlyList<string> _comparable = [];
    private IReadOnlyList<IChartDiffRow> _rows = [];
    private int _selectedIndex = -1;
    private string _comparedFileName = "";
    private string _summary = "";
    private string _status;
    private bool _isBusy;

    public ChartDiffViewModel(IFileStorageService storage, IProjectDocumentService documents, ProjectState state,
        IConfigurationService config, ILocalizationService localization, IAppLogger logger)
    {
        _storage = storage;
        _documents = documents;
        _state = state;
        _config = config;
        _l = localization;
        _logger = logger;
        _status = localization["ChartDiff_PickFile"];
        BrowseFileCommand = new AsyncRelayCommand(BrowseFileAsync);
        CloseCommand = new RelayCommand(() =>
        {
            if (!_isBusy) CloseAndRelease(true);
        });
    }

    public string ComparedFileName
    {
        get => _comparedFileName;
        private set => SetProperty(ref _comparedFileName, value);
    }

    public ICommand BrowseFileCommand { get; }

    public bool HasComparableArrangements => _comparable.Count > 0;

    public IReadOnlyList<string> ComparableArrangements => _comparable;

    public int SelectedArrangementIndex
    {
        get => _selectedIndex;
        set
        {
            if (SetProperty(ref _selectedIndex, value)) Compare();
        }
    }

    public bool HasResults => _rows.Count > 0;

    public IReadOnlyList<IChartDiffRow> Rows => _rows;

    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public ICommand CloseCommand { get; }

    /// <summary>The arrangement being compared against, while one is.</summary>
    internal Arrangement? SelectedArrangement =>
        _selectedIndex >= 0 && _selectedIndex < _theirs.Count ? _theirs[_selectedIndex] : null;

    /// <summary>A file still loading keeps the dialog open: Esc would otherwise strand the load.</summary>
    public bool TryCancel()
    {
        if (_isBusy) return false;
        CloseAndRelease(false);
        return true;
    }

    private void CloseAndRelease(bool answer)
    {
        Release();
        Close(answer);
    }

    private async Task BrowseFileAsync()
    {
        if (_isBusy) return;
        var path = await _storage.OpenFilePickerAsync(_l["ChartDiff_PickFile"],
            [new FilePickerFilter("DotCharter Project", ["*.rscp"])], _config.LoadAppConfig().LastRscpDir);
        if (string.IsNullOrEmpty(path)) return;

        _isBusy = true;
        // The previous comparison goes BEFORE the next loads: two songs are never held at once.
        Release();
        try
        {
            var other = await Task.Run(() => _documents.LoadChart(path));
            if (IsClosed) return;
            ComparedFileName = Path.GetFileName(path);
            _theirs = [.. other.Arrangements];
            _comparable = [.. _theirs.Select(a => $"{a.Name} ({a.ArrangementType})")];
            OnPropertyChanged(nameof(ComparableArrangements));
            OnPropertyChanged(nameof(HasComparableArrangements));
            if (_theirs.Count == 0)
            {
                Status = _l["ChartDiff_NoArrangements"];
                return;
            }

            Status = "";
            // The same-named arrangement is what the user almost always means.
            var activeName = _state.ActiveArrangement?.Name;
            var sameName = _theirs.ToList().FindIndex(a => a.Name == activeName);
            SelectedArrangementIndex = sameName >= 0 ? sameName : 0;
        }
        catch (Exception ex)
        {
            _logger.Error(AppLogCategory.Project, $"Chart diff could not load '{path}'.", ex);
            Status = _l.Format("ChartDiff_LoadFailed", ex.Message);
        }
        finally
        {
            _isBusy = false;
        }
    }

    private void Release()
    {
        _selectedIndex = -1;
        OnPropertyChanged(nameof(SelectedArrangementIndex));
        _theirs = [];
        _comparable = [];
        OnPropertyChanged(nameof(ComparableArrangements));
        OnPropertyChanged(nameof(HasComparableArrangements));
        SetRows([]);
        ComparedFileName = "";
        Summary = "";
    }

    private void SetRows(IReadOnlyList<IChartDiffRow> rows)
    {
        _rows = rows;
        OnPropertyChanged(nameof(Rows));
        OnPropertyChanged(nameof(HasResults));
    }

    private void Compare()
    {
        var mine = _state.ActiveArrangement;
        var theirs = SelectedArrangement;
        if (mine == null || theirs == null) return;

        SetRows([]);
        try
        {
            // The file is the OLD side; the chart in the editor is the NEW side.
            var result = ChartDiffEngine.Diff(theirs, mine);
            var beats = _state.Beats;
            var rows = new List<IChartDiffRow>();
            foreach (var level in result.Levels)
            {
                foreach (var entry in level.Entries)
                    rows.Add(ToRow(entry, level.DifficultyIndex, beats));
            }
            // Structure counts toward the summary, so it is listed too — a
            // re-sectioned chart must not read "3 changed" over an empty table.
            foreach (var entry in result.Structure)
                rows.Add(ToRow(entry, beats));

            SetRows(rows);
            Summary = result.IsEmpty
                ? _l["ChartDiff_Identical"]
                : _l.Format("ChartDiff_Summary", result.AddedCount, result.RemovedCount, result.ChangedCount);
            _logger.Info(AppLogCategory.Project,
                $"Chart diff: '{mine.Name}' vs '{theirs.Name}' in '{ComparedFileName}'. Added={result.AddedCount}, removed={result.RemovedCount}, changed={result.ChangedCount}.");
        }
        catch (Exception ex)
        {
            _logger.Error(AppLogCategory.Project, "Chart diff failed.", ex);
            Status = _l.Format("ChartDiff_LoadFailed", ex.Message);
        }
    }

    private string ChangeText(ChartDiffChange change) => change switch
    {
        ChartDiffChange.Added => _l["ChartDiff_Added"],
        ChartDiffChange.Removed => _l["ChartDiff_Removed"],
        _ => _l["ChartDiff_Changed"]
    };

    private static string Clock(double timeMs) => TimeSpan.FromMilliseconds(Math.Max(0, timeMs)).ToString(@"m\:ss\.fff");

    private Row ToRow(ChartSoundDiff entry, int difficultyIndex, IList<BeatMarker> beats)
    {
        var position = _l.Format("ChartDiff_Position", difficultyIndex, Clock(entry.Position.GetPositionMs(beats)));
        var sound = entry.Kind == ChartDiffSoundKind.Chord
            ? _l["ChartDiff_Chord"]
            : _l.Format("ChartDiff_NoteOnString", entry.StringIndex + 1);
        var detail = entry.ChangedFields.Count > 0 ? string.Join(", ", entry.ChangedFields) : DescribeSound(entry);
        return new Row(ChangeText(entry.Change), position, sound, detail, entry.Change);
    }

    private Row ToRow(ChartStructureDiff entry, IList<BeatMarker> beats)
    {
        // A chord template has no timeline position at all; only the per-level kinds have a difficulty.
        string position;
        if (entry.Kind is ChartDiffElementKind.ChordTemplate or ChartDiffElementKind.ArrangementHeader)
        {
            position = _l["ChartDiff_PositionNone"];
        }
        else
        {
            var time = Clock(entry.Position.GetPositionMs(beats));
            position = entry.DifficultyIndex >= 0
                ? _l.Format("ChartDiff_Position", entry.DifficultyIndex, time)
                : _l.Format("ChartDiff_PositionArrangement", time);
        }

        // Spelled out, not composed from the enum name, so the localization sweep can see every key.
        var kind = entry.Kind switch
        {
            ChartDiffElementKind.Anchor => _l["ChartDiff_Anchor"],
            ChartDiffElementKind.HandShape => _l["ChartDiff_HandShape"],
            ChartDiffElementKind.FretHandMute => _l["ChartDiff_FretHandMute"],
            ChartDiffElementKind.EventPoint => _l["ChartDiff_EventPoint"],
            ChartDiffElementKind.ToneChange => _l["ChartDiff_ToneChange"],
            ChartDiffElementKind.ChordTemplate => _l["ChartDiff_ChordTemplate"],
            ChartDiffElementKind.PhraseIteration => _l["ChartDiff_PhraseIteration"],
            ChartDiffElementKind.ArrangementHeader => _l["ChartDiff_ArrangementHeader"],
            _ => entry.Kind.ToString()
        };
        var detail = entry.ChangedFields.Count > 0 ? string.Join(", ", entry.ChangedFields) : entry.Label;
        return new Row(ChangeText(entry.Change), position, kind, detail, entry.Change);
    }

    /// <summary>An added or removed sound carries no changed fields; the fret identifies it instead.</summary>
    private string DescribeSound(ChartSoundDiff entry)
    {
        var note = entry.NewNote ?? entry.OldNote;
        if (note != null) return _l.Format("ChartDiff_Fret", note.Fret);
        var chord = entry.NewChord ?? entry.OldChord;
        return chord != null ? _l.Plural("ChartDiff_ChordNotesOne", "ChartDiff_ChordNotes", chord.ChordNotes.Count) : "";
    }

    private sealed record Row(string Change, string Position, string Sound, string Detail, ChartDiffChange Kind)
        : IChartDiffRow
    {
        public bool IsAdded => Kind == ChartDiffChange.Added;

        public bool IsRemoved => Kind == ChartDiffChange.Removed;

        public bool IsChanged => Kind == ChartDiffChange.Changed;
    }
}
