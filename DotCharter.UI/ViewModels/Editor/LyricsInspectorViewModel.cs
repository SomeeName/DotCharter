using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Services.Editing.Lyrics;
using DotCharter.Services.Export;
using DotCharter.Services.Import.Lyrics;
using DotCharter.Services.Infrastructure;
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

    private readonly ILyricAutoSyncRunner _autoSync;
    private readonly ChartEditorViewModel _chartEditor;
    private readonly IHistoryService _history;
    private readonly IAppLogger _logger;
    private readonly ILocalizationService _localization;
    private readonly ProjectState _projectState;
    private readonly IFileStorageService _storageService;
    private readonly ILyricsFileService _files;
    private readonly IConfigurationService? _configService;

    private readonly LyricTapSession _tap = new();

    [ObservableProperty] private string _rawLyrics = "";
    [ObservableProperty] private bool _isArmed;
    [ObservableProperty] private bool _isAutoSyncing;
    [ObservableProperty] private int _remainingCount;
    [ObservableProperty] private string _nextPreview = "";
    [ObservableProperty] private string _statusText = "";
    private Func<string> _status = () => "";

    [ObservableProperty] private bool _hasSelectedLyric;
    [ObservableProperty] private string _selectedLyricText = "";
    [ObservableProperty] private bool _selectedLyricIsWordPart;
    [ObservableProperty] private bool _selectedLyricIsPhraseEnd;
    [ObservableProperty] private bool _selectedLyricHasPitch;
    [ObservableProperty] private int _selectedLyricNote = DefaultPitch;
    [ObservableProperty] private string _selectedLyricNoteName = "";
    private LyricEvent? _selectedLyricRef;
    private string _draftOrigin = "";
    private bool _suppressSelectedSync;

    /// <summary>
    ///     A draft commit asked for while the history applies an undo/redo step
    ///     (<see cref="IHistoryService.IsApplyingStep" />: the app's focus request clears the selection
    ///     before the step has applied) is DEFERRED to the step's end (<see cref="IHistoryService.StepEnded" />,
    ///     raised on every exit), rather than dropped: deferring loses no typed text and leaves the
    ///     undo/redo step intact. A step that applied commits it as a new action like any other; one that
    ///     FAILED never does, so no later, unrelated edit commits it on top of a half-applied chart: it
    ///     returns to the box as a pending draft, its syllable selected again. Either way the rule applies —
    ///     a syllable whose stored text changed from the draft's origin, or that left the chart, drops it.
    /// </summary>
    private (LyricEvent Lyric, string Draft, string Origin)? _deferredDraft;

    private const int DefaultPitch = 60; // C4 — a comfortable default sung note.

    public LyricsInspectorViewModel(
        ChartEditorViewModel chartEditor,
        IHistoryService history,
        ProjectState projectState,
        IAppLogger logger,
        IFileStorageService storageService,
        ILyricsFileService files,
        ILyricAutoSyncRunner autoSync,
        ILocalizationService localization,
        IConfigurationService? configService = null)
    {
        _files = files;
        _configService = configService;
        _chartEditor = chartEditor;
        _history = history;
        _projectState = projectState;
        _logger = logger;
        _storageService = storageService;
        _autoSync = autoSync;
        _localization = localization;
        ShowStatus(() => _localization["Inspector_Lyrics_StatusIdle"]);
        WeakLanguageSubscription.Subscribe(_localization, this, static vm => vm.StatusText = vm._status());
        _tap.Changed += OnTapChanged;
        _history.HistoryChanged += SyncFromSelection;
        _history.StepEnded += OnStepEnded;
        _chartEditor.AddPendingEditCommit(CommitPendingEdit);
        Syllables = new LyricSyllableList(chartEditor, history);

        if (_chartEditor.Selection is INotifyPropertyChanged npc)
        {
            npc.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(ISelectionService.SelectedLyric))
                    SyncFromSelection();
            };
        }
        _chartEditor.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ChartEditorViewModel.ActiveVocalTrack)) RefreshReview();
        };
    }

    private void OnStepEnded(bool completed)
    {
        if (_deferredDraft is not { } deferred) return;
        _deferredDraft = null;
        if (deferred.Lyric.Text != deferred.Origin || !IsInChart(deferred.Lyric)) return;
        if (completed)
        {
            Commit(deferred.Lyric, deferred.Draft);
            return;
        }

        _selectedLyricRef = deferred.Lyric;
        _draftOrigin = deferred.Origin;
        SelectedLyricText = deferred.Draft;
        _chartEditor.Selection.SelectedLyric = deferred.Lyric;
    }

    private void SyncFromSelection()
    {
        var lyric = _chartEditor.Selection.SelectedLyric;
        CommitPendingDraft(lyric);
        // A DIRTY draft (it differs from the text it started from) survives a history change of
        // the same syllable while that syllable's stored text is still the draft's origin. When
        // the stored text itself changed (an undo of this syllable, another editor) the stored
        // text wins and the draft is dropped, and so it is when the syllable left the chart.
        var keepDraft = lyric != null && ReferenceEquals(lyric, _selectedLyricRef)
                        && SelectedLyricText != _draftOrigin && lyric.Text == _draftOrigin && IsInChart(lyric);
        _selectedLyricRef = lyric;
        _suppressSelectedSync = true;
        try
        {
            HasSelectedLyric = lyric != null;
            RefreshReview();
            if (!keepDraft)
            {
                SelectedLyricText = lyric?.Text ?? "";
                _draftOrigin = SelectedLyricText;
            }

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

    /// <summary>The review list card: every syllable (or only those to review), the editor's selection its own.</summary>
    public LyricSyllableList Syllables { get; }

    public string SelectedLyricReviewDetails => LyricReviewText.Format(_selectedLyricRef, _localization);

    /// <summary>Syllables of the active vocal track that still carry an auto-sync review reason.</summary>
    public int ReviewCount =>
        _chartEditor.ActiveVocalTrack?.Phrases.Sum(p => p.Lyrics.Count(l => l.ReviewReasons != LyricReviewReason.None)) ?? 0;

    public bool HasReviews => ReviewCount > 0;

    /// <summary>"n to review", or why the review buttons are off.</summary>
    public string ReviewCountText => ReviewCount is var count and > 0
        ? _localization.Format("Inspector_Lyrics_ReviewCount", count)
        : _localization["Inspector_Lyrics_ReviewNone"];

    public bool CanMarkReviewed => _selectedLyricRef is { ReviewReasons: not LyricReviewReason.None };

    private void RefreshReview()
    {
        OnPropertyChanged(nameof(SelectedLyricReviewDetails));
        OnPropertyChanged(nameof(ReviewCount));
        OnPropertyChanged(nameof(HasReviews));
        OnPropertyChanged(nameof(ReviewCountText));
        NextReviewCommand.NotifyCanExecuteChanged();
        PreviousReviewCommand.NotifyCanExecuteChanged();
        MarkReviewedCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(HasReviews))]
    private void NextReview() => SelectReview(1);

    [RelayCommand(CanExecute = nameof(HasReviews))]
    private void PreviousReview() => SelectReview(-1);

    /// <summary>The selected syllable was right all along: drop its review reasons, one undo step.</summary>
    [RelayCommand(CanExecute = nameof(CanMarkReviewed))]
    private void MarkReviewed()
    {
        if (_selectedLyricRef == null) return;
        if (_history.Do(new MarkLyricReviewedAction(_selectedLyricRef))) _chartEditor.RequestRedraw();
    }

    private void SelectReview(int direction)
    {
        var track = _chartEditor.ActiveVocalTrack;
        if (track == null) return;
        var lyrics = track.Phrases.SelectMany(p => p.Lyrics).OrderBy(l => l.Position).ToArray();
        var lyric = LyricReviewNavigation.Find(lyrics, _selectedLyricRef, direction);
        if (lyric == null) return;
        _chartEditor.Selection.SelectedLyric = lyric;
        _chartEditor.ExecuteSeek(lyric.Position.GetPositionMs(_chartEditor.Beats));
    }

    /// <summary>
    ///     Applies the text box's draft to the selected syllable — the view calls this on Enter
    ///     and when the box loses focus, never per keystroke. <see cref="SelectedLyricText" /> is
    ///     only the draft: a trailing <c>-</c> typed on the way to "x-ray" must not join lines
    ///     before the user has finished the word.
    ///     <para>
    ///         A trailing <c>-</c> in the committed text is input syntax, as in the inline editor:
    ///         it sets the word part (joining the next line when the syllable ends one) and is
    ///         never stored text. One commit is at most one history entry.
    ///     </para>
    /// </summary>
    [RelayCommand]
    public void CommitSelectedLyricText()
    {
        if (_selectedLyricRef == null) return;
        // A committed draft is clean: the commit's history change re-syncs the box to the
        // stored text ("go-" comes back as "go" with the word part set).
        _draftOrigin = SelectedLyricText;
        Commit(_selectedLyricRef, SelectedLyricText);
        if (ReferenceEquals(_selectedLyricRef, _chartEditor.Selection.SelectedLyric)) SyncFromSelection();
    }

    /// <summary>
    ///     The editor's <see cref="ChartEditorViewModel.CommitPendingEdits" /> hook (a save, an
    ///     export, its check, a close) and the inspector leaving the Lyrics tab: a dirty draft for
    ///     a syllable still in the chart is committed as Enter would, one history entry at most;
    ///     an unchanged draft makes none.
    /// </summary>
    public void CommitPendingEdit()
    {
        if (_selectedLyricRef == null || SelectedLyricText == _draftOrigin || !IsInChart(_selectedLyricRef)) return;
        if (_history.IsApplyingStep) _deferredDraft = (_selectedLyricRef, SelectedLyricText, _draftOrigin);
        else CommitSelectedLyricText();
    }

    private bool IsInChart(LyricEvent lyric) =>
        _chartEditor.VocalTracks.Any(t => t.Phrases.Any(p => p.Lyrics.Contains(lyric)));

    /// <summary>
    ///     A draft still pending when the selection moves to another syllable is committed to
    ///     the syllable it was typed for — unless that syllable left the chart (an import or a
    ///     delete), where an edit would record a node against an orphan. The reference is
    ///     dropped first: the commit's history change re-enters the selection sync.
    /// </summary>
    private void CommitPendingDraft(LyricEvent? next)
    {
        var lyric = _selectedLyricRef;
        if (lyric == null || ReferenceEquals(lyric, next) || lyric.Text == SelectedLyricText) return;
        if (!IsInChart(lyric)) return;
        _selectedLyricRef = null;
        if (_history.IsApplyingStep) _deferredDraft = (lyric, SelectedLyricText, _draftOrigin);
        else Commit(lyric, SelectedLyricText);
    }

    private void Commit(LyricEvent lyric, string draft)
    {
        if (_deferredDraft is { } deferred && ReferenceEquals(deferred.Lyric, lyric)) _deferredDraft = null;
        if (lyric.Text == draft) return;
        var (text, wordPart) = LyricInlineEditorViewModel.ParseText(draft);
        var action = wordPart
            ? LyricWordPartEdit.Create(_chartEditor.VocalTracks.Select(t => t.Phrases), lyric, text, true)
            : new RenameLyricAction(lyric, lyric.Text, draft,
                lyric.IsWordPart, lyric.IsWordPart, lyric.IsPhraseEnd, lyric.IsPhraseEnd);
        if (action.HasEffect) _history.Do(action);
        _chartEditor.RequestRedraw();
    }

    partial void OnSelectedLyricIsWordPartChanged(bool value)
    {
        if (_suppressSelectedSync || _selectedLyricRef == null) return;
        if (_selectedLyricRef.IsWordPart == value) return;
        var action = LyricWordPartEdit.Create(_chartEditor.VocalTracks.Select(t => t.Phrases), _selectedLyricRef, value);
        _history.Do(action);
        _chartEditor.RequestRedraw();
        SyncFromSelection();
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

        // Selection can belong to another vocal track. Use its actual owner and
        // keep the live collections (LyricPhrases aliases the active track's list).
        foreach (var track in _chartEditor.VocalTracks)
        {
            var phrase = track.Phrases.FirstOrDefault(p => p.Lyrics.Contains(_selectedLyricRef));
            if (phrase == null) continue;
            var action = new SetLyricPhraseEndAction(track.Phrases, phrase, _selectedLyricRef, value);
            if (!value && !action.HasEffect)
                SetStatus(() => _localization["Status_LyricPhraseEnd_NoNextLine"]);
            if (_history.Do(action)) _chartEditor.RequestRedraw();
            // A refused history action or the final line must reset the checkbox too.
            SyncFromSelection();
            return;
        }

        SyncFromSelection();
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
    private void CloseGaps()
    {
        if (_chartEditor.ActiveVocalTrack is not { } track || _chartEditor.Beats.Count == 0) return;
        var edits = LyricPhraseSpacing.InPhraseEnds(track.Phrases, _chartEditor.Beats, LoadSpacingConfig())
            .Where(change => !change.Lyric.IsSustainShortened && change.Lyric.EndPosition != change.End)
            .Select(change => new ResizeLyricAction(change.Lyric, change.Lyric.EndPosition, change.End));
        if (_history.Do(new CompositeAction(edits) { LabelKey = "History_CloseLyricGaps" }))
            _chartEditor.RequestRedraw();
    }

    private AppConfig LoadSpacingConfig() => _configService?.LoadAppConfig() ?? new AppConfig();

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
            Remembered() ?? startDir);
        if (string.IsNullOrWhiteSpace(filePath))
            return;
        _configService?.RememberDirectory(filePath, (c, d) => c.LastLyricsDir = d);

        try
        {
            var phrases = await _files.ReadLrcAsync(filePath, _chartEditor.Beats);
            if (phrases.Count == 0)
            {
                _projectState.SetMessage(_localization["Inspector_Lyrics_StatusEmpty"]);
                return;
            }

            LyricPhraseSpacing.CloseGaps(phrases, _chartEditor.Beats, LoadSpacingConfig());
            var track = PrepareActiveVocalTrack(out var isNewTrack);
            if (ReplacePhrases(track, phrases, isNewTrack: isNewTrack))
            {
                Reset();
                _chartEditor.RequestRedraw();
                Func<string> status = () => _localization.Format("Status_LrcImport_Applied", phrases.Count,
                    phrases.Sum(p => p.Lyrics.Count));
                ShowStatus(status);
                _projectState.SetMessage(StatusText);
                _logger.Info(AppLogCategory.Import,
                    $"Imported LRC lyrics from '{filePath}'. phrases={phrases.Count}, lyrics={phrases.Sum(p => p.Lyrics.Count)}.");
            }
        }
        catch (ImportFileTooLargeException tooLarge)
        {
            ReportTooLarge(tooLarge, filePath);
        }
        catch (Exception ex)
        {
            _logger.Error(AppLogCategory.Import, $"LRC import failed for '{filePath}'.", ex);
            _projectState.SetMessage(_localization.Format("Status_LrcImport_Failed", ex.Message), StatusSeverity.Error);
        }
    }

    private void ReportTooLarge(ImportFileTooLargeException tooLarge, string filePath)
    {
        _logger.Warning(AppLogCategory.Import, $"Lyrics import refused. File='{filePath}'.", tooLarge);
        _projectState.SetMessage(
            _localization.Format("Status_ImportFileTooLarge", Path.GetFileName(filePath),
                ImportSizeLimits.ToMegabytes(tooLarge.Length), ImportSizeLimits.ToMegabytes(tooLarge.Cap)),
            StatusSeverity.Error);
    }

    [RelayCommand]
    private async Task ExportLrc()
    {
        if (!TryGetExportTrack(out var track)) return;
        var project = _projectState.ActiveProject!;
        try
        {
            var text = _files.BuildLrc(track, _chartEditor.Beats,
                project.Chart.SongName, project.Chart.Artist);
            var path = await SaveLyricFileAsync(
                _localization["Picker_SaveLrcFile"],
                _files.SuggestFileName(project.Chart.SongName) + ".lrc",
                "LRC Lyrics", "*.lrc");
            if (string.IsNullOrWhiteSpace(path)) return;

            await _files.WriteAsync(path, text);
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
            Remembered() ?? startDir);
        if (string.IsNullOrWhiteSpace(filePath))
            return;
        _configService?.RememberDirectory(filePath, (c, d) => c.LastLyricsDir = d);

        try
        {
            var imported = await _files.ReadUltraStarAsync(filePath, _chartEditor.Beats);
            var voices = imported.Voices;
            var phrases = voices[0].Phrases.ToList();
            if (phrases.Count == 0)
            {
                _projectState.SetMessage(_localization["Status_UltraStarImport_Empty"]);
                return;
            }

            var track = PrepareActiveVocalTrack(out var isNewTrack);
            var addedTracks = voices.Skip(1).ToList();
            LyricPhraseSpacing.CloseGaps(voices.SelectMany(voice => voice.Phrases), _chartEditor.Beats, LoadSpacingConfig());
            if (ReplacePhrases(track, phrases, addedTracks, isNewTrack))
            {
                Reset();
                _chartEditor.RequestRedraw();
                var phraseCount = voices.Sum(v => v.Phrases.Count);
                var lyricCount = voices.Sum(v => v.Phrases.Sum(p => p.Lyrics.Count));
                Func<string> status = () => _localization.Format(
                    voices.Count > 1 ? "Status_UltraStarImport_AppliedDuet" : "Status_UltraStarImport_Applied",
                    voices.Count > 1 ? [voices.Count, phraseCount, lyricCount] : [phraseCount, lyricCount]);
                ShowStatus(status);
                _projectState.SetMessage(StatusText);
                _logger.Info(AppLogCategory.Import,
                    $"Imported UltraStar lyrics from '{filePath}'. voices={voices.Count}, relative={imported.IsRelative}, " +
                    $"phrases={phraseCount}, lyrics={lyricCount}.");
            }
        }
        catch (ImportFileTooLargeException tooLarge)
        {
            ReportTooLarge(tooLarge, filePath);
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
            var text = _files.BuildUltraStar(track, _chartEditor.Beats, project.Bpm,
                project.Chart.SongName, project.Chart.Artist, project.AudioFileName);
            var path = await SaveLyricFileAsync(
                _localization["Picker_SaveUltraStarFile"],
                _files.SuggestFileName(project.Chart.SongName) + ".txt",
                "UltraStar Song", "*.txt");
            if (string.IsNullOrWhiteSpace(path)) return;

            await _files.WriteAsync(path, text);
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
        CommitPendingEdit();
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

    private async Task<string?> SaveLyricFileAsync(string title, string suggestedName, string filterName, string pattern)
    {
        var startDir = Path.GetDirectoryName(Path.GetFullPath(
            string.IsNullOrWhiteSpace(_projectState.CurrentProjectPath) ? "." : _projectState.CurrentProjectPath));
        var path = await _storageService.SaveFilePickerAsync(title, suggestedName,
            new[]
            {
                new FilePickerFilter(filterName, new[] { pattern }),
                FilePickerFilters.AllFiles
            },
            Remembered() ?? startDir);
        _configService?.RememberDirectory(path, (c, d) => c.LastLyricsDir = d);
        return path;
    }

    private string? Remembered() => _configService?.StartDirectory(c => c.LastLyricsDir);

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
        var syncedProject = _projectState.ActiveProject;
        if (syncedProject == null)
        {
            _projectState.SetMessage(_localization["Status_NoChartLoaded"]);
            return;
        }

        if (string.IsNullOrWhiteSpace(RawLyrics))
        {
            SetStatus(() => _localization["Status_LyricAutoSync_Empty"]);
            return;
        }

        if (_chartEditor.Beats.Count == 0)
        {
            SetStatus(() => _localization["Status_LyricAutoSync_NoBeats"]);
            return;
        }

        var activeTrack = _chartEditor.ActiveVocalTrack;
        var track = PrepareActiveVocalTrack(out var isNewTrack);
        IsAutoSyncing = true;
        AutoSyncCommand.NotifyCanExecuteChanged();
        try
        {
            var progress = new Progress<string>(step => ShowStatus(() =>
                _localization.Format("Status_LyricAutoSync_Running", step)));

            var result = await _autoSync.RunAsync(RawLyrics, _chartEditor.Beats,
                megabytes => SetStatus(() => _localization.Format("Status_LyricAutoSync_Downloading", megabytes)),
                progress);

            // The runner can finish before this UI continuation resumes.
            if (!ReferenceEquals(_projectState.ActiveProject, syncedProject))
                result = new LyricAutoSyncRunResult(LyricAutoSyncRunStatus.ProjectChanged);

            switch (result.Status)
            {
                case LyricAutoSyncRunStatus.NoModel:
                    SetStatus(() => _localization.Format("Status_LyricAutoSync_NoModel", result.ModelDirectory ?? ""));
                    return;
                case LyricAutoSyncRunStatus.NoAudio:
                    SetStatus(() => _localization["Status_LyricAutoSync_NoAudio"]);
                    return;
                case LyricAutoSyncRunStatus.ProjectChanged:
                    _logger.Info(AppLogCategory.Import,
                        "Lyric auto-sync discarded: the active project changed while it ran.");
                    SetStatus(() => _localization["Status_LyricAutoSync_ProjectChanged"]);
                    return;
                case LyricAutoSyncRunStatus.Failed:
                    SetStatus(() => _localization.Format("Status_LyricAutoSync_Failed", result.Error ?? ""));
                    return;
            }

            var outcome = result.Outcome!;
            if (!ReferenceEquals(_projectState.ActiveProject, syncedProject) ||
                !ReferenceEquals(_chartEditor.ActiveVocalTrack, activeTrack) ||
                (!isNewTrack && !_chartEditor.VocalTracks.Contains(track)))
            {
                _logger.Info(AppLogCategory.Import,
                    "Lyric auto-sync discarded: the destination vocal track changed while it ran.");
                SetStatus(() => _localization["Status_LyricAutoSync_TrackChanged"]);
                return;
            }

            if (!ReplacePhrases(track, outcome.Phrases.ToList(), isNewTrack: isNewTrack)) return;

            Reset();
            _chartEditor.RequestRedraw();
            SetStatus(() => _localization.Format("Status_LyricAutoSync_Applied",
                outcome.SyllableCount, outcome.Phrases.Count, outcome.AudioSource,
                outcome.LowConfidenceCount + outcome.InterpolatedCount));
            _logger.Info(AppLogCategory.Import,
                $"Lyric auto-sync applied. syllables={outcome.SyllableCount}, " +
                $"phrases={outcome.Phrases.Count}, source={outcome.AudioSource}, " +
                $"lowConfidence={outcome.LowConfidenceCount}, interpolated={outcome.InterpolatedCount}.");
        }
        catch (Exception) when (!ReferenceEquals(_projectState.ActiveProject, syncedProject))
        {
            SetStatus(() => _localization["Status_LyricAutoSync_ProjectChanged"]);
        }
        catch (Exception ex)
        {
            _logger.Error(AppLogCategory.Import, "Lyric auto-sync failed.", ex);
            SetStatus(() => _localization.Format("Status_LyricAutoSync_Failed", ex.Message));
        }
        finally
        {
            IsAutoSyncing = false;
            AutoSyncCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanAutoSync() => !IsAutoSyncing;

    private void SetStatus(Func<string> status)
    {
        ShowStatus(status);
        _projectState.SetMessage(StatusText);
    }

    /// <summary>
    ///     Shows a status line and keeps how to word it, so a language switch re-words the line in place instead of
    ///     leaving it in the language it was written in.
    /// </summary>
    private void ShowStatus(Func<string> status)
    {
        _status = status;
        StatusText = status();
    }

    [RelayCommand]
    private void Apply()
    {
        var count = _tap.Load(RawLyrics);
        if (count == 0)
        {
            IsArmed = false;
            RemainingCount = 0;
            NextPreview = "";
            ShowStatus(() => _localization["Inspector_Lyrics_StatusEmpty"]);
            return;
        }

        IsArmed = true;
        RemainingCount = count;
        NextPreview = _tap.Preview;
        ShowStatus(() => ArmedStatus());
    }

    [RelayCommand]
    private void Reset()
    {
        _tap.Clear();
        IsArmed = false;
        RemainingCount = 0;
        NextPreview = "";
        ShowStatus(() => _localization["Inspector_Lyrics_StatusIdle"]);
    }

    private string ArmedStatus()
        => _localization.Plural("Inspector_Lyrics_StatusArmedOne", "Inspector_Lyrics_StatusArmed", _tap.Count);

    /// <summary>The raw-text card's "Tap at playhead" button: the same tap as <c>Ctrl+Space</c>.</summary>
    [RelayCommand]
    private void TapAtPlayhead() => TapNext();

    /// <summary>
    ///     Pops the next syllable from the queue and commits it to the active
    ///     vocal track at the current playhead. Bound to <c>Ctrl+Space</c>.
    /// </summary>
    public void TapNext()
    {
        if (!IsArmed || _tap.Count == 0)
        {
            _projectState.SetMessage(_localization["Status_LyricTap_NotArmed"]);
            return;
        }

        var tapMs = _projectState.CurrentTimeMs;
        var outcome = _tap.TapNext(_chartEditor.ActiveVocalTrack, _chartEditor.Beats, tapMs, _history,
            out var text);
        if (outcome == LyricTapOutcome.NoVocalTrack)
        {
            _projectState.SetMessage(_localization["Status_LyricTap_NoVocalTrack"]);
            return;
        }

        if (outcome == LyricTapOutcome.NoBeats)
        {
            _projectState.SetMessage(_localization["Status_LyricTap_NoBeats"]);
            return;
        }

        _logger.Info(AppLogCategory.Editing,
            $"Lyric tap '{text}' at {tapMs:F0}ms, remaining={_tap.Count}.");
        RemainingCount = _tap.Count;
        NextPreview = _tap.Preview;
        if (_tap.Count == 0)
        {
            IsArmed = false;
            ShowStatus(() => _localization["Inspector_Lyrics_StatusDone"]);
        }
    }

    // The session raises this after an undo or redo of a tap moved its queue.
    private void OnTapChanged(LyricTapChange change)
    {
        RemainingCount = _tap.Count;
        NextPreview = _tap.Preview;
        if (change == LyricTapChange.Redone && _tap.Count == 0)
        {
            IsArmed = false;
            ShowStatus(() => _localization["Inspector_Lyrics_StatusDone"]);
            return;
        }

        IsArmed = true;
        ShowStatus(() => ArmedStatus());
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
        IReadOnlyList<VocalTrack>? addedTracks = null, bool isNewTrack = false)
    {
        // An UltraStar duet's further voices are added in the SAME undo node as the
        // first voice's replacement, so one undo takes the whole import back.
        IChartAction action = new SetVocalPhrasesAction(track.Phrases, phrases);
        if (addedTracks is { Count: > 0 })
        {
            action = new CompositeAction(
                [action, .. addedTracks.Select(t => new AddVocalTrackAction(_chartEditor.VocalTracks, t))]);
        }

        // A track created for this import joins the SAME node, so one undo removes it
        // again and a refused or failed Do leaves no empty track behind.
        if (isNewTrack)
            action = new CompositeAction([new AddActiveVocalTrackAction(_chartEditor, _chartEditor.VocalTracks, track), action]);

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

    /// <summary>The active track, or a NEW unattached one; <see cref="ReplacePhrases"/> adds it inside the undo node.</summary>
    private VocalTrack PrepareActiveVocalTrack(out bool isNew)
    {
        isNew = _chartEditor.ActiveVocalTrack == null;
        return _chartEditor.ActiveVocalTrack ?? new VocalTrack
        {
            Name = $"Vocals {_chartEditor.VocalTracks.Count + 1}"
        };
    }
}
