using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.PlayMode;

namespace DotCharter.Presentation.Fakes.PlayMode;

/// <summary>
///     A services-free <see cref="IFailedScreen" />. <see cref="NoSection" /> drops
///     "retry from section" rather than offer a verb that cannot run.
/// </summary>
public sealed class FakeFailedScreen : ObservableObject, IFailedScreen
{
    private FakeFailedScreen(string songTitle, string arrangementName, string sectionLabel,
        double progress, string progressLabel, string accuracyLabel)
    {
        SongTitle = songTitle;
        ArrangementName = arrangementName;
        SectionLabel = sectionLabel;
        Progress = progress;
        ProgressLabel = progressLabel;
        AccuracyLabel = accuracyLabel;

        RetrySectionCommand = new RelayCommand(() => { });
        RestartCommand = new RelayCommand(() => { });
        QuitCommand = new RelayCommand(() => { });
    }

    // ── the named variants ────────────────────────────────────────────────

    public static IReadOnlyList<FakeVariant<IFailedScreen>> Variants { get; } =
    [
        new(FakeVariantNames.Default, WithSection),
        new(FakeVariantNames.Empty, NoSection),
        new(FakeVariantNames.LongText, LongText)
    ];

    /// <summary>Fell in a named section, half way through — all three verbs live.</summary>
    public static FakeFailedScreen WithSection() =>
        new("Master of Puppets", "Lead 1", "Solo B", 0.52, "4:28 of 8:35", "58.4 %");

    /// <summary>A chart with no sections: the retry-from-section verb drops.</summary>
    public static FakeFailedScreen NoSection() =>
        new("Eruption", "Lead", "", 0.19, "0:19 of 1:42", "31.2 %");

    /// <summary>The stress case: a long title, a long section and a long path name.</summary>
    public static FakeFailedScreen LongText() =>
        new(FakeFlowText.GermanSongTitle, FakeFlowText.LongArrangement,
            "非常に長いセクション名のテストケース（第二コーラス、繰り返し）", 0.94,
            "3 Stunden 41 Minuten 22 Sekunden von 3 Stunden 42 Minuten", "99.94 %");

    // ── IFailedScreen ─────────────────────────────────────────────────────

    public string SongTitle { get; }

    public string ArrangementName { get; }

    public bool HasSection => SectionLabel.Length > 0;

    public string SectionLabel { get; }

    public double Progress { get; }

    public string ProgressLabel { get; }

    public string AccuracyLabel { get; }

    public ICommand RetrySectionCommand { get; }

    public ICommand RestartCommand { get; }

    public ICommand QuitCommand { get; }
}
