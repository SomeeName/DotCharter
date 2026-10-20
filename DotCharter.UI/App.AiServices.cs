// Composition root, AI slice (0.55): the tone designer's secret store, provider
// factory and design service. Registrations only — the panel and the Settings tab
// live in ViewModels/ and Views/.

using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.UI;

public partial class App
{
    private static void AddAiServices(IServiceCollection services)
    {
        // API keys, DPAPI-sealed under the config root (ai-secrets.bin, on
        // ConfigBackupRules' credential list). A singleton: it holds a lock and a
        // path, owns no unmanaged resource, and implements neither IDisposable nor
        // IAsyncDisposable — so it cannot be the "async-only singleton" that makes
        // ServiceProvider.Dispose() throw and strands every earlier singleton.
        services.AddSingleton<DotCharter.Tone.Ai.IAiSecretStore>(sp =>
            new DotCharter.Tone.Ai.DpapiAiSecretStore(sp.GetService<IAppLogger>()));

        // Settings + the secret store -> a provider. Stateless, so a singleton;
        // the PROVIDERS it mints are per-request and the design service disposes
        // them, which is what keeps the OpenAI-compatible model's HttpClient from
        // being a transient IDisposable rooted for the life of the process.
        services.AddSingleton<DotCharter.Tone.Ai.IToneDesignModelFactory>(sp =>
            new DotCharter.Tone.Ai.ToneDesignModelFactory(
                sp.GetRequiredService<DotCharter.Tone.Ai.IAiSecretStore>(),
                sp.GetService<IAppLogger>()));

        // Grounding stage 2, the player's own library tones (IToneAnchorLibrary), reads
        // packages: the package reader registers it (App.Plugins.cs), and without
        // one the designer asks GetService and grounds on the catalogue alone.

        // The feature's one entry point. The config is read through a FUNC and
        // never captured, so changing provider under Settings applies to the next
        // request instead of the next launch.
        services.AddSingleton<DotCharter.Tone.Ai.IToneDesignService>(sp =>
            new DotCharter.Tone.Ai.ToneDesignService(
                () => sp.GetRequiredService<IConfigurationService>().LoadAppConfig(),
                sp.GetRequiredService<DotCharter.Tone.Ai.IToneDesignModelFactory>(),
                sp.GetRequiredService<ILocalizationService>(),
                sp.GetService<IAppLogger>(),
                // The same NAM capture root DspTonePresetService uses, so an AI tone
                // goes through DcRigPresetPreparation exactly as a saved one does.
                () => sp.GetService<DotCharter.Tone.Online.Tone3000.INamCaptureLibrary>()?.RootDirectory,
                sp.GetService<DotCharter.Tone.Ai.IToneAnchorLibrary>(),
                // …and the same library again as the CLOSED list a namamp module may
                // choose a capture from (W8). Read per design rather than cached: a
                // TONE3000 download lands in that directory while the panel is open,
                // and a catalogue snapshotted at startup would not offer it until the
                // next launch. One directory listing per design is nothing beside the
                // model round trip it precedes.
                () => DotCharter.Tone.Ai.NamCaptureCatalog.FromLibrary(
                    sp.GetService<DotCharter.Tone.Online.Tone3000.INamCaptureLibrary>()),
                // A capture the library does not have yet: while TONE3000 is signed
                // in, a design may name a search and the app downloads the best match
                // (through the same client, library and size cap as the browser).
                CaptureSourceOf(sp)));

        // The AI chart pass: FHP + fingering, or chord names, over the selection or
        // the whole arrangement — through the SAME provider factory and settings as the tone
        // designer. Stateless apart from its in-flight flag; the provider it mints per run
        // is disposed by the run.
        services.AddSingleton<DotCharter.Services.Ai.Chart.AiChartPassRun>(sp =>
        {
            var editor = sp.GetRequiredService<IChartEditorSurface>();
            return new DotCharter.Services.Ai.Chart.AiChartPassRun(
                sp.GetRequiredService<ProjectState>(),
                sp.GetRequiredService<IHistoryService>(),
                sp.GetService<DotCharter.Tone.Diagnostics.IStatusBarService>(),
                sp.GetRequiredService<ILocalizationService>(),
                sp.GetRequiredService<IAppLogger>(),
                sp.GetRequiredService<IFingeringGenerationService>(),
                sp.GetRequiredService<DotCharter.Tone.Ai.IToneDesignModelFactory>(),
                () => sp.GetRequiredService<IConfigurationService>().LoadAppConfig(),
                () => DotCharter.Services.Ai.Chart.AiChartPassRun.RangeOf(editor.SelectedNotes, editor.SelectedChords),
                editor.RequestRedraw)
            {
                // The sections & phrases job's checks: the SAME report "Ready to ship?" shows.
                Quality = sp.GetRequiredService<DotCharter.Services.Editing.Quality.ChartQualityReporter>(),
                // The Tones job: the W6 generator over the chat's model, its renderer and metric the
                // match lane's (the same confidence scale), and its stems CACHE-ONLY — a chat message never
                // starts a separation. Resolved per run, never on the startup path.
                ToneGenerator = mapper => sp.GetRequiredService<DotCharter.Services.Ai.Match.IStemIsolator>() is
                    DotCharter.Services.Ai.Match.StemIsolator isolator
                    ? new DotCharter.Services.Ai.Generate.RsToneGenerator(
                        isolator.CacheOnly(),
                        mapper,
                        sp.GetRequiredService<ILocalizationService>(),
                        evidence: null,
                        renderer: sp.GetRequiredService<DotCharter.Services.Ai.Match.IRigRenderer>(),
                        similarity: sp.GetRequiredService<DotCharter.Services.Ai.Match.IToneSimilarity>())
                    : null
            };
        });

        // The Tones review's Audition: a proposed body on the rig over its section, through the
        // editor's tone preview and the transport's one-pass audition. Both are reached lazily, on a click.
        services.AddSingleton<ViewModels.Shell.IAiToneAudition>(sp => new ViewModels.Shell.ChartToneAudition(
            () => sp.GetRequiredService<ViewModels.Shell.ToneRigViewModel>().TonePreview,
            (from, to) => sp.GetRequiredService<ViewModels.Editor.TransportViewModel>().AuditionSpan(from, to),
            sp.GetRequiredService<ILocalizationService>()));

        // The AI chat pane's Send: an instruction runs the SAME chart pass as the menu
        // verbs and holds its proposals for the review card. Stateless over the singleton run.
        services.AddSingleton<DotCharter.Services.Ai.Chat.IAiChatChartActions>(sp =>
            new DotCharter.Services.Ai.Chat.AiChatInstructionRunner(
                sp.GetRequiredService<DotCharter.Services.Ai.Chart.AiChartPassRun>(),
                sp.GetRequiredService<ILocalizationService>()));

        // The AI chat pane's per-chart history, driven by the pane: the OPEN
        // chart's chats only, read from ProjectState at the start of every call. A singleton:
        // it holds a gate and no unmanaged resource.
        services.AddSingleton<DotCharter.Services.Ai.Chat.IAiChatHistory>(sp =>
            new DotCharter.Services.Ai.Chat.AiChatHistory(
                new DotCharter.Services.Ai.Chat.ProjectStateAiChatChartSource(sp.GetRequiredService<ProjectState>()),
                TimeProvider.System,
                sp.GetService<IAppLogger>()));
    }

    private static DotCharter.Tone.Ai.INamCaptureSource? CaptureSourceOf(IServiceProvider sp)
    {
        var service = sp.GetService<DotCharter.Tone.Online.Tone3000.ITone3000Service>();
        var auth = sp.GetService<DotCharter.Tone.Online.Tone3000.ITone3000AuthService>();
        var library = sp.GetService<DotCharter.Tone.Online.Tone3000.INamCaptureLibrary>();
        return service is null || auth is null || library is null
            ? null
            : new DotCharter.Tone.Ai.Tone3000CaptureSource(service, auth, library, sp.GetService<IAppLogger>());
    }
}
