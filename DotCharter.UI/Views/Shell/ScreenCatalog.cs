using System.Text;
using Avalonia.Controls;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Contracts.Editor;
using DotCharter.Presentation.Contracts.PlayMode;
using DotCharter.Presentation.Contracts.Home;
using DotCharter.Presentation.Contracts.Library;
using DotCharter.Presentation.Contracts.Onboarding;
using DotCharter.Presentation.Contracts.Settings;
using DotCharter.UI.Views.Dialogs;
using DotCharter.UI.Views.Editor;
using DotCharter.UI.Views.PlayMode.Redesign;
using DotCharter.UI.Views.Prompts;
using DotCharter.UI.Views.Home;
using DotCharter.UI.Views.Library;
using DotCharter.UI.Views.Onboarding;
using DotCharter.UI.Views.Settings;

namespace DotCharter.UI.Views.Shell;

/// <summary>Whether a catalog entry is hosted by <see cref="ScreenHost" /> or by <see cref="DialogHost" />.</summary>
public enum ScreenKind
{
    Screen,
    Dialog
}

/// <summary>One registered screen: its contract, its view, and where it sits in the gallery and the goldens.</summary>
public sealed record ScreenEntry(Type Contract, Type ViewType, string Area, string Name, ScreenKind Kind, Func<Control> CreateView)
{
    /// <summary><c>&lt;area&gt;/&lt;screen&gt;--&lt;variant&gt;.png</c>, relative to <c>DotCharter.Tests/Goldens/ui/</c>.</summary>
    public string GoldenPath(string variant) =>
        $"{ScreenCatalog.Slug(Area)}/{ScreenCatalog.Slug(Name)}--{ScreenCatalog.Slug(variant)}.png";

    /// <summary>The name usage statistics count a dialog under: the view's type name without <c>View</c>.</summary>
    public string UsageId =>
        ViewType.Name.EndsWith("View", StringComparison.Ordinal) ? ViewType.Name[..^4] : ViewType.Name;
}

/// <summary>
///     Contract → view, one line per screen (target-architecture.md § 3). The
///     gallery pages, the golden screenshots and the catalog lints are all
///     derived from <see cref="Default" />; a screen added here gets every one
///     of them without a second registration.
///     <para>
///         The order is the order a player meets the screens, which is also
///         the order <see cref="FlowOf" /> hands a <see cref="ScreenHost" /> for
///         neighbour prewarm.
///     </para>
/// </summary>
public sealed class ScreenCatalog
{
    public const string PlayMode = "Play Mode";
    public const string Dialogs = "Dialogs";
    public const string Home = "Home";
    public const string Library = "Library";
    public const string Onboarding = "Onboarding";
    public const string Editor = "Editor";

    public const string Settings = "Settings";

    public static ScreenCatalog Default { get; } = new(
    [
        Screen<IStartScreen, StartScreenView>(PlayMode, "Start"),
        Screen<ISongSelectScreen, SongSelectScreenView>(PlayMode, "Song select"),
        Screen<ITuningCheckScreen, TuningCheckScreenView>(PlayMode, "Get ready"),
        Screen<ICountdownScreen, CountdownScreenView>(PlayMode, "Countdown"),
        Screen<IPlayingScreen, PlayingScreenView>(PlayMode, "Playing"),
        Screen<IPausedScreen, PausedScreenView>(PlayMode, "Paused"),
        Screen<IFailedScreen, FailedScreenView>(PlayMode, "Failed"),
        Screen<IResultsScreen, ResultsScreenView>(PlayMode, "Results"),
        Screen<IHomeScreen, HomeView>(Home, "Home"),
        Screen<ILibraryScreen, LibraryView>(Library, "Library"),
        Screen<ISettingsShellScreen, SettingsShellView>(Settings, "Shell"),
        Screen<IGeneralSettingsScreen, GeneralSettingsView>(Settings, "General"),
        Screen<IEditorSettingsScreen, EditorSettingsView>(Settings, "Editor"),
        Screen<IPlaySettingsScreen, PlaySettingsView>(Settings, "Play"),
        Screen<IAppearanceSettingsScreen, AppearanceSettingsView>(Settings, "Appearance"),
        Screen<IGraphicsSettingsScreen, GraphicsSettingsView>(Settings, "Graphics"),
        Screen<IAudioSettingsScreen, AudioSettingsView>(Settings, "Audio"),
        Screen<IFilesSettingsScreen, FilesSettingsView>(Settings, "Files"),
        Screen<IKeybindsSettingsScreen, KeybindsSettingsView>(Settings, "Keybinds"),
        Screen<IStemModelsSettingsScreen, StemModelsSettingsView>(Settings, "StemModels"),
        Screen<ITone3000SettingsScreen, Tone3000SettingsView>(Settings, "Tone3000"),
        Screen<IAiSettingsScreen, AiSettingsView>(Settings, "Ai"),
        Screen<IUpdatesSettingsScreen, UpdatesSettingsView>(Settings, "Updates"),
        Screen<ISystemCheckScreen, SystemCheckView>(Onboarding, "SystemCheck"),
        Screen<IChordShapesScreen, ChordShapesView>(Editor, "ChordShapes"),
        Screen<IAiChatScreen, AiChatPaneView>(Editor, "AiChat"),
        Dialog<IMessagePromptScreen, MessagePromptView>(Dialogs, "Message"),
        Dialog<IUnsavedChangesScreen, UnsavedChangesDialogView>(Dialogs, "UnsavedChanges"),
        Dialog<ISetBpmScreen, SetBpmDialogView>(Dialogs, "SetBpm"),
        Dialog<IAiToneGenerateScreen, AiToneGenerateDialogView>(Dialogs, "AiToneGenerate"),
        Dialog<ITempoMapOperationScreen, TempoMapOperationDialogView>(Dialogs, "TempoMapOperation"),
        Dialog<IAlignBeatGridScreen, AlignBeatGridDialogView>(Dialogs, "AlignBeatGrid"),
        Dialog<IAlignToRecordingScreen, AlignToRecordingDialogView>(Dialogs, "AlignToRecording"),
        Dialog<IUpdateAvailableScreen, UpdateAvailableDialogView>(Dialogs, "UpdateAvailable"),
        Dialog<IFhpScreen, FhpDialogView>(Dialogs, "Fhp"),
        Dialog<ILevelGenerationScreen, LevelGenerationDialogView>(Dialogs, "LevelGeneration"),
        Dialog<IBeatScreen, BeatDialogView>(Dialogs, "Beat"),
        Dialog<IPasteSpecialScreen, PasteSpecialDialogView>(Dialogs, "PasteSpecial"),
        Dialog<IArrangementSyncScreen, ArrangementSyncDialogView>(Dialogs, "ArrangementSync"),
        Dialog<IKeyboardShortcutsScreen, KeyboardShortcutsDialogView>(Dialogs, "KeyboardShortcuts"),
        Dialog<IVocalTrackSettingsScreen, VocalTrackSettingsView>(Dialogs, "VocalTrackSettings"),
        Dialog<IChartFromAudioScreen, ChartFromAudioDialogView>(Dialogs, "ChartFromAudio"),
        Dialog<IRsXmlImportScreen, RsXmlImportDialogView>(Dialogs, "RsXmlImport"),
        Dialog<IMidiImportScreen, MidiImportDialogView>(Dialogs, "MidiImport"),
        Dialog<IEofImportScreen, EofImportDialogView>(Dialogs, "EofImport"),
        Dialog<IHistoryScreen, HistoryDialogView>(Dialogs, "History"),
        Dialog<ILibraryDoctorScreen, LibraryDoctorDialogView>(Dialogs, "LibraryDoctor"),
        Dialog<IChartDiffScreen, ChartDiffDialogView>(Dialogs, "ChartDiff"),
        Dialog<IBatchLintScreen, BatchLintDialogView>(Dialogs, "BatchLint"),
        Dialog<IChartQualityReportScreen, ChartQualityReportDialogView>(Dialogs, "ChartQualityReport"),
        Dialog<IShowlightsScreen, ShowlightsDialogView>(Dialogs, "Showlights"),
        Dialog<ICheckpointsScreen, CheckpointsDialogView>(Dialogs, "Checkpoints"),
        Dialog<IGpImportScreen, GpImportDialogView>(Dialogs, "GpImport"),
        Dialog<IAboutScreen, AboutDialogView>(Dialogs, "About"),
        Dialog<ITextDocumentScreen, TextDocumentDialogView>(Dialogs, "TextDocument"),
        Dialog<ISongMetadataScreen, SongMetadataDialogView>(Dialogs, "SongMetadata"),
        Dialog<IRecoveryScreen, RecoveryDialogView>(Dialogs, "Recovery"),
        Dialog<ITunerScreen, TunerDialogView>(Dialogs, "Tuner"),
        Dialog<IEventScreen, EventDialogView>(Dialogs, "Event"),
        Dialog<IArrangementSettingsScreen, ArrangementSettingsDialogView>(Dialogs, "ArrangementSettings"),
        Dialog<INewProjectScreen, NewProjectDialogView>(Dialogs, "NewProject"),
        Dialog<IPatternBuilderScreen, PatternBuilderDialogView>(Dialogs, "PatternBuilder"),
        Dialog<ILatencyWizardScreen, LatencyCalibrationWizardView>(Dialogs, "LatencyWizard"),
        Dialog<IToneDesignerScreen, ToneDesignerDialogView>(Dialogs, "ToneDesigner"),
        Dialog<IToneCreatorScreen, ToneCreatorDialogView>(Dialogs, "ToneCreator"),
        Dialog<IRocksmithExportScreen, RocksmithExportDialogView>(Dialogs, "RocksmithExport"),
        Dialog<IDcSongExportScreen, DcSongExportDialogView>(Dialogs, "DcSongExport"),
        Dialog<IYoutubeAudioScreen, YoutubeAudioDialogView>(Dialogs, "YoutubeAudio")
    ]);

    private readonly List<ScreenEntry> _entries;

    public ScreenCatalog(IEnumerable<ScreenEntry> entries)
    {
        _entries = entries.ToList();
        var repeated = _entries.GroupBy(e => e.Contract).Where(g => g.Count() > 1).Select(g => g.Key.Name)
            .Concat(_entries.GroupBy(e => e.ViewType).Where(g => g.Count() > 1).Select(g => g.Key.Name))
            .ToList();
        if (repeated.Count > 0)
            throw new ArgumentException($"registered more than once: {string.Join(", ", repeated)}", nameof(entries));
    }

    public IReadOnlyList<ScreenEntry> Entries => _entries;

    public ScreenEntry? Find(Type contract) => _entries.FirstOrDefault(e => e.Contract == contract);

    /// <summary>The screens (not dialogs) of one area, in catalog order.</summary>
    public IReadOnlyList<Type> FlowOf(string area) =>
        [.. _entries.Where(e => e.Kind == ScreenKind.Screen && e.Area == area).Select(e => e.Contract)];

    public static ScreenEntry Screen<TContract, TView>(string area, string name)
        where TContract : class where TView : Control, new() =>
        new(typeof(TContract), typeof(TView), area, name, ScreenKind.Screen, () => new TView());

    public static ScreenEntry Dialog<TContract, TView>(string area, string name)
        where TContract : class where TView : Control, new() =>
        new(typeof(TContract), typeof(TView), area, name, ScreenKind.Dialog, () => new TView());

    /// <summary>"Song select" → <c>song-select</c>, "NoMatch" → <c>no-match</c>. Shared with the gallery's PNG names.</summary>
    public static string Slug(string text)
    {
        var sb = new StringBuilder(text.Length + 4);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsUpper(c) && i > 0 && char.IsLetterOrDigit(text[i - 1]) && !char.IsUpper(text[i - 1]))
                sb.Append('-');
            sb.Append(char.IsLetterOrDigit(c) && c < 128 ? char.ToLowerInvariant(c) : '-');
        }

        var slug = sb.ToString();
        while (slug.Contains("--", StringComparison.Ordinal)) slug = slug.Replace("--", "-", StringComparison.Ordinal);
        return slug.Trim('-');
    }
}
