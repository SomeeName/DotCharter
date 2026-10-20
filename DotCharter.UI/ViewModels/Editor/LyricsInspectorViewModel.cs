using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Analysis.Lyrics;
using DotCharter.Services.Audio.Lyrics;
using DotCharter.Services.Export;
using DotCharter.Services.Parsing.Lrc;
using DotCharter.Services.Parsing.UltraStar;
using DotCharter.Tone.UI.ViewModels.Shell;

namespace DotCharter.UI.ViewModels.Editor;

/// <summary>
///     Inline lyric tapping. The user pastes a multi-line block of raw lyrics
///     into <see cref="RawLyrics" /> (syllables split with <c>-</c>, words split
///     with space, phrases split with newline) and presses
///     <see cref="ApplyCommand" /> to load the syllable queue. During playback
///     <c>Ctrl+Space</c> calls <see cref="TapNext" /> which anchors the next
///     syllable at the current playhead. Each syllable becomes its own
///     <see cref="LyricEvent" /> in the active vocal track and is committed via
///     <see cref="SetVocalPhrasesAction" /> so it participates in undo/redo.
/// </summary>
public partial class LyricsInspectorViewModel : ViewModelBase
{
    private const double DefaultLastSyllableMs = 250.0;

    private readonly ILyricAutoSyncService _autoSync;

    /// <summary>Null in tests — auto-sync then runs with product-default options.</summary>
    private readonly IConfigurationService? _configService;

    /// <summary>Null when no downloader is wired — the models then have to be present already.</summary>
    private readonly ILyricModelDownloadService? _modelDownloads;
    private readonly ChartEditorViewModel _chartEditor;
    private readonly IHistoryService _history;
    private readonly LrcLyricsParser _lrcParser;
    private readonly IAppLogger _logger;
    private readonly ILocalizationService _localization;
    private readonly ProjectState _projectState;
    private readonly IFileStorageService _storageService;
    private readonly IExportTargetWriter _files;

    // Flat queue across phrases; each item carries its phrase index so we can
    // group LyricEvents back into LyricPhrase blocks at commit time.
    private readonly List<QueueItem> _queue = [];

    [ObservableProperty] private string _rawLyrics = "";
    [ObservableProperty] private bool _isArmed;
    [ObservableProperty] private bool _isAutoSyncing;
    [ObservableProperty] private int _remainingCount;
    [ObservableProperty] private string _nextPreview = "";
    [ObservableProperty] private string _statusText = "";

    [ObservableProperty] private bool _hasSelectedLyric;
    [ObservableProperty] private string _selectedLyricText = "";
    [ObservableProperty] private bool _selectedLyricIsWordPart;
    [ObservableProperty] private bool _selectedLyricIsPhraseEnd;
    [ObservableProperty] private bool _selectedLyricHasPitch;
    [ObservableProperty] private int _selectedLyricNote = DefaultPitch;
    [ObservableProperty] private string _selectedLyricNoteName = "";
    private LyricEvent? _selectedLyricRef;
    private bool _suppressSelectedSync;

    private const int DefaultPitch = 60; // C4 — a comfortable default sung note.

    public LyricsInspectorViewModel(
        ChartEditorViewModel chartEditor,
        IHistoryService history,
        ProjectState projectState,
        IAppLogger logger,
        IFileStorageService storageService,
        LrcLyricsParser lrcParser,
        ILyricAutoSyncService autoSync,
        ILocalizationService localization,
        ILyricModelDownloadService? modelDownloads = null,
        IConfigurationService? configService = null,
        IExportTargetWriter? files = null)
    {
        _files = files ?? new ExportTargetWriter();
        _configService = configService;
        _modelDownloads = modelDownloads;
        _chartEditor = chartEditor;
        _history = history;
        _projectState = projectState;
        _logger = logger;
        _storageService = storageService;
        _lrcParser = lrcParser;
        _autoSync = autoSync;
        _localization = localization;
        StatusText = _localization["Inspector_Lyrics_StatusIdle"];

        if (_chartEditor.Selection is INotifyPropertyChanged npc)
        {
            npc.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(ISelectionService.SelectedLyric))
                    SyncFromSelection();
            };
        }
    }

    private void SyncFromSelection()
    {
        var lyric = _chartEditor.Selection.SelectedLyric;
        _selectedLyricRef = lyric;
        _suppressSelectedSync = true;
        try
        {
            HasSelectedLyric = lyric != null;
            SelectedLyricText = lyric?.Text ?? "";
            SelectedLyricIsWordPart = lyric?.IsWordPart ?? false;
            SelectedLyricIsPhraseEnd = lyric?.IsPhraseEnd ?? false;

            var hasPitch = lyric != null && lyric.Note is > 0 and < 255;
            SelectedLyricHasPitch = hasPitch;
            SelectedLyricNote = hasPitch ? lyric!.Note : DefaultPitch;
            SelectedLyricNoteName = hasPitch
                ? MidiToNoteName(lyric!.Note)
                : _localization["Inspector_Lyrics_NoPitch"];
        }
        finally { _suppressSelectedSync = false; }
    }

    partial void OnSelectedLyricTextChanged(string value)
    {
        if (_suppressSelectedSync || _selectedLyricRef == null) return;
        if (_selectedLyricRef.Text == value) return;
        var action = new RenameLyricAction(_selectedLyricRef, _selectedLyricRef.Text, value,
            _selectedLyricRef.IsWordPart, _selectedLyricRef.IsWordPart,
            _selectedLyricRef.IsPhraseEnd, _selectedLyricRef.IsPhraseEnd);
        _history.Do(action);
        _chartEditor.RequestRedraw();
    }

    partial void OnSelectedLyricIsWordPartChanged(bool value)
    {
        if (_suppressSelectedSync || _selectedLyricRef == null) return;
        if (_selectedLyricRef.IsWordPart == value) return;
        var action = new RenameLyricAction(_selectedLyricRef, _selectedLyricRef.Text, _selectedLyricRef.Text,
            _selectedLyricRef.IsWordPart, value,
            _selectedLyricRef.IsPhraseEnd, _selectedLyricRef.IsPhraseEnd);
        _history.Do(action);
        _chartEditor.RequestRedraw();
    }

    /// <summary>
    /// Toggles <see cref="LyricEvent.IsWordPart"/> on the currently selected
    /// lyric. Mirrors RsCharter's <c>TOGGLE_WORD_PART</c> action so the user
    /// can flip the word-part flag with a keybind without opening the
    /// inspector. No-ops when no lyric is selected.
    /// </summary>
    public void ToggleSelectedWordPart()
    {
        if (_selectedLyricRef == null)
        {
            _projectState.SetMessage(_localization["Status_NoLyricSelected"]);
            return;
        }
        SelectedLyricIsWordPart = !SelectedLyricIsWordPart;
    }

    /// <summary>
    /// Toggles <see cref="LyricEvent.IsPhraseEnd"/> on the currently selected
    /// lyric. Symmetric counterpart to <see cref="ToggleSelectedWordPart"/>.
    /// No-ops when no lyric is selected.
    /// </summary>
    public void ToggleSelectedPhraseEnd()
    {
        if (_selectedLyricRef == null)
        {
            _projectState.SetMessage(_localization["Status_NoLyricSelected"]);
            return;
        }
        SelectedLyricIsPhraseEnd = !SelectedLyricIsPhraseEnd;
    }

    partial void OnSelectedLyricIsPhraseEndChanged(bool value)
    {
        if (_suppressSelectedSync || _selectedLyricRef == null) return;
        if (_selectedLyricRef.IsPhraseEnd == value) return;
        var action = new RenameLyricAction(_selectedLyricRef, _selectedLyricRef.Text, _selectedLyricRef.Text,
            _selectedLyricRef.IsWordPart, _selectedLyricRef.IsWordPart,
            _selectedLyricRef.IsPhraseEnd, value);
        _history.Do(action);
        _chartEditor.RequestRedraw();
    }

    // Turning pitch on assigns the current spinner value; turning it off stores
    // the 255 "no pitch" (spoken) sentinel. Both go through the undo stack.
    partial void OnSelectedLyricHasPitchChanged(bool value)
    {
        if (_suppressSelectedSync || _selectedLyricRef == null) return;
        var newNote = value ? ClampPitch(SelectedLyricNote) : 255;
        if (_selectedLyricRef.Note == newNote)
        {
            SelectedLyricNoteName = value ? MidiToNoteName(newNote) : _localization["Inspector_Lyrics_NoPitch"];
            return;
        }
        _history.Do(new SetLyricNoteAction(_selectedLyricRef, _selectedLyricRef.Note, newNote));
        SelectedLyricNoteName = value
            ? MidiToNoteName(newNote)
            : _localization["Inspector_Lyrics_NoPitch"];
        _chartEditor.RequestRedraw();
    }

    partial void OnSelectedLyricNoteChanged(int value)
    {
        if (_suppressSelectedSync) return;
        SelectedLyricNoteName = SelectedLyricHasPitch
            ? MidiToNoteName(value)
            : _localization["Inspector_Lyrics_NoPitch"];
        // Only mutate the model when the syllable is pitched; adjusting the
        // spinner while "pitched" is off just parks a default for later.
        if (_selectedLyricRef == null || !SelectedLyricHasPitch) return;
        var clamped = ClampPitch(value);
        if (_selectedLyricRef.Note == clamped) return;
        _history.Do(new SetLyricNoteAction(_selectedLyricRef, _selectedLyricRef.Note, clamped));
        _chartEditor.RequestRedraw();
    }

    private static int ClampPitch(int midi) => Math.Clamp(midi, 0, 127);

    private static readonly string[] NoteNames =
        ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];

    /// <summary>Formats a MIDI note number as a scientific pitch name (60 -> "C4").</summary>
    internal static string MidiToNoteName(int midi)
    {
        if (midi is < 0 or > 127) return midi.ToString();
        var name = NoteNames[midi % 12];
        var octave = midi / 12 - 1;
        return $"{name}{octave}";
    }

    [RelayCommand]
    private async Task ImportLrc()
    {
        if (_projectState.ActiveProject == null)
        {
            _projectState.SetMessage(_localization["Status_NoChartLoaded"]);
            return;
        }

        if (_chartEditor.Beats.Count == 0)
        {
            _projectState.SetMessage(_localization["Status_LrcImport_NoBeats"]);
            return;
        }

        var startDir = Path.GetDirectoryName(Path.GetFullPath(
            string.IsNullOrWhiteSpace(_projectState.CurrentProjectPath) ? "." : _projectState.CurrentProjectPath));

        var filePath = await _storageService.OpenFilePickerAsync(
            _localization["Picker_SelectLrcFile"],
            new[]
            {
                new FilePickerFilter("LRC Lyrics", new[] { "*.lrc" }),
                FilePickerFilters.AllFiles
            },
            startDir);
        if (string.IsNullOrWhiteSpace(filePath))
            return;

        try
        {
            var content = await _files.ReadAllTextAsync(filePath);
            var phrases = _lrcParser.Parse(content, _chartEditor.Beats);
            if (phrases.Count == 0)
            {
                _projectState.SetMessage(_localization["Inspector_Lyrics_StatusEmpty"]);
                return;
            }

            var track = EnsureActiveVocalTrack();
            if (ReplacePhrases(track, phrases))
            {
                Reset();
                _chartEditor.RequestRedraw();
                var status = _localization.Format("Status_LrcImport_Applied", phrases.Count,
                    phrases.Sum(p => p.Lyrics.Count));
                StatusText = status;
                _projectState.SetMessage(status);
                _logger.Info(AppLogCategory.Import,
                    $"Imported LRC lyrics from '{filePath}'. phrases={phrases.Count}, lyrics={phrases.Sum(p => p.Lyrics.Count)}.");
            }
        }
        catch (Exception ex)
        {
            _logger.Error(AppLogCategory.Import, $"LRC import failed for '{filePath}'.", ex);
            _projectState.SetMessage(_localization.Format("Status_LrcImport_Failed", ex.Message), StatusSeverity.Error);
        }
    }

    [RelayCommand]
    private async Task ExportLrc()
    {
        if (!TryGetExportTrack(out var track)) return;
        var project = _projectState.ActiveProject!;
        try
        {
            var text = new LrcLyricsWriter().Write(track, _chartEditor.Beats,
                project.Chart.SongName, project.Chart.Artist);
            var path = await SaveLyricFileAsync(
                _localization["Picker_SaveLrcFile"],
                SafeFileName(project.Chart.SongName) + ".lrc",
                "LRC Lyrics", "*.lrc");
            if (string.IsNullOrWhiteSpace(path)) return;

            // AtomicFile: a bare write truncates first, so a crash mid-export
            // leaves an empty file where the previous export was.
            await Task.Run(() => AtomicFile.WriteAllText(path, text));
            var status = _localization.Format("Status_LrcExport_Saved", path);
            _projectState.SetMessage(status);
            _logger.Info(AppLogCategory.Project, $"Exported LRC lyrics to '{path}'.");
        }
        catch (Exception ex)
        {
            _logger.Error(AppLogCategory.Project, "LRC export failed.", ex);
            _projectState.SetMessage(_localization.Format("Status_LrcExport_Failed", ex.Message), StatusSeverity.Error);
        }
    }

    [RelayCommand]
    private async Task ImportUltraStar()
    {
        if (_projectState.ActiveProject == null)
        {
            _projectState.SetMessage(_localization["Status_NoChartLoaded"]);
            return;
        }
        if (_chartEditor.Beats.Count == 0)
        {
            _projectState.SetMessage(_localization["Status_UltraStarImport_NoBeats"]);
            return;
        }

        var startDir = Path.GetDirectoryName(Path.GetFullPath(
            string.IsNullOrWhiteSpace(_projectState.CurrentProjectPath) ? "." : _projectState.CurrentProjectPath));

        var filePath = await _storageService.OpenFilePickerAsync(
            _localization["Picker_SelectUltraStarFile"],
            new[]
            {
                new FilePickerFilter("UltraStar Song", new[] { "*.txt" }),
                FilePickerFilters.AllFiles
            },
            startDir);
        if (string.IsNullOrWhiteSpace(filePath))
            return;

        try
        {
            var content = await _files.ReadAllTextAsync(filePath);
            var song = new UltraStarLyricsParser().Parse(content);
            // One track per voice: a duet's second singer goes into a NEW vocal track
            // rather than interleaving with the first in the active one.
            var voices = UltraStarVocalConverter.ToVocalTracks(song, _chartEditor.Beats);
            var phrases = voices[0].Phrases.ToList();
            if (phrases.Count == 0)
            {
                _projectState.SetMessage(_localization["Status_UltraStarImport_Empty"]);
                return;
            }

            var track = EnsureActiveVocalTrack();
            var addedTracks = voices.Skip(1).ToList();
            if (ReplacePhrases(track, phrases, addedTracks))
            {
                Reset();
                _chartEditor.RequestRedraw();
                var phraseCount = voices.Sum(v => v.Phrases.Count);
                var lyricCount = voices.Sum(v => v.Phrases.Sum(p => p.Lyrics.Count));
                var status = _localization.Format(
                    voices.Count > 1 ? "Status_UltraStarImport_AppliedDuet" : "Status_UltraStarImport_Applied",
                    voices.Count > 1 ? [voices.Count, phraseCount, lyricCount] : [phraseCount, lyricCount]);
                StatusText = status;
                _projectState.SetMessage(status);
                _logger.Info(AppLogCategory.Import,
                    $"Imported UltraStar lyrics from '{filePath}'. voices={voices.Count}, relative={song.IsRelative}, " +
                    $"phrases={phraseCount}, lyrics={lyricCount}.");
            }
        }
        catch (Exception ex)
        {
            _logger.Error(AppLogCategory.Import, $"UltraStar import failed for '{filePath}'.", ex);
            _projectState.SetMessage(_localization.Format("Status_UltraStarImport_Failed", ex.Message), StatusSeverity.Error);
        }
    }

    [RelayCommand]
    private async Task ExportUltraStar()
    {
        if (!TryGetExportTrack(out var track)) return;
        var project = _projectState.ActiveProject!;
        try
        {
            var audio = string.IsNullOrWhiteSpace(project.AudioFileName)
                ? null : Path.GetFileName(project.AudioFileName);
            var song = UltraStarVocalConverter.ToSong(track, _chartEditor.Beats,
                project.Bpm, gapMs: 0,
                project.Chart.SongName, project.Chart.Artist, audio);
            var text = new UltraStarLyricsWriter().Write(song);
            var path = await SaveLyricFileAsync(
                _localization["Picker_SaveUltraStarFile"],
                SafeFileName(project.Chart.SongName) + ".txt",
                "UltraStar Song", "*.txt");
            if (string.IsNullOrWhiteSpace(path)) return;

            // AtomicFile: a bare write truncates first, so a crash mid-export
            // leaves an empty file where the previous export was.
            await Task.Run(() => AtomicFile.WriteAllText(path, text));
            var status = _localization.Format("Status_UltraStarExport_Saved", path);
            _projectState.SetMessage(status);
            _logger.Info(AppLogCategory.Project, $"Exported UltraStar lyrics to '{path}'.");
        }
        catch (Exception ex)
        {
            _logger.Error(AppLogCategory.Project, "UltraStar export failed.", ex);
            _projectState.SetMessage(_localization.Format("Status_UltraStarExport_Failed", ex.Message), StatusSeverity.Error);
        }
    }

    // Resolves the active vocal track for export, or shows a status message and
    // returns false when there is no chart / no lyrics to write.
    private bool TryGetExportTrack(out VocalTrack track)
    {
        track = null!;
        if (_projectState.ActiveProject == null)
        {
            _projectState.SetMessage(_localization["Status_NoChartLoaded"]);
            return false;
        }
        // A play-only song's lyrics are the source's too: play-only.
        if (PlayOnlyExportGuard.IsPlayOnly(_projectState.ActiveProject))
        {
            _projectState.SetMessage(_localization[PlayOnlyExportGuard.MessageKey], StatusSeverity.Error);
            return false;
        }
        var active = _chartEditor.ActiveVocalTrack;
        if (active == null || active.Phrases.All(p => p.Lyrics.Count == 0))
        {
            _projectState.SetMessage(_localization["Status_LyricExport_NoLyrics"]);
            return false;
        }
        track = active;
        return true;
    }

    private Task<string?> SaveLyricFileAsync(string title, string suggestedName, string filterName, string pattern)
    {
        var startDir = Path.GetDirectoryName(Path.GetFullPath(
            string.IsNullOrWhiteSpace(_projectState.CurrentProjectPath) ? "." : _projectState.CurrentProjectPath));
        return _storageService.SaveFilePickerAsync(title, suggestedName,
            new[]
            {
                new FilePickerFilter(filterName, new[] { pattern }),
                FilePickerFilters.AllFiles
            },
            startDir);
    }

    private static string SafeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "lyrics";
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return cleaned.Length == 0 ? "lyrics" : cleaned;
    }

    [RelayCommand]
    private void DeleteSelectedLyric()
    {
        var lyric = _selectedLyricRef;
        var track = _chartEditor.ActiveVocalTrack;
        if (lyric == null || track == null) return;
        var phrase = track.Phrases.FirstOrDefault(p => p.Lyrics.Contains(lyric));
        if (phrase == null) return;
        _history.Do(new RemoveLyricAction(track.Phrases, phrase, lyric));
        _chartEditor.Selection.SelectedLyric = null;
        _chartEditor.RequestRedraw();
    }

    /// <summary>
    ///     Places every syllable in <see cref="RawLyrics" /> automatically by
    ///     listening to the song, instead of arming the queue for
    ///     <see cref="TapNext" /> to tap in by hand.
    ///     <para>
    ///         It reads the same box and the same format as
    ///         <see cref="ApplyCommand" /> — lines are phrases, spaces are words,
    ///         <c>-</c> splits syllables — so tapping stays available and the two
    ///         are interchangeable on the same pasted text. Japanese belongs here
    ///         as romaji: the acoustic model aligns romanized text, which is what
    ///         the owner charts anyway.
    ///     </para>
    ///     <para>
    ///         Measured over the 2,036 hand-synced syllables in
    ///         <c>TestData/EOF</c>: 90% of onsets land within 100 ms of where a
    ///         human put them, 76% within 50 ms. So this is a strong first pass,
    ///         not a finished sync — it is committed through
    ///         <see cref="SetVocalPhrasesAction" /> like every other bulk lyric
    ///         edit, so one undo takes it all back.
    ///     </para>
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAutoSync))]
    private async Task AutoSync()
    {
        if (_projectState.ActiveProject == null)
        {
            _projectState.SetMessage(_localization["Status_NoChartLoaded"]);
            return;
        }

        if (string.IsNullOrWhiteSpace(RawLyrics))
        {
            SetStatus(_localization["Status_LyricAutoSync_Empty"]);
            return;
        }

        if (_chartEditor.Beats.Count == 0)
        {
            SetStatus(_localization["Status_LyricAutoSync_NoBeats"]);
            return;
        }

        // The models are a one-time download, so a missing one is a step to take
        // rather than a dead end. Fetch what THIS sheet needs — the multilingual
        // model always, the English head only when the router wants it — and only
        // report "no model" if that could not be done.
        if (_modelDownloads != null)
        {
            var sheet = LyricSheetParser.Parse(RawLyrics);
            var megabytes = _modelDownloads.PendingMegabytesFor(sheet);
            if (megabytes > 0)
            {
                IsAutoSyncing = true;
                AutoSyncCommand.NotifyCanExecuteChanged();
                try
                {
                    SetStatus(_localization.Format(
                        "Status_LyricAutoSync_Downloading", megabytes));
                    var progress = new Progress<string>(step => StatusText =
                        _localization.Format("Status_LyricAutoSync_Running", step));

                    if (!await _modelDownloads.EnsureModelsForAsync(sheet, progress))
                    {
                        SetStatus(_localization.Format(
                            "Status_LyricAutoSync_NoModel", _autoSync.ModelDirectory));
                        return;
                    }
                }
                finally
                {
                    IsAutoSyncing = false;
                    AutoSyncCommand.NotifyCanExecuteChanged();
                }
            }
        }

        if (!_autoSync.IsModelInstalled)
        {
            SetStatus(_localization.Format(
                "Status_LyricAutoSync_NoModel", _autoSync.ModelDirectory));
            return;
        }

        var audioPath = ProjectAudioLocator.TryResolve(
            _projectState.CurrentProjectPath, _projectState.CurrentAudioFileName);
        if (string.IsNullOrWhiteSpace(audioPath))
        {
            SetStatus(_localization["Status_LyricAutoSync_NoAudio"]);
            return;
        }

        IsAutoSyncing = true;
        AutoSyncCommand.NotifyCanExecuteChanged();
        try
        {
            var progress = new Progress<string>(step => StatusText =
                _localization.Format("Status_LyricAutoSync_Running", step));

            // Options come from the editor settings (span word/phrase gaps, the
            // note-spacing minimum) — built per run so a settings change is
            // live on the next sync without restarting anything.
            var config = _configService?.LoadAppConfig();
            var options = config != null ? LyricAutoSyncOptions.FromConfig(config) : LyricAutoSyncOptions.Default;

            // SNAPSHOT the grid, and remember WHICH project this run is for.
            // `_chartEditor.Beats` is the live ObservableCollection off
            // ProjectState.ActiveProject, and this await is a multi-second
            // decode + CTC alignment on a worker: handing the live instance over
            // lets an edit (or a project OPEN, which replaces the whole
            // collection) mutate the grid the aligner is walking. A List copy
            // costs nothing next to the decode and cannot be torn.
            var syncedProject = _projectState.ActiveProject;
            var beats = _chartEditor.Beats.ToList();

            var outcome = await _autoSync.SyncAsync(
                RawLyrics, audioPath, beats, options, progress);

            // …and a result computed against THAT project's grid may only be
            // applied to THAT project. Identity, not equality: a different
            // instance is a different chart even when its metadata matches, and
            // writing these phrases into it would time somebody else's lyrics to
            // the previous song's beats.
            if (!ReferenceEquals(_projectState.ActiveProject, syncedProject))
            {
                _logger.Info(AppLogCategory.Import,
                    "Lyric auto-sync discarded: the active project changed while it ran.");
                SetStatus(_localization["Status_LyricAutoSync_ProjectChanged"]);
                return;
            }

            if (!outcome.Ran || outcome.Phrases.Count == 0)
            {
                SetStatus(_localization.Format(
                    "Status_LyricAutoSync_Failed", outcome.Error ?? "no phrases"));
                return;
            }

            var track = EnsureActiveVocalTrack();
            if (!ReplacePhrases(track, outcome.Phrases.ToList())) return;

            Reset();
            _chartEditor.RequestRedraw();
            SetStatus(_localization.Format("Status_LyricAutoSync_Applied",
                outcome.SyllableCount, outcome.Phrases.Count, outcome.AudioSource,
                outcome.LowConfidenceCount + outcome.InterpolatedCount));
            _logger.Info(AppLogCategory.Import,
                $"Lyric auto-sync applied. syllables={outcome.SyllableCount}, " +
                $"phrases={outcome.Phrases.Count}, source={outcome.AudioSource}, " +
                $"lowConfidence={outcome.LowConfidenceCount}, interpolated={outcome.InterpolatedCount}.");
        }
        catch (Exception ex)
        {
            _logger.Error(AppLogCategory.Import, "Lyric auto-sync failed.", ex);
            SetStatus(_localization.Format("Status_LyricAutoSync_Failed", ex.Message));
        }
        finally
        {
            IsAutoSyncing = false;
            AutoSyncCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanAutoSync() => !IsAutoSyncing;

    private void SetStatus(string status)
    {
        StatusText = status;
        _projectState.SetMessage(status);
    }

    [RelayCommand]
    private void Apply()
    {
        _queue.Clear();
        var lines = RawLyrics.Replace("\r\n", "\n").Split('\n');
        var phraseIdx = 0;
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            var phraseHadSyllable = false;
            foreach (var word in line.Split(' ', '\t', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = word.Split('-');
                for (var i = 0; i < parts.Length; i++)
                {
                    var text = parts[i];
                    if (text.Length == 0) continue;
                    var isWordPart = i < parts.Length - 1;
                    // Store the syllable text WITHOUT the trailing dash —
                    // the dash is just an input-syntax separator, the
                    // continuation is encoded by IsWordPart and rendered
                    // as a connector line between adjacent pills.
                    _queue.Add(new QueueItem(phraseIdx, text, isWordPart));
                    phraseHadSyllable = true;
                }
            }
            if (phraseHadSyllable) phraseIdx++;
        }

        if (_queue.Count == 0)
        {
            IsArmed = false;
            RemainingCount = 0;
            NextPreview = "";
            StatusText = _localization["Inspector_Lyrics_StatusEmpty"];
            return;
        }

        _phrasesByIndex.Clear();
        IsArmed = true;
        RemainingCount = _queue.Count;
        UpdatePreview();
        StatusText = _localization.Plural("Inspector_Lyrics_StatusArmedOne", "Inspector_Lyrics_StatusArmed", _queue.Count);
    }

    [RelayCommand]
    private void Reset()
    {
        _queue.Clear();
        _phrasesByIndex.Clear();
        IsArmed = false;
        RemainingCount = 0;
        NextPreview = "";
        StatusText = _localization["Inspector_Lyrics_StatusIdle"];
    }

    // Maps queue PhraseIndex → the LyricPhrase we appended to last for it.
    // Reference is stable across undo/redo of SetVocalPhrasesAction because
    // the same instance is re-added on redo.
    private readonly Dictionary<int, LyricPhrase> _phrasesByIndex = [];

    /// <summary>
    ///     Pops the next syllable from the queue and commits it to the active
    ///     vocal track at the current playhead. Bound to <c>Ctrl+Space</c>.
    /// </summary>
    public void TapNext()
    {
        if (!IsArmed || _queue.Count == 0)
        {
            _projectState.SetMessage(_localization["Status_LyricTap_NotArmed"]);
            return;
        }
        var track = _chartEditor.ActiveVocalTrack;
        if (track == null)
        {
            _projectState.SetMessage(_localization["Status_LyricTap_NoVocalTrack"]);
            return;
        }
        var beats = _chartEditor.Beats;
        if (beats.Count == 0)
        {
            _projectState.SetMessage(_localization["Status_LyricTap_NoBeats"]);
            return;
        }

        var item = _queue[0];
        var tapMs = _projectState.CurrentTimeMs;
        var startPos = TimelineMath.PositionFromMs(tapMs, beats);
        var endMs = tapMs + DefaultLastSyllableMs;
        var endPos = TimelineMath.PositionFromMs(endMs, beats);

        // Decide which phrase the new syllable lands in WITHOUT mutating
        // anything yet — the LyricTapAction does the actual chart mutation
        // in its Execute() so initial-commit and redo are identical.
        _phrasesByIndex.TryGetValue(item.PhraseIndex, out var existingPhrase);
        var hadPhraseInMap = existingPhrase != null;
        var phraseStillInTrack = existingPhrase != null && track.Phrases.Contains(existingPhrase);
        var targetPhrase = phraseStillInTrack ? existingPhrase! : new LyricPhrase
        {
            Position = startPos,
            EndPosition = endPos
        };
        var newLyric = new LyricEvent
        {
            Text = item.Text,
            IsWordPart = item.IsWordPart,
            Position = startPos,
            EndPosition = endPos
        };

        var prevLyric = phraseStillInTrack && targetPhrase.Lyrics.Count > 0
            ? targetPhrase.Lyrics[^1] : null;
        var prevEndBefore = prevLyric?.EndPosition;
        var trimPrevTo = prevLyric != null && prevEndBefore!.Value.GetPositionMs(beats) > tapMs - 1
            ? (FractionalPosition?)startPos : null;

        var phraseStartBefore = targetPhrase.Position;
        var phraseEndBefore = targetPhrase.EndPosition;
        FractionalPosition? phraseNewStart = phraseStartBefore.GetPositionMs(beats) > tapMs ? startPos : null;
        FractionalPosition? phraseNewEnd = phraseEndBefore.GetPositionMs(beats) < endMs ? endPos : null;

        var action = new LyricTapAction(this, track.Phrases, targetPhrase, newLyric, item,
            addPhraseToTrack: !phraseStillInTrack,
            hadPhraseInMap: hadPhraseInMap,
            prevLyric: prevLyric,
            prevEndBefore: prevEndBefore,
            trimPrevTo: trimPrevTo,
            phraseStartBefore: phraseStartBefore,
            phraseEndBefore: phraseEndBefore,
            phraseNewStart: phraseNewStart,
            phraseNewEnd: phraseNewEnd);

        _queue.RemoveAt(0);
        _history.Do(action);
        _logger.Info(AppLogCategory.Editing,
            $"Lyric tap '{item.Text}' at {tapMs:F0}ms, remaining={_queue.Count}.");

        RemainingCount = _queue.Count;
        UpdatePreview();
        if (_queue.Count == 0)
        {
            IsArmed = false;
            StatusText = _localization["Inspector_Lyrics_StatusDone"];
        }
    }

    // Called by LyricTapAction.Undo to restore the inspector queue/preview
    // state when the user undoes a tap. Pushes the syllable back to the
    // front of the queue, optionally drops the phrase mapping if the tap
    // had created the phrase, and refreshes preview/status.
    internal void RestoreTapForUndo(QueueItem item, bool addedPhrase, bool hadPhraseInMap)
    {
        _queue.Insert(0, item);
        if (addedPhrase && !hadPhraseInMap)
            _phrasesByIndex.Remove(item.PhraseIndex);
        IsArmed = true;
        RemainingCount = _queue.Count;
        UpdatePreview();
        StatusText = _localization.Plural("Inspector_Lyrics_StatusArmedOne", "Inspector_Lyrics_StatusArmed", _queue.Count);
    }

    // Called by LyricTapAction.Execute on redo to re-pop the syllable that
    // was restored to the queue and re-establish the phrase map.
    internal void ReapplyTapForRedo(QueueItem item, LyricPhrase phrase)
    {
        if (_queue.Count > 0 && _queue[0].PhraseIndex == item.PhraseIndex && _queue[0].Text == item.Text)
            _queue.RemoveAt(0);
        _phrasesByIndex[item.PhraseIndex] = phrase;
        RemainingCount = _queue.Count;
        UpdatePreview();
        if (_queue.Count == 0)
        {
            IsArmed = false;
            StatusText = _localization["Inspector_Lyrics_StatusDone"];
        }
        else
        {
            IsArmed = true;
            StatusText = _localization.Plural("Inspector_Lyrics_StatusArmedOne", "Inspector_Lyrics_StatusArmed", _queue.Count);
        }
    }

    private void UpdatePreview()
    {
        var ahead = _queue.Take(8).Select(q => q.Text);
        NextPreview = string.Join(" ", ahead);
    }

    /// <summary>
    ///     Commits a WHOLESALE phrase replacement (an LRC/UltraStar import, an
    ///     auto-sync) and drops the lyric selection when the replacement orphaned it.
    /// </summary>
    /// <remarks>
    ///     <see cref="SetVocalPhrasesAction" /> clears the track's phrase collection and
    ///     refills it with new objects, so a <c>Selection.SelectedLyric</c> from before
    ///     the import points at a <see cref="LyricEvent" /> that is not in any
    ///     phrase — and it is NOT inert. The inspector keeps showing the orphan's text,
    ///     word-part, phrase-end and pitch, and every one of those editors then pushes a
    ///     real undo node (<c>RenameLyricAction</c>, <c>SetLyricNoteAction</c>) that
    ///     mutates a detached object: the edit reports success, consumes an undo slot
    ///     and changes nothing on screen. <c>DeleteSelectedLyric</c> finds no owning
    ///     phrase and silently no-ops, <c>ChartRenderer</c> reads it into the frame
    ///     snapshot and <c>TimelineWheelAdjuster</c> resizes it.
    ///     <para>
    ///         <c>SelectionService.ClearSelections</c> already covers the project-swap
    ///         and snapshot-restore paths for exactly this reason; a bulk phrase
    ///         replacement goes through neither, so it needed its own.
    ///     </para>
    /// </remarks>
    private bool ReplacePhrases(VocalTrack track, IReadOnlyList<LyricPhrase> phrases,
        IReadOnlyList<VocalTrack>? addedTracks = null)
    {
        // An UltraStar duet's further voices are added in the SAME undo node as the
        // first voice's replacement, so one undo takes the whole import back.
        IChartAction action = new SetVocalPhrasesAction(track.Phrases, phrases);
        if (addedTracks is { Count: > 0 })
        {
            action = new CompositeAction(
                [action, .. addedTracks.Select(t => new AddVocalTrackAction(_chartEditor.VocalTracks, t))]);
        }

        if (!_history.Do(action)) return false;

        // Scoped to REACHABILITY, not to this track: a pick in another vocal track is
        // untouched by the replacement and dropping it would throw away a valid
        // selection.
        var selected = _chartEditor.Selection.SelectedLyric;
        if (selected != null && !_chartEditor.VocalTracks.Any(
                t => t.Phrases.Any(p => p.Lyrics.Contains(selected))))
        {
            _chartEditor.Selection.SelectedLyric = null;
        }

        return true;
    }

    private VocalTrack EnsureActiveVocalTrack()
    {
        if (_chartEditor.ActiveVocalTrack != null)
            return _chartEditor.ActiveVocalTrack;

        var track = new VocalTrack
        {
            Name = $"Vocals {_chartEditor.VocalTracks.Count + 1}"
        };
        _chartEditor.VocalTracks.Add(track);
        _chartEditor.ActiveVocalTrack = track;
        return track;
    }

    internal readonly record struct QueueItem(int PhraseIndex, string Text, bool IsWordPart);
}

/// <summary>
/// Wraps a single syllable-tap commit so undo/redo also moves the
/// LyricsInspector queue cursor back/forward AND fully reverts the
/// chart mutation (LyricEvent insert + previous-syllable tail trim +
/// new LyricPhrase add). Without this, the user hits Ctrl+Z to recover
/// from a mis-tap and either the syllable stays in the chart (because
/// in-place LyricPhrase mutation isn't covered by SetVocalPhrasesAction)
/// or the inspector queue advances past the recovered syllable.
/// </summary>
internal sealed class LyricTapAction : IChartAction
{
    private readonly LyricsInspectorViewModel _vm;
    private readonly ObservableCollection<LyricPhrase> _trackPhrases;
    private readonly LyricPhrase _targetPhrase;
    private readonly LyricEvent _newLyric;
    private readonly LyricsInspectorViewModel.QueueItem _item;
    private readonly bool _addPhraseToTrack;
    private readonly bool _hadPhraseInMap;
    private readonly LyricEvent? _prevLyric;
    private readonly FractionalPosition? _prevEndBefore;
    private readonly FractionalPosition? _trimPrevTo;
    private readonly FractionalPosition _phraseStartBefore;
    private readonly FractionalPosition _phraseEndBefore;
    private readonly FractionalPosition? _phraseNewStart;
    private readonly FractionalPosition? _phraseNewEnd;

    public LyricTapAction(LyricsInspectorViewModel vm,
        ObservableCollection<LyricPhrase> trackPhrases,
        LyricPhrase targetPhrase, LyricEvent newLyric, LyricsInspectorViewModel.QueueItem item,
        bool addPhraseToTrack, bool hadPhraseInMap,
        LyricEvent? prevLyric, FractionalPosition? prevEndBefore, FractionalPosition? trimPrevTo,
        FractionalPosition phraseStartBefore, FractionalPosition phraseEndBefore,
        FractionalPosition? phraseNewStart, FractionalPosition? phraseNewEnd)
    {
        _vm = vm;
        _trackPhrases = trackPhrases;
        _targetPhrase = targetPhrase;
        _newLyric = newLyric;
        _item = item;
        _addPhraseToTrack = addPhraseToTrack;
        _hadPhraseInMap = hadPhraseInMap;
        _prevLyric = prevLyric;
        _prevEndBefore = prevEndBefore;
        _trimPrevTo = trimPrevTo;
        _phraseStartBefore = phraseStartBefore;
        _phraseEndBefore = phraseEndBefore;
        _phraseNewStart = phraseNewStart;
        _phraseNewEnd = phraseNewEnd;
    }

    public bool HasEffect => true;

    public void Execute()
    {
        if (_trimPrevTo.HasValue && _prevLyric != null)
            _prevLyric.EndPosition = _trimPrevTo.Value;
        if (_phraseNewStart.HasValue) _targetPhrase.Position = _phraseNewStart.Value;
        if (_phraseNewEnd.HasValue) _targetPhrase.EndPosition = _phraseNewEnd.Value;
        if (!_targetPhrase.Lyrics.Contains(_newLyric))
            _targetPhrase.Lyrics.Add(_newLyric);
        if (_addPhraseToTrack && !_trackPhrases.Contains(_targetPhrase))
        {
            // Insert in time-sorted order so the rendered ribbon stays tidy.
            var idx = 0;
            while (idx < _trackPhrases.Count &&
                   _trackPhrases[idx].Position.CompareTo(_targetPhrase.Position) <= 0)
            {
                idx++;
            }

            _trackPhrases.Insert(idx, _targetPhrase);
        }
        _vm.ReapplyTapForRedo(_item, _targetPhrase);
    }

    public void Undo()
    {
        _targetPhrase.Lyrics.Remove(_newLyric);
        if (_addPhraseToTrack)
            _trackPhrases.Remove(_targetPhrase);
        if (_phraseNewStart.HasValue) _targetPhrase.Position = _phraseStartBefore;
        if (_phraseNewEnd.HasValue) _targetPhrase.EndPosition = _phraseEndBefore;
        if (_prevLyric != null && _prevEndBefore.HasValue)
            _prevLyric.EndPosition = _prevEndBefore.Value;
        _vm.RestoreTapForUndo(_item, _addPhraseToTrack, _hadPhraseInMap);
    }
}

