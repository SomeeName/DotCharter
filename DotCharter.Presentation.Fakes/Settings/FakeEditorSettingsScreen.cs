using CommunityToolkit.Mvvm.ComponentModel;
using DotCharter.Presentation.Contracts.Settings;

namespace DotCharter.Presentation.Fakes.Settings;

/// <summary>A services-free Editor tab in every state the screen can be in.</summary>
public sealed partial class FakeEditorSettingsScreen : ObservableObject, IEditorSettingsScreen
{
    [ObservableProperty] private bool _useFastChartingInput;
    [ObservableProperty] private bool _syncHandShapeToChordTemplate = true;
    [ObservableProperty] private bool _syncAnchorToChordFingering;
    [ObservableProperty] private bool _autoNameNewChords = true;
    [ObservableProperty] private bool _suppressDoubleStopChordNames = true;
    [ObservableProperty] private bool _keepPowerChordNames;
    [ObservableProperty] private bool _musicalChordNames;
    [ObservableProperty] private int _tappedHarmonicFretOffset = 12;
    [ObservableProperty] private bool _lyricSpanWordGap = true;
    [ObservableProperty] private bool _lyricSpanPhraseGap;
    [ObservableProperty] private int _stringOrderIndex;
    [ObservableProperty] private bool _timeline2DPolish = true;
    [ObservableProperty] private bool _timeline2DLaneTint;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMinNoteDistanceInMilliseconds))]
    private int _minNoteDistanceModeIndex = 1;

    [ObservableProperty] private double _minNoteDistanceMs = 10;
    [ObservableProperty] private int _minNoteDistanceDivisor = 64;
    [ObservableProperty] private bool _pasteIncludeNotes = true;
    [ObservableProperty] private bool _pasteIncludeChords = true;
    [ObservableProperty] private bool _pasteIncludeHandShapes = true;
    [ObservableProperty] private bool _pasteIncludeFretAnchors = true;
    [ObservableProperty] private bool _pasteIncludeEvents = true;
    [ObservableProperty] private bool _pasteIncludeTones = true;
    [ObservableProperty] private string _defaultCharterName = "";
    [ObservableProperty] private bool _padSilenceOnNewProject = true;
    [ObservableProperty] private double _leadingSilenceSeconds = 10;
    [ObservableProperty] private double _trailingSilenceSeconds = 2;

    private FakeEditorSettingsScreen()
    {
    }

    public static IReadOnlyList<FakeVariant<IEditorSettingsScreen>> Variants { get; } =
    [
        new(FakeVariantNames.Default, Default),
        new(FakeVariantNames.Empty, Empty),
        new(FakeVariantNames.LongText, LongText),
        new(FakeVariantNames.Full, Full),
        new("Milliseconds", Milliseconds)
    ];

    /// <summary>The out-of-the-box values: measure-fraction spacing, every paste filter on.</summary>
    public static FakeEditorSettingsScreen Default() => new() { DefaultCharterName = "Alex" };

    /// <summary>Every switch off and no charter name, so each dependent row is disabled.</summary>
    public static FakeEditorSettingsScreen Empty() => new()
    {
        SyncHandShapeToChordTemplate = false,
        SuppressDoubleStopChordNames = false,
        LyricSpanWordGap = false,
        PasteIncludeNotes = false,
        PasteIncludeChords = false,
        PasteIncludeHandShapes = false,
        PasteIncludeFretAnchors = false,
        PasteIncludeEvents = false,
        PasteIncludeTones = false,
        PadSilenceOnNewProject = false,
        Timeline2DPolish = false
    };

    public static FakeEditorSettingsScreen LongText() => new()
    {
        DefaultCharterName = "Donaudampfschifffahrtsgesellschaftskapitän・長いチャーター名",
        MinNoteDistanceModeIndex = 2,
        MinNoteDistanceDivisor = 1024,
        LeadingSilenceSeconds = 600,
        TrailingSilenceSeconds = 599.75
    };

    /// <summary>Every switch on, so each dependent row is enabled.</summary>
    public static FakeEditorSettingsScreen Full() => new()
    {
        UseFastChartingInput = true,
        SyncAnchorToChordFingering = true,
        KeepPowerChordNames = true,
        LyricSpanPhraseGap = true,
        StringOrderIndex = 1,
        Timeline2DLaneTint = true,
        DefaultCharterName = "Alex"
    };

    /// <summary>Spacing in milliseconds: the millisecond row is live and the denominator is not.</summary>
    public static FakeEditorSettingsScreen Milliseconds() => new() { MinNoteDistanceModeIndex = 0, MinNoteDistanceMs = 25 };

    public bool IsMinNoteDistanceInMilliseconds => MinNoteDistanceModeIndex == 0;

    public IReadOnlySet<string> HiddenKeys { get; } = new HashSet<string>();
}
