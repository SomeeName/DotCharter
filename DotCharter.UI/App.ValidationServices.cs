// Composition root, validation slice: the arrangement validation service with EVERY
// IArrangementRule (registration order is what DI injects as one IEnumerable), the
// auto-fix service, the batch + audio chart lints, and the lyric parsing/auto-sync stack.
// A static class beside App, not an App partial (SplitTypePartialRatchetTests).

using DotCharter.Services.Editing.Fixing;
using DotCharter.Services.Parsing;
using DotCharter.Services.Parsing.Lrc;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.UI;

internal static class AppValidationServices
{
    internal static void AddValidationServices(IServiceCollection services)
    {
        services.AddSingleton<IArrangementValidationService, ArrangementValidationService>();
        // Keep the rules together. DI injects them in registration order.
        services.AddSingleton<IArrangementRule, PhraseRule>();
        services.AddSingleton<IArrangementRule, NoteRule>();
        services.AddSingleton<IArrangementRule, ChordRule>();
        services.AddSingleton<IArrangementRule, SongEndAudioRule>();
        services.AddSingleton<IArrangementRule, AnchorRule>();
        services.AddSingleton<IArrangementRule, HandShapeRule>();
        services.AddSingleton<IArrangementRule, LinkNextRule>();
        services.AddSingleton<IArrangementRule, CapoAnchorRangeRule>();
        services.AddSingleton<IArrangementRule, SlideTechniqueRule>();
        services.AddSingleton<IArrangementRule, TemplateAndOverlapRule>();
        services.AddSingleton<IArrangementRule, ToneSilenceAndPhraseRule>();
        services.AddSingleton<IArrangementRule, IndexFingerAnchorRule>();
        services.AddSingleton<IArrangementRule, HandShapeContentRule>();
        services.AddSingleton<IArrangementRule, PhraseAnchorRule>();
        services.AddSingleton<IArrangementRule, OpenNoteBendRule>();
        services.AddSingleton<IArrangementRule, ChordOutsideHandShapeRule>();
        services.AddSingleton<IArrangementRule, ShowlightRule>();
        services.AddSingleton<IArrangementRule, DifficultyLevelRule>();
        services.AddSingleton<IArrangementRule, HandShapeGapRule>();
        services.AddSingleton<IArrangementRule, SectionNameVarietyRule>();
        services.AddSingleton<IArrangementRule, BoundaryCrossingRule>();
        services.AddSingleton<IArrangementRule, ToneConventionRule>();
        services.AddSingleton<IArrangementRule, CustomTuningRule>();
        services.AddSingleton<IArrangementRule, PhraseStructureRule>();
        services.AddSingleton<IArrangementRule, FretHandPositionRule>();
        services.AddSingleton<IArrangementRule, OffGridRule>();
        services.AddSingleton<IArrangementRule, LeadInRule>();
        services.AddSingleton<IArrangementRule, TechniqueCombinationRule>();
        services.AddSingleton<IArrangementRule, TempoMapRule>();
        services.AddSingleton<IArrangementRule, SustainSpacingRule>();
        services.AddSingleton<IArrangementRule, AudioCheckRule>();
        services.AddSingleton<IArrangementAutoFixService, ArrangementAutoFixService>();
        // Batch library lint over a folder of projects; IChartSerializer is only registered keyed.
        services.AddSingleton<DotCharter.Services.Editing.Linting.IBatchChartLintService>(sp =>
            new DotCharter.Services.Editing.Linting.BatchChartLintService(
                sp.GetRequiredKeyedService<IChartSerializer>("rscp"),
                sp.GetRequiredService<IArrangementValidationService>(),
                sp.GetRequiredService<IArrangementAutoFixService>(),
                sp.GetRequiredService<DotCharter.Services.Export.ISaveCompanionService>()));
        // v0.29 audio-verified lint: offline CQT over the ORIGINAL MIX (never
        // Opus stems — bright-line rule) vs. the chart's sounding pitches.
        services.AddSingleton<DotCharter.Services.Editing.Linting.IAudioChartLintService>(sp =>
            new DotCharter.Services.Editing.Linting.AudioChartLintService(
                sp.GetService<IAppLogger>()));
        services.AddSingleton<DotCharter.Services.Editing.Quality.ChartQualityReporter>();
        // Song key from the mix's chroma, for File > Song metadata when there are no notes.
        services.AddSingleton<DotCharter.Services.Audio.Key.ISongKeyAudioEstimator,
            DotCharter.Services.Audio.Key.SongKeyAudioEstimator>();
        services.AddSingleton<LrcLyricsParser>();
        services.AddSingleton<DotCharter.Services.Import.Lyrics.ILyricsFileService,
            DotCharter.Services.Import.Lyrics.LyricsFileService>();
        // Lyric auto-sync: CTC forced alignment of pasted (romanized) lyrics
        // against the song. Prefers a vocal stem separation has ALREADY cached
        // and falls back to the mix — it never starts a separation of its own.
        services.AddSingleton<DotCharter.Services.Audio.Lyrics.ILyricAutoSyncService>(sp =>
            new DotCharter.Services.Audio.Lyrics.LyricAutoSyncService(
                sp.GetRequiredService<ILocalizationService>(),
                // Its sessions take OnnxSessionFactory's Auto ladder, which is
                // DirectML (any DirectX 12 GPU) whenever the machine has one.
                sp.GetService<IAppLogger>()));

        // The models are a one-time ~400 MB download published as release assets,
        // NOT zip payload: they do not change between releases, so shipping them
        // in the app would make every update re-fetch weights the machine has.
        services.AddSingleton<DotCharter.Services.Audio.Lyrics.ILyricModelDownloadService>(sp =>
            new DotCharter.Services.Audio.Lyrics.LyricModelDownloadService(
                new DotCharter.Services.Audio.Lyrics.LyricModelAssetFetcher(
                    () => sp.GetRequiredService<DotCharter.Services.Update.IGitHubAuthService>().GetToken(),
                    sp.GetService<IAppLogger>()),
                sp.GetRequiredService<ILocalizationService>(),
                sp.GetService<IAppLogger>()));
        services.AddSingleton<DotCharter.Services.Editing.Lyrics.ILyricAutoSyncRunner>(sp =>
            new DotCharter.Services.Editing.Lyrics.LyricAutoSyncRunner(
                sp.GetRequiredService<DotCharter.Services.Audio.Lyrics.ILyricAutoSyncService>(),
                sp.GetService<DotCharter.Services.Audio.Lyrics.ILyricModelDownloadService>(),
                sp.GetService<IConfigurationService>(),
                sp.GetRequiredService<ProjectState>()));
    }
}
