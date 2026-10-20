using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.PlayMode;

namespace DotCharter.Presentation.Fakes.PlayMode;

/// <summary>
///     A services-free <see cref="IPlayingScreen" />: the HUD with no engine behind it.
///     <see cref="Extreme" /> has eight-digit figures and a sentence-long section name.
/// </summary>
public sealed class FakePlayingScreen : ObservableObject, IPlayingScreen
{
    private FakePlayingScreen(string songTitle, string arrangementName, string scoreLabel,
        string multiplierLabel, string accuracyLabel, string streakLabel, string hitsLabel,
        string missesLabel, string sectionLabel, double sectionProgress, string sectionCountLabel,
        double songProgress, string elapsedLabel, string totalLabel, double energyLevel)
    {
        SongTitle = songTitle;
        ArrangementName = arrangementName;
        ScoreLabel = scoreLabel;
        MultiplierLabel = multiplierLabel;
        AccuracyLabel = accuracyLabel;
        StreakLabel = streakLabel;
        HitsLabel = hitsLabel;
        MissesLabel = missesLabel;
        SectionLabel = sectionLabel;
        SectionProgress = sectionProgress;
        SectionCountLabel = sectionCountLabel;
        SongProgress = songProgress;
        ElapsedLabel = elapsedLabel;
        TotalLabel = totalLabel;
        EnergyLevel = energyLevel;
        PauseCommand = new RelayCommand(() => { });
    }

    // ── the named variants ────────────────────────────────────────────────

    public static IReadOnlyList<FakeVariant<IPlayingScreen>> Variants { get; } =
    [
        new(FakeVariantNames.Default, Solo),
        new(FakeVariantNames.Empty, Empty),
        new(FakeVariantNames.LongText, Extreme),
        new("Struggling", Struggling)
    ];

    /// <summary>One player, mid-song, doing well.</summary>
    public static FakePlayingScreen Solo() =>
        new("Master of Puppets", "Lead 1", "612,400", "x4", "94.1 %", "218", "1,884", "62",
            "Solo B", 0.42, "12 of 28", 0.58, "3:41", "8:35", 0.86);

    /// <summary>
    ///     The first frame of a run: nothing judged yet, so every figure is at zero and the
    ///     meter is full, on an untitled chart without sections.
    /// </summary>
    public static FakePlayingScreen Empty() =>
        new("Untitled", "Lead", "0", "x1", "—", "0", "0", "0",
            "", 0, "", 0, "0:00", "3:12", 1.0);

    /// <summary>
    ///     The low-energy case with no sections at all: the meter is nearly out
    ///     and the section block has to disappear rather than print an empty
    ///     caption over an empty bar.
    /// </summary>
    public static FakePlayingScreen Struggling() =>
        new("Eruption", "Lead", "12,400", "x1", "41.2 %", "0", "184", "262",
            "", 0, "", 0.22, "0:22", "1:42", 0.08);

    /// <summary>The stress case: eight-digit figures and long names in three scripts.</summary>
    public static FakePlayingScreen Extreme() =>
        new(FakeFlowText.LongSongTitle, FakeFlowText.LongArrangement,
            "12,345,678", "x128", "99.94 %", "12,204", "48,812", "0",
            "非常に長いセクション名のテストケース（第二コーラス）", 0.97, "212 of 214", 0.995,
            "3:41:22", "3:42:00", 1.0);

    // ── IPlayingScreen ────────────────────────────────────────────────────

    public string SongTitle { get; }

    public string ArrangementName { get; }

    public string ScoreLabel { get; }

    public string MultiplierLabel { get; }

    public string AccuracyLabel { get; }

    public string StreakLabel { get; }

    public string HitsLabel { get; }

    public string MissesLabel { get; }

    public string SectionLabel { get; }

    public bool HasSection => SectionLabel.Length > 0;

    public double SectionProgress { get; }

    public string SectionCountLabel { get; }

    public double SongProgress { get; }

    public string ElapsedLabel { get; }

    public string TotalLabel { get; }

    public double EnergyLevel { get; }

    public bool IsEnergyLow => EnergyLevel < 0.25;

    public ICommand PauseCommand { get; }
}
