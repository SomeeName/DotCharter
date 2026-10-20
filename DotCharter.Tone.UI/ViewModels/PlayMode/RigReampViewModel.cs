using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Tone.Audio.Reamp;
using DotCharter.Tone.Infrastructure.Shell;

namespace DotCharter.Tone.UI.ViewModels.PlayMode;

/// <summary>
///     The rig's DI recorder and RE-AMP transport (ABI v37): record the dry
///     guitar to a take, then play a take INTO the rig in place of the guitar
///     and turn knobs, swap amps and switch tones without playing.
///     <para>
///         One per HOST process (a singleton next to the rig's
///         <see cref="PedalboardViewModel" />), so a take keeps playing whichever
///         rig surface is open. Engine work goes through <see cref="IReampEngine" />
///         — absent on the null transport, in which case every control is
///         DISABLED and <see cref="UnavailableReason" /> says why. Decoding and
///         resampling a take run off the UI thread.
///     </para>
///     <para>
///         The poll (recorder drain, transport readout) runs on a lazily created
///         UI timer ONLY while recording or re-amping; <see cref="Tick" /> is the
///         whole of it, so the tests drive it by hand.
///     </para>
/// </summary>
public sealed partial class RigReampViewModel : ObservableObject, IDisposable
{
    private readonly IReampEngine? _engine;
    private readonly DiTakeLibrary _library;
    private readonly IShellLauncher? _shell;
    private readonly IFileStorageService? _files;
    private readonly IAppLogger? _logger;
    private readonly Func<string, (float[] Mono, int SampleRate)> _loader;
    private readonly Func<string, TimeSpan?> _probe;
    private DiTakeRecorder? _recorder;
    private DispatcherTimer? _timer;
    private bool _pushingPosition;

    /// <param name="engine">The engine seam, or null (no engine / null transport).</param>
    /// <param name="library">The takes folder.</param>
    /// <param name="localization">The words of its status lines and pickers.</param>
    /// <param name="shell">Opens the takes folder; null hides nothing, the command no-ops.</param>
    /// <param name="files">The import picker; null disables Import.</param>
    /// <param name="logger">Optional.</param>
    /// <param name="loader">Test seam: decodes a take to mono (default <see cref="DiTakeLibrary.LoadMono" />).</param>
    /// <param name="probe">Test seam: a take's duration (default: the decoder's header probe).</param>
    public RigReampViewModel(IReampEngine? engine, DiTakeLibrary library, ILocalizationService localization,
        IShellLauncher? shell = null,
        IFileStorageService? files = null, IAppLogger? logger = null,
        Func<string, (float[] Mono, int SampleRate)>? loader = null,
        Func<string, TimeSpan?>? probe = null)
    {
        _engine = engine;
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _loc = localization;
        _shell = shell;
        _files = files;
        _logger = logger;
        _loader = loader ?? DiTakeLibrary.LoadMono;
        _probe = probe ?? ProbeDuration;
        RefreshTakes();
    }

    private readonly ILocalizationService _loc;

    /// <summary>Test seam: the caller drives <see cref="Tick" /> itself and no UI
    /// timer is created (a default-gate test must not touch the dispatcher).</summary>
    internal bool ManualTick { get; set; }

    /// <summary>The seat whose input is recorded and replaced (0 = the solo rig).</summary>
    public int Player { get; set; }

    // ---- availability -----------------------------------------------------

    /// <summary>True when an engine is up to record from and re-amp into.</summary>
    public bool IsAvailable => _engine?.IsReampAvailable == true;

    /// <summary>Why the recorder and transport are disabled.</summary>
    public string UnavailableReason => _loc["Rig_Reamp_NoEngine"];

    /// <summary>Re-reads engine availability (the host calls this when a device comes or goes).</summary>
    public void RefreshAvailability()
    {
        OnPropertyChanged(nameof(IsAvailable));
        ToggleRecordCommand.NotifyCanExecuteChanged();
        ReampTakeCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Recording or re-amping: the header lamp lights.</summary>
    public bool IsActive => IsRecording || IsReamping;

    [ObservableProperty] private string? _status;

    // ---- recording --------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActive))]
    [NotifyCanExecuteChangedFor(nameof(ReampTakeCommand))]
    private bool _isRecording;

    /// <summary>Elapsed recording time, m:ss.</summary>
    [ObservableProperty] private string _recordTimeText = FormatTime(0);

    /// <summary>The recording's input level, dBFS (for the Fire level meter).</summary>
    [ObservableProperty] private double _recordLevelDb = double.NegativeInfinity;

    private bool CanToggleRecord() => IsRecording || (IsAvailable && !IsReamping && !IsLoading);

    [RelayCommand(CanExecute = nameof(CanToggleRecord))]
    private void ToggleRecord()
    {
        if (IsRecording) { StopRecording(); return; }
        if (_engine == null) return;
        var recorder = new DiTakeRecorder(_engine, Player);
        string path;
        try { path = _library.NewRecordingPath(DateTime.Now); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = _loc["Rig_Reamp_RecordFailed"];
            return;
        }
        bool started;
        try { started = recorder.Start(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.Warning(AppLogCategory.Audio, $"DI recorder: could not start '{path}': {ex.Message}");
            started = false;
        }
        if (!started)
        {
            recorder.Dispose();
            Status = _loc["Rig_Reamp_RecordFailed"];
            return;
        }
        _recorder = recorder;
        Status = null;
        RecordTimeText = FormatTime(0);
        RecordLevelDb = double.NegativeInfinity;
        IsRecording = true;
        SyncTimer();
    }

    private void StopRecording()
    {
        var recorder = _recorder;
        _recorder = null;
        IsRecording = false;
        RecordLevelDb = double.NegativeInfinity;
        SyncTimer();
        if (recorder == null) return;
        string? saved = null;
        try { saved = recorder.Stop(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.Warning(AppLogCategory.Audio, $"DI recorder: saving the take failed: {ex.Message}");
            Status = _loc["Rig_Reamp_RecordFailed"];
        }
        finally { recorder.Dispose(); }
        RefreshTakes();
        if (saved != null)
            SelectedTake = Takes.FirstOrDefault(t => string.Equals(t.Take.FilePath, saved, StringComparison.OrdinalIgnoreCase));
    }

    // ---- takes ------------------------------------------------------------

    /// <summary>Every take in the folder, newest first.</summary>
    public ObservableCollection<RigDiTakeRow> Takes { get; } = [];

    [ObservableProperty] private RigDiTakeRow? _selectedTake;

    /// <summary>False shows the empty state.</summary>
    public bool HasTakes => Takes.Count > 0;

    /// <summary>Re-lists the takes folder.</summary>
    public void RefreshTakes()
    {
        var selected = SelectedTake?.Take.FilePath;
        Takes.Clear();
        foreach (var take in _library.List())
            Takes.Add(new RigDiTakeRow(take, FormatDuration(_probe(take.FilePath))));
        MarkLiveRow();
        SelectedTake = selected == null
            ? null
            : Takes.FirstOrDefault(t => string.Equals(t.Take.FilePath, selected, StringComparison.OrdinalIgnoreCase));
        OnPropertyChanged(nameof(HasTakes));
    }

    [RelayCommand]
    private void DeleteTake(RigDiTakeRow? row)
    {
        if (row == null) return;
        if (IsReamping && string.Equals(ReampTakePath, row.Take.FilePath, StringComparison.OrdinalIgnoreCase))
            Stop();
        if (!_library.Delete(row.Take)) { Status = _loc["Rig_Reamp_FileFailed"]; return; }
        RefreshTakes();
    }

    [RelayCommand]
    private void BeginRename(RigDiTakeRow? row)
    {
        if (row == null) return;
        foreach (var other in Takes) other.IsRenaming = false;
        row.EditName = row.Name;
        row.IsRenaming = true;
    }

    [RelayCommand]
    private void CommitRename(RigDiTakeRow? row)
    {
        if (row == null || !row.IsRenaming) return;
        row.IsRenaming = false;
        var wasReamping = IsReamping && string.Equals(ReampTakePath, row.Take.FilePath, StringComparison.OrdinalIgnoreCase);
        var renamed = _library.Rename(row.Take, row.EditName ?? string.Empty);
        if (renamed == null) { Status = _loc["Rig_Reamp_FileFailed"]; return; }
        if (wasReamping) { ReampTakePath = renamed.FilePath; ReampTakeName = renamed.Name; }
        SelectedTake = null;
        RefreshTakes();
        SelectedTake = Takes.FirstOrDefault(t => string.Equals(t.Take.FilePath, renamed.FilePath, StringComparison.OrdinalIgnoreCase));
    }

    [RelayCommand]
    private void CancelRename(RigDiTakeRow? row) => row?.IsRenaming = false;

    [RelayCommand]
    private void OpenFolder()
    {
        // create: true — the folder may not exist before the first take.
        if (_shell != null && !_shell.OpenFolder(_library.RootDirectory, create: true))
            Status = _loc["Rig_Reamp_FileFailed"];
    }

    /// <summary>False without a file picker (a test host).</summary>
    public bool CanImport => _files != null;

    [RelayCommand]
    private async Task Import()
    {
        if (_files == null) return;
        var patterns = DiTakeLibrary.SupportedExtensions.Select(e => "*" + e).ToArray();
        var path = await _files.OpenFilePickerAsync(_loc["Rig_Reamp_ImportTitle"],
            [new FilePickerFilter(_loc["Rig_Reamp_AudioFiles"], patterns)]);
        if (string.IsNullOrWhiteSpace(path)) return;
        var take = _library.Import(path);
        if (take == null) { Status = _loc["Rig_Reamp_ImportFailed"]; return; }
        Status = null;
        RefreshTakes();
        SelectedTake = Takes.FirstOrDefault(t => string.Equals(t.Take.FilePath, take.FilePath, StringComparison.OrdinalIgnoreCase));
    }

    // ---- re-amp transport --------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActive))]
    [NotifyCanExecuteChangedFor(nameof(ToggleRecordCommand))]
    private bool _isReamping;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleRecordCommand))]
    [NotifyCanExecuteChangedFor(nameof(ReampTakeCommand))]
    private bool _isLoading;

    /// <summary>The take being re-amped, by name (the transport's label).</summary>
    [ObservableProperty] private string? _reampTakeName;

    private string? ReampTakePath { get; set; }

    [ObservableProperty] private bool _isPlaying;

    /// <summary>The play head, seconds. Two-way: a slider drag SEEKS.</summary>
    [ObservableProperty] private double _position;

    /// <summary>The take's length, seconds (the slider's maximum).</summary>
    [ObservableProperty] private double _duration = 1;

    /// <summary>"0:12 / 1:30" — fixed-width in the mono face.</summary>
    [ObservableProperty] private string _positionText = FormatTime(0) + " / " + FormatTime(0);

    private bool _loop = true;

    /// <summary>Loop the take (default on) or play it once.</summary>
    public bool Loop
    {
        get => _loop;
        set
        {
            if (!SetProperty(ref _loop, value)) return;
            if (IsReamping) _engine?.SetTakeLoop(Player, value);
        }
    }

    partial void OnPositionChanged(double value)
    {
        UpdatePositionText();
        if (_pushingPosition || !IsReamping) return;
        _engine?.SeekTake(Player, value);
    }

    private bool CanReampTake(RigDiTakeRow? row) => row != null && IsAvailable && !IsRecording && !IsLoading;

    /// <summary>Loads a take into the seat's input and starts it: from here the
    /// rig hears the take instead of the guitar.</summary>
    [RelayCommand(CanExecute = nameof(CanReampTake))]
    private async Task ReampTake(RigDiTakeRow? row)
    {
        if (row == null || _engine == null) return;
        IsLoading = true;
        Status = null;
        try
        {
            var path = row.Take.FilePath;
            var loop = Loop;
            var player = Player;
            var loaded = await Task.Run(() =>
            {
                var (mono, rate) = _loader(path);
                return _engine.LoadTake(player, mono, rate, loop);
            });
            if (!loaded)
            {
                Status = _loc["Rig_Reamp_LoadFailed"];
                return;
            }
            ReampTakePath = path;
            ReampTakeName = row.Name;
            IsReamping = true;
            MarkLiveRow();
            _engine.SetTakePlaying(player, true);
            IsPlaying = true;
            Tick();
            SyncTimer();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
                                       or ArgumentException or InvalidOperationException)
        {
            _logger?.Warning(AppLogCategory.Audio, $"Re-amp: could not load '{row.Take.FilePath}': {ex.Message}");
            Status = _loc["Rig_Reamp_LoadFailed"];
        }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    private void PlayPause()
    {
        if (!IsReamping || _engine == null) return;
        var next = !IsPlaying;
        if (_engine.SetTakePlaying(Player, next)) IsPlaying = next;
    }

    /// <summary>Unloads the take: the seat's live guitar is back.</summary>
    [RelayCommand]
    private void Stop()
    {
        if (!IsReamping) return;
        _engine?.ClearTake(Player);
        IsReamping = false;
        IsPlaying = false;
        ReampTakeName = null;
        ReampTakePath = null;
        MarkLiveRow();
        SetPositionFromEngine(0, 1);
        SyncTimer();
    }

    // ---- the poll -----------------------------------------------------------

    /// <summary>One poll: drains the recorder and reads the transport.</summary>
    internal void Tick()
    {
        if (_recorder != null)
        {
            try { _recorder.Poll(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger?.Warning(AppLogCategory.Audio, $"DI recorder: writing failed: {ex.Message}");
                Status = _loc["Rig_Reamp_RecordFailed"];
                StopRecording();
                return;
            }
            RecordTimeText = FormatTime(_recorder.ElapsedSeconds);
            var peak = _recorder.LastPeak;
            var db = peak > 0 ? 20.0 * Math.Log10(peak) : double.NegativeInfinity;
            // Fall back gently, like a hardware meter; rise at once.
            RecordLevelDb = db >= RecordLevelDb || double.IsNegativeInfinity(RecordLevelDb)
                ? db
                : Math.Max(db, RecordLevelDb - 3.0);
        }

        if (IsReamping && _engine != null)
        {
            if (_engine.TryGetTakeStatus(Player, out var s))
            {
                IsPlaying = s.IsPlaying;
                SetPositionFromEngine(s.PositionSeconds, s.LengthSeconds);
            }
            else if (!_engine.HasTake(Player))
            {
                // The engine dropped the take (it could not be replayed after a
                // device change): say so and stand down, never a dead transport.
                Stop();
                Status = _loc["Rig_Reamp_Lost"];
            }
        }
    }

    private void MarkLiveRow()
    {
        foreach (var row in Takes)
            row.IsLive = IsReamping && string.Equals(row.Take.FilePath, ReampTakePath, StringComparison.OrdinalIgnoreCase);
    }

    private void SetPositionFromEngine(double position, double length)
    {
        _pushingPosition = true;
        try
        {
            Duration = Math.Max(length, 0.001);
            Position = Math.Clamp(position, 0, Duration);
            UpdatePositionText();
        }
        finally { _pushingPosition = false; }
    }

    private void UpdatePositionText()
        => PositionText = FormatTime(Position) + " / " + FormatTime(IsReamping ? Duration : 0);

    private void SyncTimer()
    {
        var want = IsRecording || IsReamping;
        if (!want) { _timer?.Stop(); return; }
        if (ManualTick) return;
        _timer ??= CreateTimer();
        _timer.Start();
    }

    private DispatcherTimer CreateTimer()
    {
        // 20 Hz: a smooth play head, and a recorder drain far inside the ~2.7 s ring.
        var t = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(50) };
        t.Tick += (_, _) => Tick();
        return t;
    }

    internal static string FormatTime(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) seconds = 0;
        var total = (int)Math.Floor(seconds);
        return $"{total / 60}:{total % 60:00}";
    }

    private static string FormatDuration(TimeSpan? d) => d is { } t ? FormatTime(t.TotalSeconds) : "–";

    private static TimeSpan? ProbeDuration(string path)
    {
        try
        {
            var (rate, _, frames) = JuceSharp.AudioIo.JuceAudioDecoder.Probe(path);
            return rate > 0 ? TimeSpan.FromSeconds((double)frames / rate) : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>Stops the poll. A recording still running is SAVED, not thrown
    /// away — closing the app mid-take keeps what was played.</summary>
    public void Dispose()
    {
        _timer?.Stop();
        var recorder = _recorder;
        _recorder = null;
        if (recorder == null) return;
        try { recorder.Stop(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.Warning(AppLogCategory.Audio, $"DI recorder: saving the take at shutdown failed: {ex.Message}");
        }
        finally { recorder.Dispose(); }
    }
}

/// <summary>One take in the re-amp panel's list.</summary>
public sealed partial class RigDiTakeRow : ObservableObject
{
    public RigDiTakeRow(DiTake take, string durationText)
    {
        Take = take;
        DurationText = durationText;
    }

    public DiTake Take { get; }

    public string Name => Take.Name;

    /// <summary>"1:30", or a dash when the file could not be probed.</summary>
    public string DurationText { get; }

    [ObservableProperty] private bool _isRenaming;

    /// <summary>This take is the one playing into the rig (the list lights it).</summary>
    [ObservableProperty] private bool _isLive;

    [ObservableProperty] private string? _editName;
}
