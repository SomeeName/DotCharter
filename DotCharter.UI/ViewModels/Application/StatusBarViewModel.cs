using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Services.Gameplay.Profiles;
using DotCharter.Services.Gameplay.Stats;
using DotCharter.Tone.Diagnostics;
using DotCharter.UI.Controls.Layers.Helpers;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Editor.Inspector;
namespace DotCharter.UI.ViewModels.Application;

/// <summary>
///     The status rail's EDITING-STATE readouts (P1-11), pure so the strings
///     are testable without a dispatcher: SNAP (the grid label from
///     <see cref="GridSnapOption" />), SEL n (total selected objects), ZOOM ×n
///     (timeline zoom over the default pixels-per-ms) and, only while one is
///     set, the string-edit MASK. Each takes its localized FORMAT, so the
///     strings stay testable without the culture singleton; the rail passes
///     <c>StatusBar_Snap</c> / <c>_Selection</c> / <c>_Zoom</c>.
/// </summary>
internal static class EditStateReadouts
{
    /// <summary>`UserSettingsState.PixelsPerMs`'s default — the ×1.0 baseline.</summary>
    internal const double BaselinePixelsPerMs = 0.2;

    public static string Snap(int gridDivisor, string format) =>
        string.Format(CultureInfo.InvariantCulture, format, GridSnapOption.LabelFor(gridDivisor));

    public static string Selection(int count, string format) =>
        string.Format(CultureInfo.InvariantCulture, format, count);

    public static string Zoom(double pixelsPerMs, string format) =>
        string.Format(CultureInfo.InvariantCulture, format,
            (pixelsPerMs / BaselinePixelsPerMs).ToString("0.##", CultureInfo.InvariantCulture));

    /// <summary>Empty string while no mask is set — an empty mask edits every
    /// string, and printing "MASK (all)" would make the resting state look
    /// modal. Otherwise the strings are named exactly as the lane gutter and
    /// the inspector's Sound tab name them, "MASK A (5), B (2)"
    /// (<see cref="GuitarStringLabel" />) — never the
    /// low-first Ctrl+digits ("MASK 2·5") next to an inspector saying "A (5)".</summary>
    /// <param name="mask">The project's string-edit mask.</param>
    /// <param name="tuning">The active arrangement's tuning names.</param>
    /// <param name="format">The resx <c>StatusBar_Mask</c>, "MASK {0}".</param>
    /// <param name="stringLabelFormat">The resx <c>Inspector_StringLabelFmt</c>, "{0} ({1})".</param>
    public static string Mask(StringEditMask mask, IReadOnlyList<string>? tuning, string format,
        string stringLabelFormat)
    {
        if (mask.IsEmpty) return string.Empty;
        var indices = Enumerable.Range(0, StringEditMask.MaxStrings).Where(mask.IsExplicit);
        var strings = GuitarStringLabel.List(indices, tuning?.Count ?? 6, tuning, stringLabelFormat);
        return string.Format(CultureInfo.InvariantCulture, format, strings);
    }

    public static int TotalSelected(ISelectionService selection) =>
        selection.SelectedNotes.Count + selection.SelectedChords.Count
        + selection.SelectedEventPoints.Count + selection.SelectedTones.Count
        + selection.SelectedHandShapes.Count + selection.SelectedAnchors.Count;
}

/// <summary>One row in the status bar's profile-switcher flyout — and, since the
/// launch screen was rebuilt, in its PLAYER PROFILES panel too.</summary>
public sealed record ProfileOption(PlayerProfile Profile, bool IsActive, IBrush Accent, Bitmap? Avatar)
{
    public string Name => Profile.DisplayName;
    public bool HasAvatar => Avatar != null;

    /// <summary>
    ///     The one-line detail under a profile's name, from the only per-profile
    ///     facts that actually exist: when it was last used, and whether it plays
    ///     left-handed.
    ///     <para>
    ///         The commissioned mockup prints "LEVEL 14 · 12 DAY STREAK" here.
    ///         <b>There is no level, XP or rank concept anywhere in this app</b>
    ///         (<see cref="PlayerProfile" /> is id / name / accent / avatar /
    ///         lefty / created / last-used), so no number was invented. The
    ///         streak is real but is stored PER PROFILE in that profile's own
    ///         practice store, which this view model does not open — it is
    ///         printed for the ACTIVE profile on the launch screen's own chip
    ///         instead of guessed at for every row.
    ///     </para>
    /// </summary>
    public string Detail { get; init; } = "";

    /// <inheritdoc cref="Detail" />
    public bool HasDetail => Detail.Length > 0;
}

/// <summary>
/// Backing view model for the IntelliJ-style bottom status bar in
/// <c>MainWindow.axaml</c>. Aggregates the existing
/// <see cref="ProjectState.MainDisplayMessage"/> hint, recent warnings +
/// errors surfaced by <see cref="IAppLogger"/>, and active long-running
/// operations (audio engine restart, stem separation, VST loading, etc.)
/// from <see cref="IStatusBarService"/>.
/// </summary>
public sealed partial class StatusBarViewModel : ObservableObject, IDisposable
{
    private readonly IStatusBarService _statusBar;
    private readonly ILocalizationService _localization;

    /// <summary>The rail's localization; the shell that hosts it speaks through the same.</summary>
    internal ILocalizationService Localization => _localization;
    private readonly ProjectState _projectState;
    private readonly IAudioPlaybackService _audio;
    private readonly IUiDispatcher _ui;
    private readonly IPlayerProfileService _playerProfiles;
    private readonly IArtworkSource _artwork;
    private readonly IFileStorageService _storage;
    private readonly TransportViewModel? _transport;
    private readonly ISelectionService? _selection;

    public StatusBarViewModel(
        IStatusBarService statusBar,
        ProjectState projectState,
        IAudioPlaybackService audio,
        IUiDispatcher ui,
        IPlayerProfileService playerProfiles,
        IFileStorageService storage,
        ILocalizationService localization,
        TransportViewModel? transport = null,
        ISelectionService? selection = null,
        IArtworkSource? artwork = null)
    {
        _artwork = artwork ?? new ArtworkSource(playerProfiles);
        _localization = localization;
        _playbackStatus = localization["StatusBar_Stopped"];
        _statusBar = statusBar;
        _projectState = projectState;
        _audio = audio;
        _ui = ui;
        _playerProfiles = playerProfiles;
        _storage = storage;
        _transport = transport;
        _selection = selection;
        _playerProfiles.ActiveProfileChanged += OnActiveProfileChanged;
        // Cached once: a fresh closure per coalesced 60 Hz post would defeat
        // the point of the throttle below.
        _refreshPlaybackPost = () =>
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
            RefreshPlaybackStatus();
        };
        RefreshProfiles();

        RecentMessages = _statusBar.RecentMessages;
        ActiveTasks = _statusBar.ActiveTasks;

        RecentMessages.CollectionChanged += OnMessagesChanged;
        ActiveTasks.CollectionChanged += OnTasksChanged;

        _projectState.PropertyChanged += OnProjectStateChanged;
        // Edit-state readouts (P1-11). Selection changes fire per selection
        // edit (marquee drags produce bursts), zoom fires per wheel notch —
        // both coalesce into ONE Background-priority post, single-flight, so
        // the rail never competes with input. No per-frame bindings: these are
        // plain strings refreshed on real state changes only.
        _transport?.PropertyChanged += OnTransportChanged;
        _selection?.SelectionChanged += OnSelectionChanged;
        _refreshEditReadoutsPost = () =>
        {
            Interlocked.Exchange(ref _editReadoutsQueued, 0);
            RefreshEditReadouts();
        };
        RefreshEditReadouts();
        _audio.DeviceReconnected += OnAudioDeviceChanged;
        _audio.DeviceLost += OnAudioDeviceLost;
        _audio.DeviceBufferFramesChanged += OnAudioBufferChanged;
        _audio.PositionChanged += OnAudioPositionChanged;

        // Engine readiness and BPM/beat info aren't pushed via dedicated
        // events, so poll once a second. This is cheap (a handful of int
        // reads + one BPM lookup) and avoids the "Audio: not ready" stale
        // label the user reported after loading a PSARC.
        // On the UI thread through the dispatcher abstraction (a DispatcherTimer
        // in the app) rather than a System.Timers.Timer posting from the pool:
        // the pool timer outlived every headless test's app and kept posting
        // into a reset dispatcher for the rest of the run.
        _refreshTimer = _ui.SchedulePeriodic(TimeSpan.FromSeconds(1), () =>
        {
            RefreshAudioStatus();
            RefreshPlaybackStatus();
            FadeMainMessage(Environment.TickCount64);
        });

        RefreshCounts();
        RefreshAudioStatus();
        RefreshActiveTaskLabel();
        RefreshProjectStatus();
        RefreshPlaybackStatus();
    }

    private readonly IDisposable _refreshTimer;

    public ObservableCollection<StatusBarMessage> RecentMessages { get; }
    public ObservableCollection<StatusBarTask> ActiveTasks { get; }

    public string MainMessage => _mainMessageFaded ? "" : _projectState.MainDisplayMessage;

    /// <summary>
    ///     How long routine news stays on the rail. Without it the rail's one line keeps the LAST thing
    ///     said forever — "Loaded Chart: project.rscp" for a whole session. Info and
    ///     success step aside after this; a warning or an error stays until something replaces it,
    ///     because it still needs the user. The full history is one click away in the log chip.
    /// </summary>
    internal const long MainMessageFadeMs = 8000;

    internal static bool MainMessageFades(StatusSeverity severity, long shownAtMs, long nowMs) =>
        severity is StatusSeverity.Info or StatusSeverity.Success && nowMs - shownAtMs >= MainMessageFadeMs;

    private long _mainMessageShownAtMs = Environment.TickCount64;
    private bool _mainMessageFaded;

    /// <summary>Called by the rail's once-a-second timer with the current tick count.</summary>
    internal void FadeMainMessage(long nowMs)
    {
        if (_mainMessageFaded || !MainMessageFades(MainMessageSeverity, _mainMessageShownAtMs, nowMs)) return;
        _mainMessageFaded = true;
        OnPropertyChanged(nameof(MainMessage));
        OnPropertyChanged(nameof(HasMainMessageGlyph));
        OnPropertyChanged(nameof(HasMainMessageAction));
    }

    private void RestartMainMessage()
    {
        _mainMessageShownAtMs = Environment.TickCount64;
        if (!_mainMessageFaded) return;
        _mainMessageFaded = false;
        OnPropertyChanged(nameof(HasMainMessageGlyph));
        OnPropertyChanged(nameof(HasMainMessageAction));
    }

    /// <summary>
    ///     The one verb the message offers beside itself ("Show" on a lossy Editor on Fire
    /// export), from <see cref="ProjectState.MainDisplayAction" />; it goes with the
    ///     message it came with.
    /// </summary>
    public bool HasMainMessageAction => !_mainMessageFaded && _projectState.MainDisplayAction != null;

    public string MainMessageActionLabel => _projectState.MainDisplayAction?.Label ?? "";

    [RelayCommand]
    private void RunMainMessageAction() => _projectState.MainDisplayAction?.Run();

    /// <summary>
    ///     How <see cref="MainMessage" /> reads — the ONE line on the rail used
    ///     to draw a failed save exactly like a finished one, so the view keys
    ///     its brush and glyph off these rather than off the text. Mirrored from
    ///     <see cref="ProjectState.MainDisplaySeverity" />; the flags are what
    ///     the XAML's <c>Classes.err</c> / <c>.warn</c> / <c>.ok</c> bind to.
    /// </summary>
    public StatusSeverity MainMessageSeverity => _projectState.MainDisplaySeverity;

    public bool IsMainMessageError => MainMessageSeverity == StatusSeverity.Error;
    public bool IsMainMessageWarning => MainMessageSeverity == StatusSeverity.Warning;
    public bool IsMainMessageSuccess => MainMessageSeverity == StatusSeverity.Success;

    /// <summary>Anything but <see cref="StatusSeverity.Info" /> gets a glyph in
    /// front of the text; colour alone is not a signal on a rail that also prints
    /// neutral read-outs in colour.</summary>
    public bool HasMainMessageGlyph => !_mainMessageFaded && MainMessageSeverity != StatusSeverity.Info;

    private void RaiseMainMessageSeverityChanged()
    {
        OnPropertyChanged(nameof(MainMessageSeverity));
        OnPropertyChanged(nameof(IsMainMessageError));
        OnPropertyChanged(nameof(IsMainMessageWarning));
        OnPropertyChanged(nameof(IsMainMessageSuccess));
        OnPropertyChanged(nameof(HasMainMessageGlyph));
    }

    // ---- the ⚠/⛔ chips: CURRENT chart lint issues, not accumulated events --
    //
    // These count the CURRENT arrangement's validation issues, not the WARN/ERROR LOG LINES
    // in RecentMessages: a tally since launch (capped at 200) would climb
    // 8→21 during pure library browsing, since the package source alone has
    // eleven Warning sites (missing RS1 audio, art/manifest oddities), and
    // every browsed song appends its parse warnings. A user reads the amber
    // chip as "my chart has N problems", so it shows the CURRENT
    // arrangement's validation issues (pushed by the shell from the
    // inspector's debounced recompute) — a fresh empty project shows 0 and
    // browsing moves nothing. The session log tail is still one click away
    // on its own neutral chip below.

    [ObservableProperty] private int _warningCount;
    [ObservableProperty] private int _errorCount;

    public bool HasWarnings => WarningCount > 0;
    public bool HasErrors => ErrorCount > 0;

    /// <summary>
    ///     Push the current chart's lint counts (called by the shell whenever
    ///     the inspector's debounced validation recompute lands — UI thread).
    ///     <paramref name="important" /> maps to the danger chip,
    ///     <paramref name="warnings" /> to the amber one.
    /// </summary>
    public void SetChartIssueCounts(int important, int warnings)
    {
        ErrorCount = important;
        WarningCount = warnings;
    }

    /// <summary>Assigned by the shell: opens the inspector's Issues tab. A
    /// delegate for the same reason <c>ConfirmDiscardAsync</c> is one — null
    /// means no shell is listening and the chips simply do nothing.</summary>
    public Action? OpenChartIssues { get; set; }

    /// <summary>Assigned by the shell: reads the session's live dirty flag
    /// (<c>IProjectSessionService.IsProjectDirty</c>). A delegate for the same
    /// reason <see cref="OpenChartIssues" /> is one; null (headless hosts)
    /// keeps the marker off. Until this existed <see cref="IsProjectDirty" />
    /// was hard-assigned <c>false</c> on every refresh and bound by nothing —
    /// no surface in the app showed unsaved changes at all.</summary>
    public Func<bool>? DirtyStateSource { get; set; }

    /// <summary>Re-reads <see cref="DirtyStateSource" /> into
    /// <see cref="IsProjectDirty" />. The shell calls this from the session's
    /// <c>ProjectDirtyChanged</c> event (UI thread — edits are UI-driven).</summary>
    public void RefreshDirtyState() => IsProjectDirty = DirtyStateSource?.Invoke() ?? false;

    [RelayCommand]
    private void ShowChartIssues() => OpenChartIssues?.Invoke();

    /// <summary>The session-log chip only exists while there is something to
    /// read — maintained from the log collection's change events.</summary>
    [ObservableProperty] private bool _hasRecentLogMessages;

    [ObservableProperty] private string _audioStatus = string.Empty;

    // ---- editing-state readouts (P1-11) ------------------------------------

    /// <summary>False when the shell was composed without a transport/selection
    /// (headless hosts, tests) — the cluster then hides instead of rendering
    /// four empty strings between two dividers.</summary>
    public bool HasEditReadouts => _transport != null && _selection != null;

    /// <summary>
    ///     Whether the chart editor is the surface on screen; set by the shell. Snap, selection,
    ///     zoom and the transport mean nothing on the Library or in Settings, where they were a long
    ///     line of editor state on the way to the footer's buttons.
    ///     True by default, so a host without a shell shows them as before.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsEditReadouts))]
    private bool _isEditorSurface = true;

    /// <summary>The editing readouts show when they exist AND the editor is the surface.</summary>
    public bool ShowsEditReadouts => HasEditReadouts && IsEditorSurface;

    [ObservableProperty] private string _snapLabel = string.Empty;
    [ObservableProperty] private string _selectionLabel = string.Empty;
    [ObservableProperty] private string _zoomLabel = string.Empty;
    [ObservableProperty] private string _maskLabel = string.Empty;

    /// <summary>The MASK readout only exists while a mask is SET — an empty
    /// mask edits every string and must not read as modal state.</summary>
    public bool HasStringMask => MaskLabel.Length > 0;

    partial void OnMaskLabelChanged(string value)
    {
        _ = value;
        OnPropertyChanged(nameof(HasStringMask));
    }

    private int _editReadoutsQueued;
    private readonly Action _refreshEditReadoutsPost;

    private void QueueEditReadoutsRefresh()
    {
        if (Interlocked.Exchange(ref _editReadoutsQueued, 1) == 0)
            _ui.Post(_refreshEditReadoutsPost);
    }

    private void OnTransportChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TransportViewModel.GridDivisor))
            QueueEditReadoutsRefresh();
    }

    private void OnSelectionChanged(object? sender, EventArgs e) => QueueEditReadoutsRefresh();

    private void RefreshEditReadouts()
    {
        if (_transport == null || _selection == null) return;
        SnapLabel = EditStateReadouts.Snap(_transport.GridDivisor, _localization["StatusBar_Snap"]);
        SelectionLabel = EditStateReadouts.Selection(EditStateReadouts.TotalSelected(_selection), _localization["StatusBar_Selection"]);
        ZoomLabel = EditStateReadouts.Zoom(_projectState.PixelsPerMs, _localization["StatusBar_Zoom"]);
        MaskLabel = EditStateReadouts.Mask(_projectState.StringEditMask, _projectState.ActiveArrangement?.Tuning,
            _localization["StatusBar_Mask"], _localization["Inspector_StringLabelFmt"]);
    }

    [ObservableProperty] private string _playbackStatus;
    [ObservableProperty] private string _bpmLabel = string.Empty;
    [ObservableProperty] private string _positionLabel = string.Empty;
    [ObservableProperty] private string _measureLabel = string.Empty;
    [ObservableProperty] private bool _hasProject;
    [ObservableProperty] private string _projectName = string.Empty;
    [ObservableProperty] private bool _isProjectDirty;

    // ---- the tone the RIG is currently built as ----------------------------

    /// <summary>
    ///     The tone the tone rig is actually built as right now, while something
    ///     is driving it. Empty when nothing is.
    ///     <para>
    ///         It exists because the only reader of the applied tone was
    ///         <c>GameplayToneHud</c> → the 3D highway HUD, which in the chart
    ///         editor means: only with the 3D preview panel open, and only with a
    ///         Play Mode setting on. Charting in the 2D timeline with the tone
    ///         preview running, nothing anywhere said which tone was live — so a
    ///         switch that fired and a switch that silently found no preset looked
    ///         identical.
    ///     </para>
    /// </summary>
    [ObservableProperty] private string _activeToneName = string.Empty;

    /// <summary>The conversion had to APPROXIMATE or skip gear for this tone —
    /// it plays, but it is not what the package specified. Marked rather than
    /// hidden, the same fact the gameplay HUD dims its badge for.</summary>
    [ObservableProperty] private bool _activeToneApproximated;

    public bool HasActiveTone => ActiveToneName.Length > 0;

    partial void OnActiveToneNameChanged(string value) => OnPropertyChanged(nameof(HasActiveTone));

    /// <summary>
    ///     Publish the tone the rig is now built as, or clear it with a blank
    ///     name. Safe from ANY thread — the apply that raises this runs on the
    ///     tone coordinator's worker, never the UI thread.
    /// </summary>
    public void SetActiveTone(string? toneName, bool approximated = false)
    {
        var name = toneName?.Trim() ?? "";
        _ui.Post(() =>
        {
            ActiveToneName = name;
            ActiveToneApproximated = name.Length > 0 && approximated;
        });
    }

    [ObservableProperty] private bool _hasActiveTask;
    [ObservableProperty] private string _activeTaskLabel = string.Empty;
    [ObservableProperty] private double _activeTaskProgress;
    [ObservableProperty] private bool _activeTaskIndeterminate;
    [ObservableProperty] private int _activeTaskCount;
    [ObservableProperty] private bool _activeTaskCanCancel;

    // --- Player profile switcher -------------------------------------------

    public ObservableCollection<ProfileOption> Profiles { get; } = [];

    [ObservableProperty] private string _activeProfileName = string.Empty;
    [ObservableProperty] private IBrush _activeProfileBrush = Brushes.Gray;
    [ObservableProperty] private string _newProfileName = string.Empty;
    [ObservableProperty] private Bitmap? _activeProfileAvatar;
    [ObservableProperty] private bool _hasActiveAvatar;
    [ObservableProperty] private bool _isRenamingProfile;
    [ObservableProperty] private string _renameProfileName = string.Empty;

    /// <summary>Decoded avatar bitmaps owned by the current Profiles snapshot (disposed on refresh).</summary>
    private readonly List<Bitmap> _avatarBitmaps = [];

    private void OnActiveProfileChanged(PlayerProfile _) => _ui.Post(RefreshProfiles);

    /// <summary>The ACTIVE profile's left-handed flag (v0.34), two-way from
    /// the switcher flyout. The setter round-trip is loop-safe: the service
    /// mutator no-ops on an equal value, and refresh only re-fires the
    /// change handler when the value actually differs.</summary>
    [ObservableProperty] private bool _isActiveLefty;

    partial void OnIsActiveLeftyChanged(bool value)
        => _playerProfiles.SetLeftyFlip(_playerProfiles.ActiveProfile.Id, value);

    private void RefreshProfiles()
    {
        var active = _playerProfiles.ActiveProfile;
        ActiveProfileName = active.DisplayName;
        ActiveProfileBrush = ParseAccent(active.AccentColor);
        IsActiveLefty = active.LeftyFlip;
        IsRenamingProfile = false;

        // Detach the old bitmaps from every binding before disposing them.
        Profiles.Clear();
        ActiveProfileAvatar = null;
        var stale = _avatarBitmaps.ToArray();
        _avatarBitmaps.Clear();

        foreach (var profile in _playerProfiles.Profiles)
        {
            var avatar = LoadAvatar(profile.Id);
            if (avatar != null) _avatarBitmaps.Add(avatar);
            var isActive = profile.Id == active.Id;
            if (isActive) ActiveProfileAvatar = avatar;
            Profiles.Add(new ProfileOption(profile, isActive, ParseAccent(profile.AccentColor), avatar)
            {
                Detail = DescribeProfile(profile, DateTime.UtcNow, _localization)
            });
        }

        HasActiveAvatar = ActiveProfileAvatar != null;
        foreach (var bitmap in stale) bitmap.Dispose();
    }

    /// <summary>
    ///     Decodes the avatar at DISPLAY resolution
    ///     (<see cref="ProfileAvatarImage.DecodeWidth" />) through a stream — see
    ///     <see cref="ProfileAvatarImage" /> for why both halves matter. Null for
    ///     an unreadable/corrupt image; the accent dot takes over.
    /// </summary>
    private Bitmap? LoadAvatar(Guid profileId) => ProfileAvatarImage.Load(_artwork, ArtworkKey.ProfileAvatar(profileId));

    // --- Active-profile actions (rename / picture / export / import) --------

    [RelayCommand]
    private void BeginRenameProfile()
    {
        RenameProfileName = _playerProfiles.ActiveProfile.DisplayName;
        IsRenamingProfile = true;
    }

    [RelayCommand]
    private void CommitRenameProfile()
    {
        var name = RenameProfileName.Trim();
        IsRenamingProfile = false;
        if (name.Length == 0 || name == _playerProfiles.ActiveProfile.DisplayName) return;
        _playerProfiles.Rename(_playerProfiles.ActiveProfile.Id, name);
        RefreshProfiles();
    }

    [RelayCommand]
    private void CancelRenameProfile() => IsRenamingProfile = false;

    [RelayCommand]
    private async Task ChooseProfilePictureAsync()
    {
        var path = await _storage.OpenFilePickerAsync(
            _localization["Profile_Picture_Title"],
            new[]
            {
                new FilePickerFilter(_localization["Profile_ImageFilter"],
                    new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif", "*.webp" })
            });
        if (string.IsNullOrEmpty(path)) return;

        // Downscale BEFORE the service copies it: SetAvatar is a plain File.Copy
        // (Services owns no image dependency), so without this a 12 MP phone
        // photo lives in the profile folder forever and travels in every profile
        // export. Returns the original path when no downscale was needed.
        var prepared = ProfileAvatarImage.PrepareForImport(path, _artwork);
        try
        {
            _playerProfiles.SetAvatar(_playerProfiles.ActiveProfile.Id, prepared);
        }
        finally
        {
            if (!string.Equals(prepared, path, StringComparison.Ordinal))
                _artwork.DeleteStaged(prepared);
        }

        RefreshProfiles();
    }

    [RelayCommand]
    private async Task ExportProfileAsync()
    {
        var active = _playerProfiles.ActiveProfile;
        var path = await _storage.SaveFilePickerAsync(
            _localization["Profile_Export_Title"],
            active.DisplayName + ".dcprofile",
            new[]
            {
                new FilePickerFilter(_localization["Profile_FileFilter"],
                    new[] { "*.dcprofile" })
            });
        if (string.IsNullOrEmpty(path)) return;
        _playerProfiles.ExportProfile(active.Id, path);
    }

    [RelayCommand]
    private async Task ImportProfileAsync()
    {
        var path = await _storage.OpenFilePickerAsync(
            _localization["Profile_Import_Title"],
            new[]
            {
                new FilePickerFilter(_localization["Profile_FileFilter"],
                    new[] { "*.dcprofile" })
            });
        if (string.IsNullOrEmpty(path)) return;
        var imported = _playerProfiles.ImportProfile(path);
        if (imported != null) _playerProfiles.SwitchTo(imported.Id);
        RefreshProfiles();
    }

    /// <summary>
    ///     Builds <see cref="ProfileOption.Detail" />: "LAST USED 3 W AGO" with
    ///     "· LEFT-HANDED" appended when the profile plays mirrored. Both halves
    ///     are stored fields of <see cref="PlayerProfile" /> — nothing here is
    ///     derived from anything the app does not persist.
    ///     <para>
    ///         <c>internal static</c> with an explicit <paramref name="nowUtc" />
    ///         so the bucketing can be asserted without a clock, a service or a
    ///         UI thread. A profile with no <c>LastUsedUtc</c> (a hand-edited or
    ///         pre-0.11 registry entry) reports only what it knows rather than
    ///         "55 years ago".
    ///     </para>
    /// </summary>
    internal static string DescribeProfile(PlayerProfile profile, DateTime nowUtc, ILocalizationService loc)
    {
        var parts = new List<string>(2);

        if (profile.LastUsedUtc != default)
        {
            parts.Add(loc.Format("PlayMode_Launch_LastUsedFmt",
                PlayMode.RelativeTimeText.Format(
                    RelativeTime.Between(profile.LastUsedUtc, nowUtc), loc)));
        }

        if (profile.LeftyFlip) parts.Add(loc["PlayMode_Launch_LeftyTag"]);

        return string.Join(" · ", parts);
    }

    private static IBrush ParseAccent(string hex)
    {
        try
        {
            return new SolidColorBrush(Color.Parse(hex));
        }
        catch
        {
            return Brushes.Gray;
        }
    }

    [RelayCommand]
    private void SwitchProfile(ProfileOption? option)
    {
        if (option == null || option.IsActive) return;
        _playerProfiles.SwitchTo(option.Profile.Id);
        RefreshProfiles();
    }

    [RelayCommand]
    private void CreateProfile()
    {
        var name = NewProfileName.Trim();
        if (name.Length == 0) return;
        var created = _playerProfiles.Create(name);
        NewProfileName = string.Empty;
        _playerProfiles.SwitchTo(created.Id);
        RefreshProfiles();
    }

    /// <summary>Invokes the primary task's cancel hook (the pill's ✕).</summary>
    [RelayCommand]
    private void CancelActiveTask()
    {
        var primary = ActiveTasks.Count > 0 ? ActiveTasks[0] : null;
        primary?.CancelAction?.Invoke();
    }

    partial void OnWarningCountChanged(int value) { _ = value; OnPropertyChanged(nameof(HasWarnings)); }
    partial void OnErrorCountChanged(int value) { _ = value; OnPropertyChanged(nameof(HasErrors)); }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e) => _ui.Post(RefreshCounts);
    private void OnTasksChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Subscribe / unsubscribe synchronously (it's the COLLECTION change
        // that runs on whichever thread mutated the list); just bounce the
        // label refresh that touches [ObservableProperty]s to the UI thread.
        if (e.OldItems != null)
            foreach (StatusBarTask t in e.OldItems) t.PropertyChanged -= OnTaskPropertyChanged;
        if (e.NewItems != null)
            foreach (StatusBarTask t in e.NewItems) t.PropertyChanged += OnTaskPropertyChanged;
        _ui.Post(RefreshActiveTaskLabel);
    }

    private void OnTaskPropertyChanged(object? sender, PropertyChangedEventArgs e) => _ui.Post(RefreshActiveTaskLabel);

    private void OnProjectStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProjectState.MainDisplayMessage))
        {
            _ui.Post(() =>
            {
                RestartMainMessage();
                OnPropertyChanged(nameof(MainMessage));
            });
        }
        else if (e.PropertyName == nameof(ProjectState.MainDisplayAction))
        {
            _ui.Post(() =>
            {
                OnPropertyChanged(nameof(HasMainMessageAction));
                OnPropertyChanged(nameof(MainMessageActionLabel));
            });
        }
        else if (e.PropertyName == nameof(ProjectState.MainDisplaySeverity))
        {
            _ui.Post(() =>
            {
                RestartMainMessage();
                RaiseMainMessageSeverityChanged();
            });
        }
        else if (e.PropertyName is nameof(ProjectState.ActiveProject)
                 or nameof(ProjectState.CurrentProjectPath))
        {
            _ui.Post(RefreshProjectStatus);
        }
        else if (e.PropertyName is nameof(ProjectState.PixelsPerMs)
                 or nameof(ProjectState.StringEditMask)
                 or nameof(ProjectState.ActiveArrangement))
        {
            QueueEditReadoutsRefresh();
        }
    }

    private void OnAudioDeviceChanged(object? sender, AudioDeviceReconnectedEventArgs e) => _ui.Post(() =>
    {
        RefreshAudioStatus();
        // Auto-recovery succeeded (device watchdog): tell the user their
        // interface is back without any settings round-trip.
        _projectState.SetMessage(string.IsNullOrEmpty(e.DeviceName)
            ? _localization["Audio_DeviceRecoveredDefault"]
            : _localization.Format("Audio_DeviceRecovered", e.DeviceName));
    });

    private void OnAudioDeviceLost(object? sender, EventArgs e) => _ui.Post(() =>
    {
        RefreshAudioStatus();
        _projectState.SetMessage(_localization["Audio_DeviceLost"], StatusSeverity.Error);
    });

    private void OnAudioBufferChanged(object? sender, int frames) => _ui.Post(RefreshAudioStatus);

    // PositionChanged fires on the audio timer thread at up to 250 Hz.
    // Same contract as AudioPlaybackCoordinator: throttle to ~60 Hz and keep
    // at most one dispatcher post in flight, otherwise the status bar floods
    // the UI thread with per-tick refreshes during playback.
    private const long RefreshIntervalTicks = 16; // ms via Environment.TickCount64
    private long _lastRefreshTicks;
    private int _refreshQueued;
    private readonly Action _refreshPlaybackPost;

    private void OnAudioPositionChanged(object? sender, double ms)
    {
        var nowTicks = Environment.TickCount64;
        if (nowTicks - _lastRefreshTicks < RefreshIntervalTicks) return;
        _lastRefreshTicks = nowTicks;
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 0)
            _ui.Post(_refreshPlaybackPost);
    }

    // Project identity only changes on load/save — kept off the 60 Hz
    // playback-refresh path (re-deriving ProjectName via
    // Path.GetFileNameWithoutExtension on every tick would be wasted work).
    private void RefreshProjectStatus()
    {
        var proj = _projectState.ActiveProject;
        HasProject = proj != null;
        // The song, not the file: every project saves as project.rscp, and a song Play
        // Mode loaded has no file yet but knows its artist and title.
        var name = proj == null
            ? string.Empty
            : DotCharter.Services.Session.ProjectDisplayName.For(_projectState.CurrentProjectPath, proj.Chart);
        ProjectName = proj != null && name.Length == 0 ? _localization["Project_Untitled"] : name;
        // Re-READ, never reset: a project switch usually lands clean, but an
        // attach-audio path change fires this too and the session stays dirty.
        RefreshDirtyState();
    }

    private void RefreshPlaybackStatus()
    {
        PlaybackStatus = _audio.IsPlaying
            ? _localization["StatusBar_Playing"]
            : _localization["StatusBar_Stopped"];
        var ms = _audio.CurrentTimeMs;
        // 10 ms-quantized cached label ("mm:ss.ff") — repeated refreshes of
        // the same quantum return the identical string instance, so the
        // property setter short-circuits with zero allocation.
        PositionLabel = RenderStringCache.MmSsFf(Math.Max(0, ms));

        var beats = _projectState.Beats;
        if (beats.Count > 0)
        {
            int lo = 0, hi = beats.Count - 1, idx = 0;
            while (lo <= hi)
            {
                var mid = (lo + hi) >> 1;
                if (beats[mid].Time <= ms) { idx = mid; lo = mid + 1; }
                else
                {
                    hi = mid - 1;
                }
            }
            // Count beats from last measure marker at/below idx. Walk only
            // the delta since the previous refresh; recount from scratch on
            // a backwards seek or when the beat list itself changes.
            if (!ReferenceEquals(beats, _measureScanBeats) || beats.Count != _measureScanCount ||
                idx < _measureScanIndex)
            {
                _measureScanBeats = beats;
                _measureScanCount = beats.Count;
                _measureScanIndex = -1;
                _measureCount = BarCount.Zero;
            }

            // The shared rule (DotCharter.Core's BarCount), folded one beat at a
            // time so this incremental scanner and ChartPositionLabel's
            // from-scratch walk cannot drift apart. Starting at bar 1 and
            // incrementing only after beat 0 would, on a PICKUP grid, print a bar
            // number one past the pill the timeline draws at the same instant.
            _measureCount = _measureCount.AdvanceThrough(beats, _measureScanIndex + 1, idx);

            _measureScanIndex = idx;
            // Reformat only when the value actually moved — the labels
            // change a few times per second, the refresh runs at 60 Hz.
            var measureKey = (_measureCount.Bar << 10) | _measureCount.BeatInBar;
            if (measureKey != _lastMeasureKey)
            {
                _lastMeasureKey = measureKey;
                MeasureLabel = $"M{_measureCount.Bar}.{_measureCount.BeatInBar}";
            }

            var bpm = beats[idx].Bpm;
            if (bpm != _lastBpm)
            {
                _lastBpm = bpm;
                BpmLabel = bpm > 0 ? $"{bpm:0.#} BPM" : string.Empty;
            }
        }
        else
        {
            _lastMeasureKey = -1;
            _lastBpm = double.NaN;
            MeasureLabel = string.Empty;
            BpmLabel = string.Empty;
        }
    }

    private int _lastMeasureKey = -1;
    private double _lastBpm = double.NaN;

    private object? _measureScanBeats;
    private int _measureScanCount;
    private int _measureScanIndex = -1;
    private BarCount _measureCount = BarCount.Zero;

    private void RefreshCounts() =>
        // The chips do not derive from the log tail (see the block comment
        // at WarningCount) — only the session-log chip's visibility does.
        HasRecentLogMessages = RecentMessages.Count > 0;

    private void RefreshAudioStatus()
    {
        var rate = _audio.ActiveSampleRate;
        var buf = _audio.ActualBufferFrames;
        if (rate <= 0 || buf <= 0)
        {
            AudioStatus = _localization["StatusBar_Audio_NotReady"];
            return;
        }
        var ms = buf * 1000.0 / rate;
        // The BUFFER's duration, labelled as such: output latency is a larger, separate figure.
        AudioStatus = _localization.Format("StatusBar_Audio_Fmt", rate / 1000.0, buf, ms);
    }

    private void RefreshActiveTaskLabel()
    {
        ActiveTaskCount = ActiveTasks.Count;
        HasActiveTask = ActiveTasks.Count > 0;
        if (ActiveTasks.Count == 0)
        {
            ActiveTaskLabel = string.Empty;
            ActiveTaskProgress = 0;
            ActiveTaskIndeterminate = false;
            ActiveTaskCanCancel = false;
            return;
        }
        var primary = ActiveTasks[0];
        ActiveTaskLabel = ActiveTasks.Count > 1
            ? _localization.Format("StatusBar_Task_MoreFmt", primary.Label, ActiveTasks.Count - 1)
            : primary.Label;
        ActiveTaskIndeterminate = primary.IsIndeterminate;
        ActiveTaskProgress = primary.Progress;
        ActiveTaskCanCancel = primary.CanCancel;
    }

    [RelayCommand]
    public void ClearMessages() => _statusBar.ClearMessages();

    public void Dispose()
    {
        _refreshTimer.Dispose();
        _playerProfiles.ActiveProfileChanged -= OnActiveProfileChanged;
        RecentMessages.CollectionChanged -= OnMessagesChanged;
        ActiveTasks.CollectionChanged -= OnTasksChanged;
        _projectState.PropertyChanged -= OnProjectStateChanged;
        _transport?.PropertyChanged -= OnTransportChanged;
        _selection?.SelectionChanged -= OnSelectionChanged;
        _audio.DeviceReconnected -= OnAudioDeviceChanged;
        _audio.DeviceLost -= OnAudioDeviceLost;
        _audio.DeviceBufferFramesChanged -= OnAudioBufferChanged;
        _audio.PositionChanged -= OnAudioPositionChanged;
        foreach (var t in ActiveTasks) t.PropertyChanged -= OnTaskPropertyChanged;
        foreach (var bitmap in _avatarBitmaps) bitmap.Dispose();
        _avatarBitmaps.Clear();
    }
}
