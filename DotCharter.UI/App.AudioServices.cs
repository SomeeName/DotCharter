// Composition root, audio slice: the JUCE playback service, the per-player DSP
// rigs and their tone presets/sessions, stem separation with its model/GPU/cache
// stack, the waveform disk cache and the silent transport for audio-less charts.

using DotCharter.Services.Audio.StemSeparation;
using DotCharter.Services.Audio.StemSeparation.Cache;
using DotCharter.Services.Audio.StemSeparation.GuitarRoles;
using DotCharter.Services.Audio.StemSeparation.Onnx;
using DotCharter.Services.Audio.Waveform;
using DotCharter.Tone.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.UI;

public partial class App
{
    /// <summary>
    ///     Set by test fixtures that boot the real app session, to keep real
    ///     audio-device enumeration — and an EXCLUSIVE ASIO grab that outlives
    ///     a hung testhost — out of the run.
    ///     <para>
    ///         <b>Reference this constant; never re-type the string.</b> The
    ///         switch existed and was set by <c>HeadlessSessionFixture</c> for
    ///         several releases while NOTHING called
    ///         <see cref="AppContext.TryGetSwitch" /> for it, so every headless
    ///         test that constructed the app opened the real engine anyway. A
    ///         switch nobody reads looks exactly like a switch that works.
    ///     </para>
    /// </summary>
    public const string UseNullAudioPlaybackServiceSwitch = "DotCharter.UseNullAudioPlaybackService";

    private static void AddAudioServices(IServiceCollection services)
    {
        // JUCE migration (develop-juce): the OwnAudio-backed PlaybackService
        // has been removed. The new native engine (JuceSharp.Native +
        // DotCharter.Audio C# wrapper) is being wired in. Until the JUCE
        // graph is feature-complete we register the NullAudioPlaybackService
        // so the rest of the app boots and we can drive playback through the
        // new engine via JuceSharp.JuceAudioEngine when it is ready.
        services.AddSingleton<IAudioPlaybackService>(sp =>
        {
            if (AppContext.TryGetSwitch(UseNullAudioPlaybackServiceSwitch, out var useNull) && useNull)
                return new NullAudioPlaybackService();

            return new JuceAudioPlaybackService(
                sp.GetRequiredService<ILocalizationService>(),
                sp.GetService<IAppLogger>(),
                sp.GetService<IStatusBarService>(),
                sp.GetService<DotCharter.Tone.Audio.Dsp.IDspChainService>(),
                sp.GetService<DotCharter.Tone.Audio.Dsp.IPlayerDspChains>());
        });
        // The focused seams of that one instance (docs/agents/services/audio-engine.md,
        // "Focused interfaces for view models"). Each FORWARDS to the singleton
        // above, so there is still exactly one engine. Only the seams something
        // resolves are registered — IVstChainHost has no consumer of its own yet.
        AddAudioSeams(services);
        // In-house tone-rig DSP chains (0.16; PER PLAYER since ABI v28). The real
        // JuceDspChainService P/Invokes the native dca_engine_dsp_* /
        // dca_dsp_module_* ABI; those entry points may be absent in a
        // native-less build, so we attempt to load the static module catalogue
        // at construction and fall back to the safe NullDspChainService for
        // every seat if the native surface is missing — a missing-native build
        // still launches and the pedalboard shows its empty state.
        services.AddSingleton<DotCharter.Tone.Audio.Dsp.IPlayerDspChains>(sp =>
        {
            var log = sp.GetService<IAppLogger>();
            var seats = DotCharter.Tone.Audio.Dsp.DspPlayers.MaxPlayers;
            try
            {
                return DotCharter.Tone.Audio.Dsp.PlayerDspChains.CreateNative(seats, log);
            }
            catch (Exception ex)
            {
                log?.Info(AppLogCategory.Audio,
                    $"Tone-rig DSP native surface unavailable ({ex.GetType().Name}); using empty pedalboard.");
                return DotCharter.Tone.Audio.Dsp.PlayerDspChains.CreateNull(seats);
            }
        });
        // Player 1's chain IS the pre-v28 IDspChainService: every existing
        // consumer (pedalboard, RS in-game tones, tone presets, take re-amp)
        // resolves this and keeps driving exactly the chain it always did.
        services.AddSingleton<DotCharter.Tone.Audio.Dsp.IDspChainService>(sp =>
            sp.GetRequiredService<DotCharter.Tone.Audio.Dsp.IPlayerDspChains>().For(0));
        // Built-in factory tone catalogue + user tone-profile store (persisted
        // over AppConfig.UserTonePresets). Applies presets onto the DSP chain
        // above via DcRigApplier; throw-free when the chain is the null service.
        services.AddSingleton<DotCharter.Tone.Audio.Dsp.Presets.IDspTonePresetService>(sp =>
            new DotCharter.Tone.Audio.Dsp.Presets.DspTonePresetService(
                sp.GetRequiredService<DotCharter.Tone.Audio.Dsp.IDspChainService>(),
                sp.GetRequiredService<IConfigurationService>(),
                sp.GetService<IAppLogger>(),
                // Resolve capture-backed presets' bundled .nam file names to
                // absolute paths under the NAM capture library dir at apply time.
                () => sp.GetService<DotCharter.Tone.Online.Tone3000.INamCaptureLibrary>()?.RootDirectory,
                // ABI v28: lets ApplyToPlayer put a DIFFERENT tone on each seat.
                sp.GetService<DotCharter.Tone.Audio.Dsp.IPlayerDspChains>()));
        // Gives the rig a life longer than one engine: restores the player's last
        // board (or the first-run default preset) whenever a live engine attaches,
        // and keeps the live chain written back to AppConfig. Without it the
        // pedalboard starts empty on every launch AND is silently wiped by any
        // engine rebuild (a device change, say). Started in
        // OnFrameworkInitializationCompleted, after the audio service exists.
        // The live rig is PER PLAYER PROFILE (0.45): one tone-rig.json under
        // profiles/<id>/, holding that profile's board for every seat.
        // Profile and seat are different axes — a profile owns a rig per seat —
        // and the store's scope is PINNED so a flush ordered before a switch
        // still lands on the outgoing player. The pre-0.45 global
        // AppConfig.ActiveToneRig is adopted into the active profile once and
        // then cleared.
        services.AddSingleton<DotCharter.Tone.Audio.Dsp.IProfileToneRigStore>(sp =>
            new DotCharter.Tone.Audio.Dsp.ProfileToneRigStore(
                sp.GetRequiredService<DotCharter.Services.Gameplay.Profiles.IPlayerProfileService>(),
                sp.GetRequiredService<IConfigurationService>(),
                sp.GetService<IAppLogger>()));
        // ...once per SEAT since ABI v28, so player 2's board survives an
        // engine rebuild and a quit exactly as player 1's does.
        services.AddSingleton<DotCharter.Tone.Audio.Dsp.ToneSessionSet>(sp =>
            DotCharter.Tone.Audio.Dsp.ToneSessionSet.Create(
                sp.GetRequiredService<DotCharter.Tone.Audio.Dsp.IPlayerDspChains>(),
                sp.GetRequiredService<IConfigurationService>(),
                sp.GetRequiredService<DotCharter.Tone.Audio.Dsp.Presets.IDspTonePresetService>(),
                sp.GetRequiredService<DotCharter.Tone.Audio.Dsp.IProfileToneRigStore>(),
                sp.GetService<IAppLogger>()));
        // Owns StartAll: nothing else starts the tone sessions, so a coordinator
        // nobody resolves fails loudly on the first launch instead of quietly
        // leaving every profile sharing one rig.
        services.AddSingleton<DotCharter.Services.Audio.Dsp.ProfileToneRigCoordinator>(sp =>
            new DotCharter.Services.Audio.Dsp.ProfileToneRigCoordinator(
                sp.GetRequiredService<DotCharter.Services.Gameplay.Profiles.IPlayerProfileService>(),
                sp.GetRequiredService<DotCharter.Tone.Audio.Dsp.IProfileToneRigStore>(),
                sp.GetRequiredService<DotCharter.Tone.Audio.Dsp.ToneSessionSet>(),
                sp.GetService<IAppLogger>()));
        // The chart synth's instrument follows the player profile and a playing
        // song tone's override. Started beside the tone-rig coordinator
        // (App.axaml.cs); the transport's pickers drive it.
        services.AddSingleton<DotCharter.Services.Audio.ChartSynth.ProfileChartSynthInstrumentStore>(sp =>
            new DotCharter.Services.Audio.ChartSynth.ProfileChartSynthInstrumentStore(
                sp.GetRequiredService<DotCharter.Services.Gameplay.Profiles.IPlayerProfileService>()));
        services.AddSingleton<DotCharter.Services.Audio.ChartSynth.ChartSynthInstrumentCoordinator>(sp =>
            new DotCharter.Services.Audio.ChartSynth.ChartSynthInstrumentCoordinator(
                sp.GetRequiredService<IAudioPlaybackService>(),
                sp.GetRequiredService<DotCharter.Services.Gameplay.Profiles.IPlayerProfileService>(),
                sp.GetRequiredService<DotCharter.Services.Audio.ChartSynth.ProfileChartSynthInstrumentStore>(),
                sp.GetRequiredService<IConfigurationService>(),
                DotCharter.Services.Audio.Dsp.Tones.SongToneEditStore.Default,
                () => sp.GetService<DotCharter.Services.Audio.Coordination.RsTonePlaybackCoordinator>(),
                sp.GetService<IAppLogger>()));
        services.AddSingleton<DotCharter.Tone.Audio.Dsp.ToneSessionService>(sp =>
            sp.GetRequiredService<DotCharter.Tone.Audio.Dsp.ToneSessionSet>().Primary);
        services.AddSingleton<StemModelManager>(sp =>
            new StemModelManager(
                sp.GetService<IConfigurationService>(),
                sp.GetService<IAppLogger>(),
                releaseAssets: sp.GetService<DotCharter.Services.Update.IGitHubReleaseAssetDownloader>()));
        // Stem separation runs on DirectML (any DirectX 12 GPU): what the loaded
        // ONNX Runtime offers and which adapters the machine has, read by the
        // separation service AND by Settings > Stem models' provider and GPU rows.
        services.AddSingleton<DotCharter.Services.Audio.StemSeparation.Gpu.IStemGpuEnvironment,
            DotCharter.Services.Audio.StemSeparation.Gpu.OnnxStemGpuEnvironment>();
        // The CUDA runtime earlier versions downloaded (~1.4 GB) is dead weight now;
        // Settings > Stem models offers to remove it while it is still on disk.
        services.AddSingleton(sp =>
            new DotCharter.Services.Audio.StemSeparation.Gpu.LegacyGpuRuntimeFolder(
                sp.GetRequiredService<StemModelManager>().ModelsDirectory,
                sp.GetService<IAppLogger>()));
        // The one-click "download the default stem model?" step in front of the first
        // separation of a user who never chose a model: never a silent 353 MB.
        services.AddSingleton<IStemDefaultModelProvisioner>(sp =>
            new StemDefaultModelProvisioner(
                sp.GetRequiredService<StemModelManager>(),
                sp.GetService<DotCharter.Services.Audio.StemSeparation.Gpu.IStemGpuEnvironment>(),
                sp.GetService<IAppLogger>()));
        services.AddSingleton<InFlightStemSeparationTracker>();
        services.AddSingleton<IStemSeparationService>(sp =>
        {
            var log = sp.GetService<IAppLogger>();
            var mgr = sp.GetService<StemModelManager>();
            var tracker = sp.GetRequiredService<InFlightStemSeparationTracker>();
            var gpu = sp.GetService<DotCharter.Services.Audio.StemSeparation.Gpu.IStemGpuEnvironment>();
            var onnx = new OnnxStemSeparationService(mgr, log, ffmpegResolver: null, gpu: gpu,
                configuredFfmpegPath: () => sp.GetService<IConfigurationService>()?.LoadAppConfig().FfmpegPath);
            var native = new JuceStemSeparationService(log, mgr);
            // Order: managed ONNX Runtime FIRST (GPU via DirectML when an
            // *.onnx model is the active model — e.g. htdemucs-6s-onnx), then
            // bundled native demucs.cpp (CPU) as a fallback when no ONNX model
            // is selected. These two are the whole pipeline: the Python-backed
            // JuceSharp orchestrator was never in it and was deleted.
            var ordered = new IStemSeparationService[] { onnx, native };
            return new CompositeStemSeparationService(ordered, log, tracker);
        });
        // Durable, per-song stem cache: separation runs once and is packaged as
        // one compressed .dcstems archive under %LocalAppData%/DotCharter/StemCache,
        // keyed by song identity + audio content hash, so the long separation
        // process never repeats for the same song. Stems are Opus when ffmpeg is
        // available, else WAV (recorded in the manifest so the cache is portable).
        services.AddSingleton<StemCacheStore>(sp =>
        {
            var log = sp.GetService<IAppLogger>();
            return new StemCacheStore(
                new FfmpegStemEncoder(log,
                    configuredFfmpegPath: () => sp.GetService<IConfigurationService>()?.LoadAppConfig().FfmpegPath),
                log);
        });
        services.AddSingleton<StemCacheCoordinator>(sp =>
            new StemCacheCoordinator(
                sp.GetRequiredService<StemCacheStore>(),
                sp.GetService<IAppLogger>()));
        // Guitar roles: Lead (centre) / Rhythm (sides) derived from the guitar
        // stem by an offline stereo-position split, kept beside it. Never a second
        // separation. Driven by StemMixerViewModel.GuitarRoles.
        services.AddSingleton<IGuitarRoleStems>(sp =>
            new GuitarRoleStemService(sp.GetService<IAppLogger>()));
        // 0.14 P4: generate-once waveform pyramid, keyed like the stem cache
        // (song identity + audio signature) under %LocalAppData%/DotCharter/WaveformCache.
        services.AddSingleton<WaveformDiskCache>(sp =>
            new WaveformDiskCache(sp.GetService<IAppLogger>()));
        // Silent transport for audio-less charts: a cached silent WAV of the
        // project length so the playhead / scrubbing / count-in / Play Mode work
        // when a chart has no (resolvable) audio. Never fed to stem separation.
        services.AddSingleton<DotCharter.Services.Audio.SilentTransport.SilentAudioTrack>(sp =>
            new DotCharter.Services.Audio.SilentTransport.SilentAudioTrack(sp.GetService<IAppLogger>()));
    }

    /// <summary>
    ///     The focused seams of <see cref="IAudioPlaybackService" />, each a
    ///     forwarding registration onto the one singleton. Forwarding, not a
    ///     second <c>AddSingleton&lt;T, JuceAudioPlaybackService&gt;</c>: that
    ///     would build a second engine. The container captures the same instance
    ///     once per seam it resolves, so it may call <c>Dispose</c> on it more than
    ///     once — harmless, <c>JuceAudioPlaybackService.Dispose</c> is idempotent,
    ///     and the clean-shutdown path disposes the audio service explicitly
    ///     before the container (<see cref="RunCleanShutdown" />).
    ///     <para>
    ///         Pinned by <c>UI/AudioSeamRegistrationTests</c>: every seam here
    ///         resolves to the same object as <see cref="IAudioPlaybackService" />.
    ///     </para>
    /// </summary>
    internal static void AddAudioSeams(IServiceCollection services)
    {
        services.AddSingleton<IPlaybackTransport>(sp => sp.GetRequiredService<IAudioPlaybackService>());
        services.AddSingleton<IAudioDeviceControl>(sp => sp.GetRequiredService<IAudioPlaybackService>());
        services.AddSingleton<IStemMixer>(sp => sp.GetRequiredService<IAudioPlaybackService>());
        services.AddSingleton<ILiveInputCapture>(sp => sp.GetRequiredService<IAudioPlaybackService>());
    }
}
