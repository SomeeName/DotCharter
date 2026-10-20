using System.Windows.Input;
using DotCharter.Presentation.Contracts.Dialogs;

namespace DotCharter.Presentation.Fakes.Dialogs;

/// <summary>A services-free <see cref="IShowlightsScreen" />; Save answers true, Cancel false.</summary>
public sealed class FakeShowlightsScreen : FakeDialog<bool>, IShowlightsScreen
{
    private static readonly IReadOnlyList<string> Fog = ["None", "Green", "Red", "Blue", "Yellow"];
    private static readonly IReadOnlyList<string> Beams = ["None", "Off", "Green", "Blue+"];
    private static readonly IReadOnlyList<string> Lasers = ["None", "On", "Off"];

    private int _fog;
    private int _beams;
    private int _lasers;
    private int _existing;

    private FakeShowlightsScreen(string beat, string ms, IReadOnlyList<IShowlightPosition> existing, int selected,
        int fog, int beams, int lasers)
    {
        TargetPositionText = beat;
        TargetMsText = ms;
        ExistingPositions = existing;
        _existing = selected;
        _fog = fog;
        _beams = beams;
        _lasers = lasers;
        FogOptions = Fog;
        BeamsOptions = Beams;
        LasersOptions = Lasers;
        UsePlayheadCommand = Disabled();
        DeleteAtPositionCommand = Answer(true);
        SaveCommand = Answer(true);
        CancelCommand = Answer(false);
    }

    public static IReadOnlyList<FakeVariant<IShowlightsScreen>> Variants { get; } =
    [
        new(FakeVariantNames.Default, Default),
        new(FakeVariantNames.Empty, Empty),
        new(FakeVariantNames.LongText, LongText),
        new(FakeVariantNames.Full, Full)
    ];

    /// <summary>The target is one of three cued points, and its cues are loaded.</summary>
    public static FakeShowlightsScreen Default() => new("336 3/4", "101 840.909 ms",
    [
        new Position("0", "Fog: Green · Beams: Off · Lasers: Off"),
        new Position("128", "Fog: Red · Beams: On"),
        new Position("336 3/4", "Fog: Blue · Lasers: On")
    ], 2, 3, 0, 2);

    /// <summary>A song with no cues yet: the empty state, and nothing to delete.</summary>
    public static FakeShowlightsScreen Empty() => new("0", "0 ms", [], -1, 0, 0, 0);

    public static FakeShowlightsScreen LongText() => new("1024 15/16", "1 234 567.891 ms",
    [
        new Position("1024 15/16", "Nebel: Grün · Strahlen: Aus · Laser: An · 未知のキュー（旧フォーマット）")
    ], 0, 1, 1, 2);

    /// <summary>A fully cued song: the list scrolls.</summary>
    public static FakeShowlightsScreen Full() => new("64", "32 000 ms",
    [
        .. Enumerable.Range(0, 60).Select(IShowlightPosition (i) =>
            new Position($"{i * 16}", i % 2 == 0 ? "Fog: Green · Beams: On" : "Fog: Red · Lasers: Off"))
    ], 4, 1, 2, 1);

    public string TargetPositionText { get; }

    public string TargetMsText { get; }

    public ICommand UsePlayheadCommand { get; }

    public IReadOnlyList<string> FogOptions { get; }

    public int SelectedFogIndex
    {
        get => _fog;
        set => SetProperty(ref _fog, value);
    }

    public IReadOnlyList<string> BeamsOptions { get; }

    public int SelectedBeamsIndex
    {
        get => _beams;
        set => SetProperty(ref _beams, value);
    }

    public IReadOnlyList<string> LasersOptions { get; }

    public int SelectedLasersIndex
    {
        get => _lasers;
        set => SetProperty(ref _lasers, value);
    }

    public bool HasExistingPositions => ExistingPositions.Count > 0;

    public IReadOnlyList<IShowlightPosition> ExistingPositions { get; }

    public int SelectedExistingIndex
    {
        get => _existing;
        set => SetProperty(ref _existing, value);
    }

    public bool HasShowlightsAtTarget => _existing >= 0;

    public ICommand DeleteAtPositionCommand { get; }

    public ICommand SaveCommand { get; }

    public ICommand CancelCommand { get; }

    private sealed record Position(string PositionText, string Summary) : IShowlightPosition;
}
