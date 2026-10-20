using System.Windows.Input;
using DotCharter.Presentation.Contracts.Dialogs;

namespace DotCharter.Presentation.Fakes.Dialogs;

/// <summary>A services-free <see cref="ILevelGenerationScreen" />; Generate answers true, Cancel false.</summary>
public sealed class FakeLevelGenerationScreen : FakeDialog<bool>, ILevelGenerationScreen
{
    private const string Lead = "Lead — 1 level now; every generated level replaces it.";

    private FakeLevelGenerationScreen(string subtitle, double count, bool busy, double progress, string progressLabel)
    {
        Subtitle = subtitle;
        LevelCount = count;
        IsBusy = busy;
        Progress = progress;
        ProgressLabel = progressLabel;
        ApplyCommand = busy ? Disabled() : Answer(true);
        CancelCommand = busy ? Disabled() : Answer(false);
    }

    public static IReadOnlyList<FakeVariant<ILevelGenerationScreen>> Variants { get; } =
    [
        new(FakeVariantNames.Default, Default),
        new(FakeVariantNames.Empty, Empty),
        new(FakeVariantNames.LongText, LongText),
        new(FakeVariantNames.Busy, Busy)
    ];

    public static FakeLevelGenerationScreen Default() => new(Lead, 5, false, 0, "");

    public static FakeLevelGenerationScreen Empty() => new("", 2, false, 0, "");

    public static FakeLevelGenerationScreen LongText() =>
        new("Rhythmusgitarre (alternative Stimmung, zweites Arrangement) — derzeit 17 Stufen; 生成すると既存の" +
            "難易度レベルはすべて置き換えられます。", 20, false, 0, "");

    /// <summary>Mid-run: the progress shows and both actions are disabled.</summary>
    public static FakeLevelGenerationScreen Busy() => new(Lead, 8, true, 0.375, "Level 3 of 8");

    public string Subtitle { get; }

    public double LevelCount { get; set; }

    public double Minimum => 2;

    public double Maximum => 20;

    public bool IsBusy { get; }

    public double Progress { get; }

    public string ProgressLabel { get; }

    public ICommand ApplyCommand { get; }

    public ICommand CancelCommand { get; }
}
