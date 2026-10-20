// Composition root, state slice: the keyed chart serializers, the shared ProjectState,
// the playback/tone coordinators, the keybind registrar and the editor interaction tools.

using DotCharter.Services.Parsing;
using DotCharter.Services.Parsing.Rscp;
using DotCharter.Services.Parsing.Xml;
using DotCharter.UI.Controls.Layers;
using DotCharter.UI.Input;
using DotCharter.UI.Input.Tools;
using Microsoft.Extensions.DependencyInjection;
using DotCharter.Services.Audio.Coordination;
using DotCharter.UI.Platform;

namespace DotCharter.UI;

public partial class App
{
    private static void AddStateServices(IServiceCollection services)
    {
        // Parsing. TRANSIENT, not singleton: a serializer owns a writer which owns
        // an RscpTemplateManager, and four call sites serialize on worker threads
        // that can overlap — the save path, the auto-backup timer, the recovery
        // snapshot store and the checkpoint service. Those types are stateless
        // today, so a shared instance is safe only by accident; the first mutable
        // field added to either would become cross-save corruption between a save
        // and a concurrent auto-backup. Construction is trivial.
        services.AddKeyedTransient<IChartSerializer, RscpChartSerializer>("rscp");
        services.AddKeyedTransient<IChartSerializer, XmlChartSerializer>("xml");

        // State & Orchestration
        services.AddSingleton<ProjectState>();
        services.AddSingleton<AudioPlaybackCoordinator>();
        services.AddSingleton<StemWaveformCoordinator>();
        // RS in-game-tone live switching (0.16 Phase 2): the minimal-diff applier
        // over the DSP chain + the coordinator that drives it from the chart's
        // ToneChanges when AppConfig.UseInGameTones is on. Both resolve their
        // deps (IDspChainService, ProjectState, IConfigurationService, IAppLogger)
        // from DI; the coordinator is pulled into PlayModeViewModel's ctor.
        services.AddSingleton<DotCharter.Tone.Audio.Dsp.RsToneConversion.DcRigApplier>();
        // The coordinator publishes the applied preset to the gameplay HUD through
        // this adapter over the renderer's tone-HUD channel.
        services.AddSingleton<IGameplayToneHud, RenderingGameplayToneHud>();
        // Converted tones are levelled by RENDERED loudness: the meter renders
        // a fixed phrase through each chain on the device-less offline engine
        // (IOfflineRigHost, App.MatchServices) — never the live one — and caches
        // the answer per chain for the session.
        services.AddSingleton<IToneLoudnessMeter>(sp => new RenderedToneLoudnessMeter(
            sp.GetRequiredService<DotCharter.Services.Ai.Match.IOfflineRigHost>(), sp.GetService<IAppLogger>()));
        services.AddSingleton<RsTonePlaybackCoordinator>(sp =>
        {
            var tones = ActivatorUtilities.CreateInstance<RsTonePlaybackCoordinator>(sp);
            // while a song's tone drives seat 0 (the only seat this
            // coordinator drives), the player's tone session saves nothing — the
            // live chain is the song's, and the player's board is in the
            // coordinator's snapshot until it is restored.
            sp.GetRequiredService<DotCharter.Tone.Audio.Dsp.ToneSessionSet>().Primary.BoardDisplaced =
                () => tones.IsPlayersBoardDisplaced;
            return tones;
        });
        services.AddSingleton<AppKeybindRegistrar>();

        // Tools — TRANSIENT, not singleton (0.59): every tool carries mutable
        // per-gesture state (drag targets, marquee origin, pending context
        // menu, double-click latches), and as singletons ONE instance of each
        // was shared across every ToolCoordinator — the main timeline and each
        // stacked row traded drag state through the same object. The factory
        // resolves a fresh IEnumerable<IEditorTool> per manager
        // (TimelineInteractionFactory.CreateManager), and the manager disposes
        // disposable tools (HighwayInteractionTool unsubscribes
        // HistoryChanged) — a transient with an event subscription and no
        // disposer would leak one tool per pane float/redock. Registration
        // ORDER is still the contract (see BuildServices).
        services.AddTransient<IEditorTool, EventInteractionTool>();
        services.AddTransient<IEditorTool, HandShapeInteractionTool>();
        services.AddTransient<IEditorTool, FretAnchorInteractionTool>();
        services.AddTransient<IEditorTool, HighwayInteractionTool>();
        // NOT TimelineInteractionManager: it is created per ChartRenderer by
        // ITimelineInteractionFactory (StackedTracksView keeps one per row),
        // and ChartRenderer is a control that the container never sees — so
        // the singleton registration could only ever have thrown
        // "Unable to resolve service for type ChartRenderer" if anything had
        // resolved it. Dead and misleading; found by AppServiceGraphTests.
        services.AddSingleton<PinnedLabelHitRegistry>();
    }
}
