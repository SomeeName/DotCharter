using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DotCharter.Localization;
using DotCharter.Services.Ai.Generate;
using DotCharter.Tone.Audio.Dsp.Tones;
using DotCharter.Tone.Audio.Tonelab;
using DotCharter.Tone.Diagnostics;
using DotCharter.Tone.Parsing.Psarc;

namespace DotCharter.Services.Audio.Coordination;

/// <summary>
///     The chart editor's "Preview tones: DotCharter rig / Rocksmith" choice. While Rocksmith is
///     chosen and the editor preview drives the rig (the toolbar toggle, the chart synth or
///     both), every tone the coordinator puts on the playhead is also sent to the game through
///     a <see cref="TonelabTonePreview" /> (validate, debounce, <c>apply-file</c>), whose first
///     accepted tone claims the session's audio route. The chart synth is mixed into seat 0's
///     chain BEFORE the native external insert, so the synth and the live guitar both reach the
///     game; the clone rig keeps being driven underneath (bypassed while the insert is open).
///     <list type="bullet">
///         <item>Rocksmith is selectable only while the tonelab session is ready, and otherwise
///         says why (<see cref="UnavailableReasonKey" />).</item>
///         <item>When the preview stops (off, a Play Mode run taking the rig, close) the game's
///         tone is restored and the route let go. A session that ends while chosen falls back to
///         the rig and says so (<see cref="Notice" />).</item>
///         <item>No lookahead: a switch reaches the game after the coordinator's apply, the
///         preview's debounce and the game's own tone tick (see the tonelab guide).</item>
///     </list>
/// </summary>
public sealed class EditorRocksmithTonePreview : ObservableObject, IDisposable
{
    private readonly RsTonePlaybackCoordinator _tones;
    private readonly ITonelabSession _session;
    private readonly ILocalizationService _loc;
    private readonly IAppLogger _logger;
    private readonly IUiDispatcher? _ui;
    private readonly TonelabTonePreview _inGame;
    private readonly object _gate = new();
    private readonly Action<ActiveToneInfo> _onToneApplied;
    private readonly Action _onDrivingStopped;
    private readonly EventHandler _onSessionChanged;
    private volatile IReadOnlyDictionary<string, RsTone> _projectTones = new Dictionary<string, RsTone>();
    private bool _isChosen;
    private string _notice = "";
    private bool _disposed;
    private volatile bool _startRequested;

    public EditorRocksmithTonePreview(RsTonePlaybackCoordinator tones, ITonelabSession session, ILocalizationService loc,
        IAppLogger logger, IUiDispatcher? ui)
        : this(tones, session, loc, logger, ui,
            new TonelabTonePreview(session, Validate, new TonelabToneFiles(), TimerTonelabScheduler.Instance, logger))
    {
    }

    internal EditorRocksmithTonePreview(RsTonePlaybackCoordinator tones, ITonelabSession session, ILocalizationService loc,
        IAppLogger logger, IUiDispatcher? ui, TonelabTonePreview inGame)
    {
        _tones = tones;
        _session = session;
        _loc = loc;
        _logger = logger;
        _ui = ui;
        _inGame = inGame;
        _onToneApplied = OnToneApplied;
        _onDrivingStopped = OnDrivingStopped;
        _onSessionChanged = (_, _) => Post(OnSessionChanged);
        _tones.ActiveToneChanged += _onToneApplied;
        _tones.DrivingStopped += _onDrivingStopped;
        _session.StateChanged += _onSessionChanged;
        _inGame.Completed += OnCompleted;
    }

    /// <summary>The preview plays through Rocksmith (true) or the DotCharter rig (false, the default).</summary>
    public bool IsChosen => _isChosen;

    /// <summary>Rocksmith can be chosen: the tonelab session is ready.</summary>
    public bool IsAvailable => _session.State == TonelabSessionState.Ready;

    /// <summary>No game side yet, but the app has a launcher: the switch starts Rocksmith (<see cref="StartAsync" />).</summary>
    public bool CanStart => !_disposed && _session.State == TonelabSessionState.Disconnected && _session.CanLaunch;

    /// <summary>Why Rocksmith cannot be chosen, as a resx key; empty while it can.</summary>
    public string UnavailableReasonKey =>
        IsAvailable ? ""
        : _session.State == TonelabSessionState.Starting ? "Dialog_Tones_InGameStarting"
        : _session.LastStatus?.Result is { Outcome: TonelabOutcome.UnsupportedVersion } ? "Dialog_Tones_InGameVersion"
        : _session.LastLaunchFailure == GameToneLaunchFailure.SteamNotRunning ? "Dialog_Tones_InGameSteamNotRunning"
        : _session.LastLaunchFailure != GameToneLaunchFailure.None ? "Dialog_Tones_InGameLaunchFailed"
        : CanStart ? "Toolbar_TonePreviewRocksmith_Start"
        : "Toolbar_TonePreviewRocksmith_NotConnected";

    /// <summary>The last thing worth saying: a fall-back to the rig or a tone the game refused. Empty otherwise.</summary>
    public string Notice { get => _notice; private set => SetProperty(ref _notice, value); }

    /// <summary>The Rocksmith switch's tooltip: the reason while unavailable, else what it does (and the last notice).</summary>
    public string Tip
    {
        get
        {
            var reason = UnavailableReasonKey;
            var text = reason switch
            {
                "" => _loc["Toolbar_TonePreviewRocksmith_Tip"],
                "Dialog_Tones_InGameVersion" => string.Format(CultureInfo.CurrentCulture, _loc[reason],
                    _session.LastStatus?.Result.Detail, "v" + TonelabProtocol.Version),
                _ => _loc[reason]
            };
            return Notice.Length == 0 ? text : Notice + Environment.NewLine + text;
        }
    }

    internal TonelabTonePreview InGame => _inGame;

    /// <summary>
    ///     The catalogue gate: a tone naming gear the game does not have, or a knob off its range, is never sent.
    ///     The key is not held to the authoring rule: the tone already exists, and an official package's key may
    ///     start with a digit ('311allm_clean'), which the game loads.
    /// </summary>
    public static TonelabResult? Validate(RsTone tone) =>
        RsToneCatalogValidation.FirstLoadFault(tone, RsGearCatalog.Shared) is { } fault
            ? new TonelabResult(TonelabOutcome.Invalid, fault.Message) { ErrorCode = fault.Code }
            : null;

    /// <summary>The project's tones as the game takes them, by key AND name (a tone change names either).</summary>
    public static IReadOnlyDictionary<string, RsTone> ParseTones(ChartProject? project, IReadOnlyDictionary<string, string>? bodyEdits)
    {
        var tones = new Dictionary<string, RsTone>(StringComparer.OrdinalIgnoreCase);
        if (project is null) return tones;
        foreach (var definition in project.ToneDefinitions)
        {
            var json = bodyEdits is not null && bodyEdits.TryGetValue(definition.Key, out var edited) ? edited : definition.Json;
            if (string.IsNullOrWhiteSpace(json)) continue;
            var tone = RsToneJsonParser.Parse(new PsarcToneDefinition(definition.Key, definition.Name, json));
            foreach (var alias in new[] { definition.Key, definition.Name })
            {
                if (!string.IsNullOrWhiteSpace(alias)) tones.TryAdd(alias.Trim(), tone);
            }
        }

        return tones;
    }

    /// <summary>The project's tones changed (a body committed, a designer edit, another project): what later sends use.</summary>
    public void SetProjectTones(IReadOnlyDictionary<string, RsTone> tones) => _projectTones = tones;

    /// <summary>Picks the target. Rocksmith is refused (false) while the session is not ready.</summary>
    public bool Choose(bool rocksmith)
    {
        if (_disposed) return false;
        if (rocksmith && !IsAvailable) return false;
        if (rocksmith == _isChosen) return true;
        lock (_gate)
        {
            _isChosen = rocksmith;
            if (!rocksmith) _inGame.Stop();
            else if (_tones.IsPreviewing) SendLocked(_tones.CurrentToneName);
        }

        Notice = "";
        _logger.Info(AppLogCategory.Audio, $"Tone preview: target {(rocksmith ? "Rocksmith" : "the DotCharter rig")}.");
        Raise();
        return true;
    }

    /// <summary>
    ///     The editor preview just took the rig: sends the tone at the playhead. Needed because the
    ///     coordinator's pre-roll apply fires before it reports itself as previewing.
    /// </summary>
    public void PreviewStarted()
    {
        if (_disposed) return;
        lock (_gate)
        {
            if (_isChosen && _tones.IsPreviewing) SendLocked(_tones.CurrentToneName);
        }
    }

    /// <summary>
    ///     Starts Rocksmith through the session's launcher (off the UI thread) and chooses it once the
    ///     game side is ready. The session keeps polling a starting game; this waits for that too.
    /// </summary>
    public Task StartAsync()
    {
        if (!CanStart) return Task.CompletedTask;
        var session = _session;
        var logger = _logger;
        return Task.Run(async () =>
        {
            try
            {
                await session.LaunchAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.Warning(AppLogCategory.Audio, "tonelab: starting Rocksmith failed.", ex);
            }

            _startRequested = true;
            Post(OnSessionChanged);
        });
    }

    /// <summary>Looks for a running game side (off the UI thread) unless one is ready already.</summary>
    public Task ProbeAsync()
    {
        if (_disposed || IsAvailable) return Task.CompletedTask;
        var session = _session;
        var logger = _logger;
        return Task.Run(async () =>
        {
            try
            {
                await session.ProbeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.Warning(AppLogCategory.Audio, "tonelab: probe failed.", ex);
            }
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _tones.ActiveToneChanged -= _onToneApplied;
        _tones.DrivingStopped -= _onDrivingStopped;
        _session.StateChanged -= _onSessionChanged;
        _inGame.Completed -= OnCompleted;
        lock (_gate)
        {
            _isChosen = false;
            _inGame.Dispose();
        }
    }

    // The apply worker's thread (or the UI thread for the pre-roll). The gate orders a late
    // apply against a stop: once the coordinator stopped previewing, nothing more is sent.
    private void OnToneApplied(ActiveToneInfo info)
    {
        if (info.SongIdentity is not null) return;   // a Play Mode run's package tone, not the editor's
        lock (_gate)
        {
            if (_isChosen && _tones.IsPreviewing) SendLocked(info.ToneName);
        }
    }

    private void OnDrivingStopped()
    {
        lock (_gate) _inGame.Stop();
    }

    private void SendLocked(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        if (_projectTones.TryGetValue(key.Trim(), out var tone)) _inGame.Apply(tone);
        else _logger.Info(AppLogCategory.Audio, $"tonelab: the editor preview has no tone body for '{key}'; the game keeps its tone.");
    }

    private void OnSessionChanged()
    {
        if (_disposed) return;
        // A start the switch asked for picks Rocksmith as soon as the game side is ready.
        if (_startRequested && IsAvailable)
        {
            _startRequested = false;
            Choose(true);
            return;
        }

        if (_isChosen && !IsAvailable)
        {
            lock (_gate)
            {
                _isChosen = false;
                _inGame.Stop();
            }

            Notice = _loc["Dialog_Tones_InGameEnded"];
            _logger.Info(AppLogCategory.Audio, "Tone preview: the Rocksmith session ended; back on the DotCharter rig.");
        }

        Raise();
    }

    private void OnCompleted(object? sender, TonelabResult result) => Post(() =>
    {
        if (_disposed) return;
        Notice = result.Outcome switch
        {
            TonelabOutcome.Ok => "",
            TonelabOutcome.UnknownGear => Format("Dialog_Tones_InGameUnknownGear", result.Detail),
            TonelabOutcome.Invalid => Format("Dialog_Tones_InGameInvalid", result.Detail),
            TonelabOutcome.Timeout => _loc["Dialog_Tones_InGameTimeout"],
            TonelabOutcome.NotConnected => _loc["Dialog_Tones_InGameEnded"],
            TonelabOutcome.Malformed => _loc["Dialog_Tones_InGameMalformed"],
            _ => Format("Dialog_Tones_InGameRejected", (result.ErrorCode + " " + result.Detail).Trim())
        };
        OnPropertyChanged(nameof(Tip));
    });

    private string Format(string key, string arg) => string.Format(CultureInfo.CurrentCulture, _loc[key], arg);

    private void Raise()
    {
        OnPropertyChanged(nameof(IsChosen));
        OnPropertyChanged(nameof(IsAvailable));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(UnavailableReasonKey));
        OnPropertyChanged(nameof(Tip));
    }

    private void Post(Action action)
    {
        if (_ui is null || _ui.CheckAccess()) action();
        else _ui.PostNormal(action);
    }
}
