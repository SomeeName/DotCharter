// Composition root, tone-match slice (0.56): "match a tone from audio" end to end —
// the content-invariant feature extractor, the distance every lane scores against,
// the offline rig renderer, the stem isolator, the rig sampler, the retrieval
// library, the matcher and the optional trained estimator. Registrations only; the
// panel lives in ViewModels/PlayMode/ToneDesignViewModel.Match.cs and the host that
// hands it a song is PlayModeToneRigViewModel.cs.
//
// Since the match-by-section wave (0.57) it also holds the PRIOR lane at the end:
// the language model's guess at a section's rig (IToneMatchPriorSource) and the
// narrator that puts the matched rig back into words (IToneNarrator).

using DotCharter.Services.Ai.Match;
using DotCharter.Services.Ai.Match.Model;
using DotCharter.Services.Ai.Match.Prior;
using DotCharter.Services.Ai.Match.Search;
using DotCharter.Services.Ai.Match.Sections;
using DotCharter.Services.Ai.Match.Similarity;
using DotCharter.Services.Audio.StemSeparation;
using DotCharter.Services.Audio.StemSeparation.Cache;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.UI;

public partial class App
{
    private static void AddMatchServices(IServiceCollection services)
    {
        // Stateless and pure: one vector in, one vector out, no scratch that outlives
        // a call. A singleton because every comparison in the app must go through the
        // SAME extractor — two vectors from different extractors are not comparable
        // and ToneSimilarity.Distance refuses them outright.
        services.AddSingleton<DotCharter.Services.Ai.Match.IToneFeatureExtractor>(
            _ => new ToneFeatureExtractor());

        // The metric. Takes the extractor rather than minting its own, so the rule
        // "the same extractor on both sides of every comparison" is enforced by the
        // container instead of by everyone remembering it.
        services.AddSingleton<DotCharter.Services.Ai.Match.IToneSimilarity>(sp =>
            new ToneSimilarity(sp.GetRequiredService<DotCharter.Services.Ai.Match.IToneFeatureExtractor>()));

        // The device-less engine behind the renderer. A singleton because it owns a
        // JuceAudioEngine: one engine, one chain, one seat, and its own lock
        // serialises concurrent renders. It is IDisposable and NOT IAsyncDisposable —
        // the shape that makes ServiceProvider.Dispose() throw and strand every
        // singleton created before it.
        services.AddSingleton<IOfflineRigHost>(sp => new NullDeviceRigHost(sp.GetService<IAppLogger>()));

        // Dry DI + rig -> wet audio, deterministic and level-matched.
        services.AddSingleton<DotCharter.Services.Ai.Match.IRigRenderer>(sp =>
            new RigRenderer(sp.GetRequiredService<IOfflineRigHost>()));

        // The guitar out of a record. It holds the stem cache store, whose whole
        // point is that a separation happens once per song, ever — so it must be a
        // singleton and must never be handed a per-call store.
        services.AddSingleton<DotCharter.Services.Ai.Match.IStemIsolator>(sp =>
            new StemIsolator(
                sp.GetRequiredService<IStemSeparationService>(),
                new StemCacheStore(logger: sp.GetService<IAppLogger>()),
                sp.GetRequiredService<ILocalizationService>(),
                sp.GetService<IAppLogger>()));

        // A file on disk -> MonoAudio. One implementation, because the UI lane wrote
        // its own RIFF parser while this one was calling WavIo, and a second RIFF
        // parser is a second set of format bugs (the UI's cannot read the float32
        // WAVs the stem cache writes).
        services.AddSingleton<IAudioClipLoader>(_ => new AudioClipLoader());

        // Draws realistic rigs. Stateless and deterministic for a seed: the caller
        // brings the Random, so a singleton is shared safely as long as nobody shares
        // the Random, which nobody does.
        services.AddSingleton<DotCharter.Services.Ai.Match.IRigSampler>(_ => new RigSampler());

        // The retrieval library: thousands of sampled rigs rendered ONCE over the
        // fixed 20-second reference-DI medley and embedded. A singleton because it
        // builds or loads that library exactly once per process and then answers in
        // milliseconds — and because the medley is what every stored vector was made
        // over, it comes from ReferenceDi rather than from a file a user could swap.
        //
        // It never renders more than RigLibraryOptions.FirstMatchCount
        // (96) rows in front of a match: the SHIPPED library (rig-library-v1.zip,
        // installed by the same downloader as the model — `installedLibrary` is
        // re-read on every match) is used whole when its DI fingerprint matches,
        // and otherwise the local cache is grown small-first and then on a
        // BelowNormal thread of its own. The provider is IDisposable (and NOT
        // IAsyncDisposable) so the container stops that thread on a clean exit.
        services.AddSingleton<IRigLibraryProvider>(sp => new RigLibraryProvider(
            _ => Task.FromResult(ReferenceDi.Medley()),
            sp.GetRequiredService<DotCharter.Services.Ai.Match.IRigSampler>(),
            sp.GetRequiredService<DotCharter.Services.Ai.Match.IRigRenderer>(),
            sp.GetRequiredService<DotCharter.Services.Ai.Match.IToneSimilarity>().Extractor,
            installedLibrary: () => sp
                .GetRequiredService<DotCharter.Services.Ai.Match.Model.IRigEstimatorModelDownloadService>()
                .LibraryPath,
            logger: sp.GetService<IAppLogger>()));

        // How a real user GETS that model. Published as a release asset
        // (rig-estimator-v1.zip) rather than as zip payload: ~200 MB of weights
        // that change far more rarely than the app, so bundling it would make
        // every update re-download bytes the machine already has — the same
        // arrangement, and the same asset fetcher, as the lyric acoustic models.
        //
        // Until this existed nothing outside RigEstimatorOnnx and the python
        // tools named `ai-match\rig-estimator` at all: IsAvailable was
        // permanently false for every user who had not run export_onnx.py by
        // hand, and the matcher silently stayed search-only.
        services.AddSingleton<DotCharter.Services.Ai.Match.Model.IRigEstimatorModelDownloadService>(sp =>
            new RigEstimatorModelDownloadService(
                new DotCharter.Services.Audio.Lyrics.LyricModelAssetFetcher(
                    () => sp.GetRequiredService<DotCharter.Services.Update.IGitHubAuthService>().GetToken(),
                    sp.GetService<IAppLogger>()),
                sp.GetRequiredService<ILocalizationService>(),
                sp.GetService<IAppLogger>()));

        // The trained estimator, used only as a PRIOR for the matcher. It reports
        // IsAvailable=false when no model file is on disk, which is the state of every
        // machine that has not downloaded one — so registering it is not the same as
        // depending on it.
        //
        // The directory comes from the INSTALLER rather than from the
        // estimator's own default, so "where it is written" and "where it is
        // read" cannot drift into two paths. They are equal today and
        // RigEstimatorModelInstallerTests asserts it.
        services.AddSingleton<DotCharter.Services.Ai.Match.IRigEstimator>(sp =>
            new RigEstimatorOnnx(
                modelDirectory: sp
                    .GetRequiredService<DotCharter.Services.Ai.Match.Model.IRigEstimatorModelDownloadService>()
                    .ModelDirectory,
                log: message => sp.GetService<IAppLogger>()?.Info(AppLogCategory.Audio, message)));

        // Where a finished match leaves its FEATURE VECTORS and its per-block
        // breakdown (W5b): one bounded JSON per run under
        // %LOCALAPPDATA%/DotCharter/ai-match/results, beside the retrieval library and
        // the estimator model. A score nobody can inspect is a score nobody can argue
        // with, and the alternative to keeping the vectors is re-running a multi-minute
        // search against a library snapshot that has since grown.
        services.AddSingleton<IMatchResultStore>(_ => new MatchResultStore());

        // The feature's one entry point. Retrieval over the library, then CMA-ES over
        // the real renderer.
        services.AddSingleton<DotCharter.Services.Ai.Match.IToneMatcher>(sp => new ToneMatcher(
            sp.GetRequiredService<DotCharter.Services.Ai.Match.IRigRenderer>(),
            sp.GetRequiredService<DotCharter.Services.Ai.Match.IToneSimilarity>(),
            sp.GetRequiredService<IRigLibraryProvider>(),
            sp.GetRequiredService<ILocalizationService>(),
            sp.GetRequiredService<DotCharter.Services.Ai.Match.IRigEstimator>(),
            options: null,
            artifacts: sp.GetRequiredService<IMatchResultStore>()));

        // ---- generate lane (W6) ----------------------------------------------
        // One Rocksmith in-game tone per placed tone change. The MODEL boundary is
        // IRsToneMapper: features + context in, a catalogue-valid RsTone out. It
        // resolves the tone-design provider PER CALL (AiProviderResolution) so a
        // provider change in Settings takes effect without rebuilding the container,
        // and so a machine with no provider configured fails with that sentence
        // rather than at construction.
        services.AddSingleton<DotCharter.Services.Ai.Generate.IRsToneMapper>(sp =>
            new DotCharter.Services.Ai.Generate.LanguageModelRsToneMapper(
                () => DotCharter.Tone.Ai.AiProviderResolution.Create(
                    sp.GetRequiredService<DotCharter.Tone.Ai.IToneDesignModelFactory>(),
                    () => sp.GetRequiredService<IConfigurationService>().LoadAppConfig(),
                    out _),
                DescribeToneProvider(sp)));

        // The lane's entry point. The renderer + similarity are the MATCH lane's, so
        // a generated tone's confidence is the same W5b scale a matched one reports
        // and means the same thing.
        services.AddSingleton<DotCharter.Services.Ai.Generate.IRsToneGenerator>(sp =>
            new DotCharter.Services.Ai.Generate.RsToneGenerator(
                sp.GetRequiredService<DotCharter.Services.Ai.Match.IStemIsolator>(),
                sp.GetRequiredService<DotCharter.Services.Ai.Generate.IRsToneMapper>(),
                sp.GetRequiredService<ILocalizationService>(),
                evidence: null,
                renderer: sp.GetRequiredService<DotCharter.Services.Ai.Match.IRigRenderer>(),
                similarity: sp.GetRequiredService<DotCharter.Services.Ai.Match.IToneSimilarity>()));

        // ---- prior lane ------------------------------------------------------
        // Knowledge before measurement: the language model guesses the rig, the
        // matcher starts from that guess, the model explains the result.

        // The guess. A thin adapter over the DESIGN service — same provider, same
        // catalogue-grounded prompt, same validator — so a prior is exactly a tone
        // the designer could have produced, and nothing here has a second copy of
        // the module rules. It answers null (never throws) when no provider is
        // configured, which is what keeps the pure-audio match working on a machine
        // that has never opened Settings → AI.
        services.AddSingleton<DotCharter.Services.Ai.Match.IToneMatchPriorSource>(sp =>
            new ToneDesignPriorSource(
                sp.GetRequiredService<DotCharter.Tone.Ai.IToneDesignService>(),
                sp.GetRequiredService<ILocalizationService>(),
                () => DotCharter.Tone.Ai.AiProviderResolution.SettingsOf(
                    () => sp.GetRequiredService<IConfigurationService>().LoadAppConfig()),
                sp.GetService<IAppLogger>()));

        // The explanation. It does NOT go through IToneDesignService: that service's
        // system prompt is the whole module catalogue and four few-shots, which is
        // grounding for EMITTING a chain and pure cost for describing one. Own small
        // prompt, own two-field schema, same provider resolution.
        services.AddSingleton<DotCharter.Services.Ai.Match.IToneNarrator>(sp => new ToneNarrator(
            () => sp.GetRequiredService<IConfigurationService>().LoadAppConfig(),
            sp.GetRequiredService<DotCharter.Tone.Ai.IToneDesignModelFactory>(),
            sp.GetService<IAppLogger>(),
            // The narration is shown next to the panel's own strings, so it is
            // written in the language the panel is in — the CONFIGURED culture, not
            // the thread's.
            () => sp.GetRequiredService<ILocalizationService>().Culture));

        // sections lane — "match by section": one rig per part of a song,
        // so tone-from-audio works on a song that is not in the library.

        // Where the parts ARE. It takes the similarity rather than an extractor of its
        // own, so the boundaries it finds are by construction places the matcher can
        // hear a difference — a segmenter with a private metric would split a song
        // where the matcher cannot, and hand the user two rigs that are one rig.
        services.AddSingleton<DotCharter.Services.Ai.Match.IToneSectionProposer>(sp => new ToneSectionProposer(
            sp.GetRequiredService<DotCharter.Services.Ai.Match.IStemIsolator>(),
            sp.GetRequiredService<DotCharter.Services.Ai.Match.IToneSimilarity>(),
            sp.GetRequiredService<ILocalizationService>()));

        // The parts MATCHED, sequentially — one device-less engine, one section at a
        // time. The prior source is the prior lane's and is OPTIONAL: GetService, not
        // GetRequiredService, so a build without that lane still matches from audio
        // alone instead of failing to construct the container.
        services.AddSingleton<DotCharter.Services.Ai.Match.IToneSetMatcher>(sp => new ToneSetMatcher(
            sp.GetRequiredService<DotCharter.Services.Ai.Match.IStemIsolator>(),
            sp.GetRequiredService<DotCharter.Services.Ai.Match.IToneMatcher>(),
            sp.GetRequiredService<ILocalizationService>(),
            sp.GetService<DotCharter.Services.Ai.Match.IToneMatchPriorSource>()));
    }

    /// <summary>One line naming the configured provider and model, for a generated
    /// tone's provenance and the run report. Resolved lazily and defensively: a
    /// machine with nothing configured still gets a description rather than an
    /// exception at container build.</summary>
    private static string DescribeToneProvider(IServiceProvider sp)
    {
        try
        {
            var settings = DotCharter.Tone.Ai.AiProviderResolution.SettingsOf(
                () => sp.GetRequiredService<IConfigurationService>().LoadAppConfig());
            return $"{settings.Kind}:{settings.ModelId}";
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            return "ai:unconfigured";
        }
    }
}
