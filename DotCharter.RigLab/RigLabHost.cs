using DotCharter.Localization;
using DotCharter.Tone.Ai;
using DotCharter.Tone.Audio;
using DotCharter.Tone.Audio.Abstractions;
using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.Audio.Dsp.Presets;
using DotCharter.Tone.Audio.Reamp;
using DotCharter.Tone.Diagnostics;
using DotCharter.Tone.Infrastructure.Configuration;
using DotCharter.Tone.Infrastructure.Shell;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.Tone.Logging;
using DotCharter.Tone.Online.Tone3000;
using DotCharter.Tone.UI.Platform;
using DotCharter.Tone.UI.ViewModels.PlayMode;
using DotCharter.Tone.UI.ViewModels.Settings;

namespace DotCharter.RigLab;

/// <summary>
///     RigLab's composition root: the audio engine, the per-seat DSP rig, the
///     tone presets and the live-rig store, TONE3000 and the NAM capture
///     library, and the rig's view models — the same objects DotCharter's
///     <c>App.AudioServices</c> / <c>App.OnlineServices</c> build for the rig, and
///     nothing else. Built by hand rather than through a container: there are a
///     dozen of them, and the disposal order at the bottom is the part that
///     matters.
/// </summary>
public sealed class RigLabHost : IDisposable
{
    private readonly object _startGate = new();
    private Task? _started;
    private bool _disposed;

    /// <param name="config">riglab.json; the host reads and writes the rig's
    /// settings through it.</param>
    /// <param name="ui">Where engine callbacks are marshalled to.</param>
    /// <param name="useNullAudio">Build everything but open no audio device
    /// (the headless tests).</param>
    /// <param name="namLibraryRoot">Where downloaded and bundled NAM captures
    /// live; null is the shared per-user library DotCharter also uses.</param>
    public RigLabHost(RigLabConfigService config, IUiDispatcher ui, bool useNullAudio = false,
        string? namLibraryRoot = null)
    {
        Config = config;
        Ui = ui;
        Logger = new AppLogger(config);
        StatusBar = new StatusBarService(ui, Logger);

        Chains = useNullAudio
            ? PlayerDspChains.CreateNull(DspPlayers.MaxPlayers)
            : CreateChains(Logger);
        Chain = Chains.For(0);

        TokenStore = new DpapiTone3000TokenStore(Logger);
        Tone3000Auth = new Tone3000AuthService(config.Load, TokenStore, Logger);
        Tone3000 = new Tone3000Client(ct => Tone3000Auth.GetAccessTokenAsync(ct), Logger);
        NamLibrary = new NamCaptureLibrary(Tone3000, Logger, namLibraryRoot);

        Presets = new DspTonePresetService(Chain, config, Logger, () => NamLibrary.RootDirectory, Chains);
        // No player profiles here: ONE live rig per seat, next to riglab.json.
        RigStore = new ProfileToneRigStore(WritablePaths.ConfigRoot, legacyConfig: null, Logger);
        Sessions = ToneSessionSet.Create(Chains, config, Presets, RigStore, Logger);

        Audio = useNullAudio
            ? new NullAudioPlaybackService()
            : new JuceAudioPlaybackService(LocalizationService.Instance, Logger, StatusBar, Chain, Chains);

        Files = new DesktopFileStorageService();
        Shell = new DesktopShellLauncher();
        Clipboard = new AvaloniaSystemClipboard();

        // "Design a tone": the same designer DotCharter runs, minus what needs
        // the charting stack (library anchors from psarcs, match from audio).
        // Keys are sealed next to riglab.json; a design may fetch its amp
        // capture from TONE3000 while the player is signed in.
        AiSecrets = new DpapiAiSecretStore(Logger);
        AiModels = new ToneDesignModelFactory(AiSecrets, Logger);
        Design = new ToneDesignService(
            () => config.Load(),
            AiModels,
            LocalizationService.Instance,
            Logger,
            () => NamLibrary.RootDirectory,
            anchors: null,
            captures: () => NamCaptureCatalog.FromLibrary(NamLibrary),
            captureSource: new Tone3000CaptureSource(Tone3000, Tone3000Auth, NamLibrary, Logger));

        Rig = new PedalboardViewModel(
            Chain, Presets, LocalizationService.Instance, config, Tone3000, Tone3000Auth, NamLibrary, Audio, Files, Chains, Shell,
            dispatcher: ui,
            subscribeProfileSwitch: null,
            toneDesignPanel: seams => new ToneDesignCoreViewModel(
                () => Design, seams.Apply, seams.SnapshotLive, seams.SaveUser, seams.Localization, seams.ModuleName,
                post: seams.Post, logger: seams.Logger,
                // "Copy prompt" for the ChatGPT copy & paste route — the same
                // clipboard adapter DotCharter registers.
                clipboard: Clipboard)
            {
                // The panel's "Open settings" goes to the AI tab; the window sets it.
                OpenSettings = () => OpenAiSettings?.Invoke()
            });
        InputChannel = new LiveInputChannelViewModel(Audio, config, LocalizationService.Instance, ui);
        // DI recorder + re-amp (ABI v37). Takes live in %APPDATA%\DotCharter RigLab\DI Takes
        // (WritablePaths follows the renamed app folder). The null transport has no
        // engine seam, so the panel shows itself disabled with its reason.
        Reamp = new RigReampViewModel(Audio as IReampEngine, new DiTakeLibrary(), LocalizationService.Instance, Shell, Files, Logger);
        Screen = new AmpFxRigViewModel(Rig, NamLibrary, InputChannel)
        {
            DropTune = DropTuneOver(Audio, config),
            // The window is the only host; closing it is the window's ✕.
            ShowCloseButton = false,
            InputPeakProvider = new InputPeakMeter(Audio).Read,
            CallbackLoadProvider = RigCpuLoadViewModel.Over(Audio),
            Reamp = Reamp
        };
        Settings = new AudioSettingsViewModel(Audio, config, ui, InputChannel, LocalizationService.Instance);
        Plugins = new VstRackViewModel(Audio, config, ui, Logger);
        Ai = new AiSettingsViewModel(config, AiSecrets, AiModels, Design, Tone3000Auth, ui, LocalizationService.Instance, Logger);

        // The bar's engine line and the jack list follow the device: a lost or
        // reconnected interface re-reads both.
        Audio.DeviceLost += OnDeviceChanged;
        Audio.DeviceReconnected += OnDeviceChanged;
    }

    private void OnDeviceChanged(object? sender, EventArgs e) => Ui.Post(RefreshDeviceState);

    private void RefreshDeviceState()
    {
        Settings.RefreshStatus();
        InputChannel.Refresh();
        Reamp.RefreshAvailability();
    }

    public RigLabConfigService Config { get; }
    public IUiDispatcher Ui { get; }
    public AppLogger Logger { get; }
    public StatusBarService StatusBar { get; }
    public IPlayerDspChains Chains { get; }
    public IDspChainService Chain { get; }
    public ITone3000TokenStore TokenStore { get; }
    public ITone3000AuthService Tone3000Auth { get; }
    public ITone3000Service Tone3000 { get; }
    public INamCaptureLibrary NamLibrary { get; }
    public IDspTonePresetService Presets { get; }
    public IProfileToneRigStore RigStore { get; }
    public ToneSessionSet Sessions { get; }
    public IAudioPlaybackService Audio { get; }
    public IFileStorageService Files { get; }
    public IShellLauncher Shell { get; }
    public ISystemClipboard Clipboard { get; }
    public PedalboardViewModel Rig { get; }
    public LiveInputChannelViewModel InputChannel { get; }
    public RigReampViewModel Reamp { get; }
    public AmpFxRigViewModel Screen { get; }
    public AudioSettingsViewModel Settings { get; }
    public VstRackViewModel Plugins { get; }
    public IAiSecretStore AiSecrets { get; }
    public IToneDesignModelFactory AiModels { get; }
    public IToneDesignService Design { get; }
    public AiSettingsViewModel Ai { get; }

    /// <summary>Shows the AI tab — set by whoever owns the window, so the design
    /// panel's "Open settings" has somewhere to go.</summary>
    public Action? OpenAiSettings { get; set; }

    /// <summary>
    ///     Brings the rig up, OFF the UI thread and in this order: open the audio
    ///     device, install the bundled NAM captures, restore the last rig, then the
    ///     VST chain. The captures go before the rig for the reason DotCharter's
    ///     startup gives: a capture-backed preset names its .nam by file name, and
    ///     on a fresh install that file only exists once the installer copied it —
    ///     a rig restored first would load a modelless amp, i.e. silence.
    /// </summary>
    public Task StartAsync()
    {
        lock (_startGate)
        {
            return _started ??= Task.Run(StartCore);
        }
    }

    private async Task StartCore()
    {
        try
        {
            await Audio.InitializeAsync().ConfigureAwait(false);
            Audio.ApplyAudioConfiguration(Config.Load());
        }
        catch (Exception ex)
        {
            Logger.Warning(AppLogCategory.Audio, "RigLab: the audio engine did not start.", ex);
        }
        Ui.Post(RefreshDeviceState);

        try
        {
            var repaired = NamLibrary.MigrateFileExtensions();
            if (repaired > 0)
                Logger.Info(AppLogCategory.Application, $"Repaired {repaired} NAM capture file extension(s).");
            BundledNamCaptureInstaller.InstallFromDirectory(
                NamLibrary, Path.Combine(AppContext.BaseDirectory, "NamCaptures"), Logger);
        }
        catch (Exception ex)
        {
            Logger.Warning(AppLogCategory.Application, "RigLab: bundled NAM capture install failed.", ex);
        }

        try { Sessions.StartAll(); }
        catch (Exception ex) { Logger.Warning(AppLogCategory.Audio, "RigLab: the tone sessions failed to start.", ex); }

        try { await Plugins.RestoreAsync().ConfigureAwait(false); }
        catch (Exception ex) { Logger.Warning(AppLogCategory.Audio, "RigLab: the VST chain failed to restore.", ex); }

        // A configured MIDI controller comes up with the rig, not when somebody
        // first opens a unit's details: program changes and the scene CC must work
        // from the first song. (DotCharter does the same after its first paint.)
        if (!string.IsNullOrWhiteSpace(Config.Load().RigMidiInputDevice))
        {
            Ui.Post(() =>
            {
                try { _ = Rig.MidiControls; }
                catch (Exception ex) { Logger.Warning(AppLogCategory.Audio, "RigLab: the MIDI controller failed to start.", ex); }
            });
        }
    }

    /// <summary>
    ///     Shutdown, in the order the parts depend on each other: write the live
    ///     rig (the last few seconds of knob-twiddling live only in the chain),
    ///     drop the view models' engine subscriptions, close the audio device —
    ///     which detaches the DSP chains before the engine goes — then flush the
    ///     settings and close the log.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Audio.DeviceLost -= OnDeviceChanged;
        Audio.DeviceReconnected -= OnDeviceChanged;
        Try(() => Sessions.FlushAll());
        Try(() => Ai.Dispose());
        Try(() => Plugins.Dispose());
        Try(() => Reamp.Dispose());
        Try(() => Rig.Dispose());
        Try(() => Sessions.Dispose());
        Try(() => Audio.Dispose());
        Try(() => StatusBar.Dispose());
        Try(() => Config.Dispose());
        Try(() => Logger.Dispose());
    }

    private void Try(Action step)
    {
        try { step(); }
        catch (Exception ex)
        {
            // The logger may be the thing that failed; stderr still reaches a console.
            Console.Error.WriteLine($"RigLab shutdown step failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>The footer's drop-tune pedal over the engine's input shifter
    /// (seat 0), remembered in riglab.json and applied at start — RigLab has no
    /// transport to own it, so this is the one writer.</summary>
    internal static RigDropTuneViewModel DropTuneOver(IAudioPlaybackService audio, IRigConfigService config)
    {
        var saved = Math.Clamp(config.LoadRigConfig().InputPitchShiftSemis, -RigDropTuneViewModel.MaxSemitones, 0);
        if (saved != 0) audio.InputPitchShiftSemitones = saved;
        return new RigDropTuneViewModel(
            () => (int)Math.Round(audio.InputPitchShiftSemitones),
            value =>
            {
                audio.InputPitchShiftSemitones = value;
                config.UpdateRigConfig(c => c.InputPitchShiftSemis = value);
            });
    }

    private static IPlayerDspChains CreateChains(IAppLogger logger)
    {
        try
        {
            return PlayerDspChains.CreateNative(DspPlayers.MaxPlayers, logger);
        }
        catch (Exception ex)
        {
            // A build without the native DSP surface still opens, with an empty rig.
            logger.Info(AppLogCategory.Audio,
                $"Tone-rig DSP native surface unavailable ({ex.GetType().Name}); using an empty rig.");
            return PlayerDspChains.CreateNull(DspPlayers.MaxPlayers);
        }
    }
}
