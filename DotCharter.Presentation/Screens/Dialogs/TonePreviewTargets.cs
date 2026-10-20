using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Services.Ai.Generate;
using DotCharter.Services.Infrastructure.Configuration;
using DotCharter.Tone.Audio.Abstractions;
using DotCharter.Tone.Audio.Dsp.Tones;
using DotCharter.Tone.Audio.Tonelab;
using DotCharter.Tone.Diagnostics;
using DotCharter.Tone.Infrastructure.Configuration;
using DotCharter.Tone.Logging;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>
///     The Tone designer's "Preview in: DotCharter rig / Rocksmith" choice. The rig (the
///     clone, <see cref="TonePreviewSession" />) is the default; Rocksmith is only
///     selectable while a tonelab session reports ready, and otherwise says why. Owns the
///     in-game target (<see cref="TonelabTonePreview" />), hands it the gear-catalogue
///     check (<see cref="RsToneCatalogValidation" />, which lives in Services and so cannot
///     be called from Tone), and turns each send's result into the one status line the
///     dialog shows. Session and send events arrive on worker threads and are posted to
///     the UI thread.
/// </summary>
public sealed class TonePreviewTargets : ObservableObject, IToneDesignerPreviewTargets, ITonePreviewTarget, IDisposable
{
    private readonly ILocalizationService _loc;
    private readonly IAppLogger _logger;
    private readonly ITonelabSession _session;
    private readonly IUiDispatcher _ui;
    private readonly IConfigurationService _config;
    private readonly TonelabTonePreview _inGame;
    private readonly EventHandler _sessionRelay;
    private bool _isInGame;
    private RsTone? _last;
    private string _status = "";
    private bool _disposed;

    public TonePreviewTargets(TonePreviewSession clone, ILocalizationService loc, IAppLogger logger, ITonelabSession session, IUiDispatcher ui,
        IConfigurationService config)
        : this(clone, loc, logger, session, ui, config,
            new TonelabTonePreview(session, Validate, new TonelabToneFiles(), TimerTonelabScheduler.Instance, logger))
    {
    }

    internal TonePreviewTargets(TonePreviewSession clone, ILocalizationService loc, IAppLogger logger, ITonelabSession session,
        IUiDispatcher ui, IConfigurationService config, TonelabTonePreview inGame)
    {
        Clone = clone;
        _loc = loc;
        _logger = logger;
        _session = session;
        _ui = ui;
        _config = config;
        _inGame = inGame;
        _inGame.Completed += OnCompleted;
        _sessionRelay = WeakRelay(session, this);
        session.StateChanged += _sessionRelay;
        ReconnectCommand = new AsyncRelayCommand(ReconnectAsync);
        LaunchCommand = new AsyncRelayCommand(LaunchAsync);
        if (session.State != TonelabSessionState.Ready) _ = ProbeAsync(session, logger);
    }

    /// <summary>Raised on the UI thread when the target the preview goes to changes.</summary>
    public event EventHandler? TargetChanged;

    public bool IsInGame
    {
        get => _isInGame;
        set
        {
            if (value && !CanUseInGame) value = false;
            if (value == _isInGame) return;
            // A running preview moves with the switch: the old target restores, the new one takes the tone.
            var old = Current;
            var wasActive = old.IsActive;
            old.Stop();
            SetProperty(ref _isInGame, value);
            if (wasActive && _last is { } tone && Current.IsAvailable) Current.Apply(tone);
            OnPropertyChanged(nameof(IsClone));
            OnPropertyChanged(nameof(AsioNote));
            OnPropertyChanged(nameof(HasAsioNote));
            if (!value) SetStatus("");
            TargetChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool IsClone
    {
        get => !_isInGame;
        set
        {
            if (value) IsInGame = false;
        }
    }

    public bool CanUseInGame => _session.State == TonelabSessionState.Ready;

    public string InGameUnavailableReason =>
        CanUseInGame ? ""
        : _session.State == TonelabSessionState.Starting ? _loc["Dialog_Tones_InGameStarting"]
        : _session.LastStatus?.Result is { Outcome: TonelabOutcome.UnsupportedVersion } old
            ? string.Format(CultureInfo.CurrentCulture, _loc["Dialog_Tones_InGameVersion"], old.Detail, "v" + TonelabProtocol.Version)
        : _session.LastLaunchFailure == GameToneLaunchFailure.SteamNotRunning ? _loc["Dialog_Tones_InGameSteamNotRunning"]
        : _session.LastLaunchFailure != GameToneLaunchFailure.None ? _loc["Dialog_Tones_InGameLaunchFailed"]
        : _session.CanLaunch ? _loc["Dialog_Tones_InGameNotRunning"]
        : _loc["Dialog_Tones_InGameNotConnected"];

    /// <summary>Looks for a running game side again (the newest, when there are several); off the UI thread.</summary>
    public ICommand ReconnectCommand { get; }

    /// <summary>Only with a launcher (an optional plugin): start Rocksmith with tonelab and attach.</summary>
    public bool CanLaunch => _session.CanLaunch;

    public ICommand LaunchCommand { get; }

    /// <summary>
    ///     Control-only mode: the game plays through its OWN device, and an ASIO driver serves one
    ///     program at a time. While Rocksmith is chosen and DotCharter itself is on ASIO, this says
    ///     what to do; DotCharter does not release its device for the game in this phase.
    /// </summary>
    public string AsioNote => _isInGame && _session.GameHoldsAudioDevice && IsOnAsio() ? _loc["Dialog_Tones_InGameAsioNote"] : "";

    public bool HasAsioNote => AsioNote.Length > 0;

    public bool HasInGameUnavailableReason => !CanUseInGame;

    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    public bool HasStatus => _status.Length > 0;

    /// <summary>The default target: the DotCharter rig (the clone).</summary>
    public TonePreviewSession Clone { get; }

    /// <summary>Where the preview goes now: the game when chosen, else the rig.</summary>
    internal ITonePreviewTarget Current => _isInGame ? _inGame : Clone;

    internal TonelabTonePreview InGame => _inGame;

    /// <summary>The chosen target's availability: the rig needs the native chain, the game a ready session.</summary>
    public bool IsAvailable => Current.IsAvailable;

    public bool IsActive => Current.IsActive;

    public void Apply(RsTone tone)
    {
        _last = tone;
        Current.Apply(tone);
    }

    public bool Stop()
    {
        _last = null;
        return Current.Stop();
    }

    /// <summary>Ends a running preview on whichever target holds it, then lets go of the session.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _disposed = true;
        _session.StateChanged -= _sessionRelay;
        _inGame.Completed -= OnCompleted;
        _inGame.Dispose();
    }

    /// <summary>The catalogue gate: a tone naming gear the game does not have, or a knob off its range, is never sent.</summary>
    internal static TonelabResult? Validate(RsTone tone) => DotCharter.Services.Audio.Coordination.EditorRocksmithTonePreview.Validate(tone);

    /// <summary>The line the dialog prints for one result; empty for a quiet success.</summary>
    internal string Describe(TonelabResult result, ExternalInsertStats? audio) => result.Outcome switch
    {
        TonelabOutcome.Ok when audio is not null => Format("Dialog_Tones_InGameLatency", audio.LatencyMs.ToString("0.#", CultureInfo.CurrentCulture)),
        TonelabOutcome.Ok => _loc["Dialog_Tones_InGameApplied"],
        TonelabOutcome.UnknownGear => Format("Dialog_Tones_InGameUnknownGear", result.Detail),
        TonelabOutcome.Invalid => Format("Dialog_Tones_InGameInvalid", result.Detail),
        TonelabOutcome.Timeout => _loc["Dialog_Tones_InGameTimeout"],
        TonelabOutcome.NotConnected => _loc["Dialog_Tones_InGameEnded"],
        TonelabOutcome.Malformed => _loc["Dialog_Tones_InGameMalformed"],
        _ => Format("Dialog_Tones_InGameRejected", (result.ErrorCode + " " + result.Detail).Trim())
    };

    private string Format(string key, string arg) => string.Format(CultureInfo.CurrentCulture, _loc[key], arg);

    /// <summary>Static, so a probe still queued never keeps this (and the designer it serves) alive.</summary>
    private static async Task ProbeAsync(ITonelabSession session, IAppLogger logger)
    {
        try
        {
            await Task.Run(() => session.ProbeAsync()).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.Warning(AppLogCategory.Audio, "tonelab: probe failed.", ex);
        }
    }

    private async Task ReconnectAsync()
    {
        try
        {
            await Task.Run(() => _session.ReconnectAsync()).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warning(AppLogCategory.Audio, "tonelab: reconnect failed.", ex);
        }

        Post(RefreshAvailability);
    }

    private async Task LaunchAsync()
    {
        try
        {
            await Task.Run(() => _session.LaunchAsync()).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warning(AppLogCategory.Audio, "tonelab: launch failed.", ex);
        }

        Post(RefreshAvailability);
    }

    private bool IsOnAsio() => string.Equals(_config.LoadRigConfig().AudioDriver, "ASIO", StringComparison.OrdinalIgnoreCase);

    private void RefreshAvailability()
    {
        if (_disposed) return;
        OnPropertyChanged(nameof(CanUseInGame));
        OnPropertyChanged(nameof(InGameUnavailableReason));
        OnPropertyChanged(nameof(HasInGameUnavailableReason));
    }

    private void OnCompleted(object? sender, TonelabResult result)
    {
        var audio = _session.AudioStats;
        Post(() =>
        {
            if (_disposed || !_isInGame) return;
            SetStatus(Describe(result, audio));
        });
    }

    /// <summary>
    ///     The session is an app singleton; a designer that is never disposed must not stay
    ///     alive through its event (and keep its project alive with it), so it listens weakly.
    /// </summary>
    private static EventHandler WeakRelay(ITonelabSession session, TonePreviewTargets targets)
    {
        var weak = new WeakReference<TonePreviewTargets>(targets);
        EventHandler? relay = null;
        relay = (sender, e) =>
        {
            if (weak.TryGetTarget(out var alive)) alive.OnSessionChanged(sender, e);
            else session.StateChanged -= relay;
        };
        return relay;
    }

    private void OnSessionChanged(object? sender, EventArgs e) => Post(() =>
    {
        if (_disposed) return;
        RefreshAvailability();
        if (!_isInGame || CanUseInGame) return;
        IsInGame = false;
        SetStatus(_loc["Dialog_Tones_InGameEnded"]);
    });

    private void SetStatus(string text)
    {
        Status = text;
        OnPropertyChanged(nameof(HasStatus));
    }

    private void Post(Action action)
    {
        if (_ui.CheckAccess()) action();
        else _ui.PostNormal(action);
    }
}
