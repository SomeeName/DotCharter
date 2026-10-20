using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.PlayMode;

namespace DotCharter.Presentation.Fakes.PlayMode;

/// <summary>
///     A services-free <see cref="IResultsScreen" />.
///     <para>
///         Three panels under the hero each have their own empty state, and
///         <see cref="FirstRun" /> is all three at once: a chart with no
///         sections, a run with no timing samples and a machine with one
///         profile are every one of them ordinary, and a card whose data is
///         empty keeps its slot and says why. Captures of the current screen
///         never had that combination.
///     </para>
/// </summary>
public sealed class FakeResultsScreen : ObservableObject, IResultsScreen
{
    /// <summary>The track the histogram is drawn in. The fake and the view agree on it.</summary>
    public const double TimingTrackHeight = 96;

    private bool _isTakePlaying;

    private FakeResultsScreen(string songTitle, string arrangementName, string tuningLabel,
        string gradeLabel, string scoreLabel, string accuracyLabel, string streakLabel,
        string hitsLabel, string missesLabel, bool isFullCombo, bool isNewRecord,
        string comparisonLabel, IReadOnlyList<IResultSection> sections, bool hasTiming,
        string timingHeadline, string timingMeanLabel, string timingSpreadLabel,
        string timingEarlyLabel, string timingLateLabel, string timingOnTimeLabel,
        IReadOnlyList<ITimingBar> timingBars, IReadOnlyList<ILeaderboardRow> leaderboard,
        bool canListenBack)
    {
        SongTitle = songTitle;
        ArrangementName = arrangementName;
        TuningLabel = tuningLabel;
        GradeLabel = gradeLabel;
        ScoreLabel = scoreLabel;
        AccuracyLabel = accuracyLabel;
        StreakLabel = streakLabel;
        HitsLabel = hitsLabel;
        MissesLabel = missesLabel;
        IsFullCombo = isFullCombo;
        IsNewRecord = isNewRecord;
        ComparisonLabel = comparisonLabel;
        Sections = sections;
        HasTiming = hasTiming;
        TimingHeadline = timingHeadline;
        TimingMeanLabel = timingMeanLabel;
        TimingSpreadLabel = timingSpreadLabel;
        TimingEarlyLabel = timingEarlyLabel;
        TimingLateLabel = timingLateLabel;
        TimingOnTimeLabel = timingOnTimeLabel;
        TimingBars = timingBars;
        Leaderboard = leaderboard;
        CanListenBack = canListenBack;

        RetryCommand = new RelayCommand(() => { });
        PracticeCommand = new RelayCommand(() => { });
        SongSelectCommand = new RelayCommand(() => { });
        OpenInEditorCommand = new RelayCommand(() => { });
        ListenBackCommand = new RelayCommand(() =>
        {
            _isTakePlaying = !_isTakePlaying;
            OnPropertyChanged(nameof(IsTakePlaying));
        });
    }

    // ── the named variants ────────────────────────────────────────────────

    public static IReadOnlyList<FakeVariant<IResultsScreen>> Variants { get; } =
    [
        new(FakeVariantNames.Default, Strong),
        new(FakeVariantNames.Empty, FirstRun),
        new(FakeVariantNames.LongText, LongText),
        new(FakeVariantNames.Full, Full),
        new("Weak", Weak),
        new("SetlistNext", SetlistNext),
        new("SetlistDone", SetlistDone),
        new("TechniqueDowngrades", TechniqueDowngrades)
    ];

    /// <summary>
    /// A run that hit notes WITHOUT their technique: bends left unbent, a slide
    ///     picked instead of slid, vibrato held still, palm mutes played open. Each is a hit at
    ///     half score, never a miss, and the technique line counts them.
    /// </summary>
    public static FakeResultsScreen TechniqueDowngrades() => WithTechniques(Weak(),
        "Played without the technique: bends 3/12 · slides 1/4 · vibrato 2/9 · palm mutes 5/64");

    private static FakeResultsScreen WithTechniques(FakeResultsScreen screen, string label)
    {
        screen.TechniqueLabel = label;
        return screen;
    }

    /// <summary>Song 2 of a 5-song setlist: "Next: &lt;song&gt;" is the primary action.</summary>
    public static FakeResultsScreen SetlistNext() => WithSetlist(Strong(), FakeSetlistRun.MidRun());

    /// <summary>The setlist's last song: it says the setlist is done.</summary>
    public static FakeResultsScreen SetlistDone() => WithSetlist(Weak(), FakeSetlistRun.LastSong());

    private static FakeResultsScreen WithSetlist(FakeResultsScreen screen, FakeSetlistRun setlist)
    {
        screen.Setlist = setlist;
        return screen;
    }

    /// <summary>A strong run: S grade, a full combo, a new record, every panel full.</summary>
    public static FakeResultsScreen Strong() =>
        new("Master of Puppets", "Lead 1", "E♭ Standard", "S", "1,940,200", "99.4 %", "1,884",
            "1,884", "0", isFullCombo: true, isNewRecord: true,
            "New best! +128,400 over your previous 1,811,800.",
            BuildSections(
                ("Intro", "0:00", 0.99), ("Verse 1", "0:42", 0.97), ("Chorus 1", "1:24", 1.0),
                ("Verse 2", "2:06", 0.96), ("Solo A", "3:18", 0.92), ("Solo B", "4:28", 0.99),
                ("Bridge", "5:40", 0.98), ("Chorus 3", "6:52", 1.0), ("Outro", "7:48", 0.99)),
            hasTiming: true, "On time", "+1.4 ms", "±8.2 ms", "412", "396", "1,076",
            Histogram(2, 6, 14, 30, 58, 92, 61, 34, 15, 7, 3),
            BuildLeaderboard(("1", "Alex", "1,940,200", "99.4 %", true),
                ("2", "Mara", "1,204,880", "88.1 %", false),
                ("3", "Jonas", "812,400", "74.6 %", false)),
            canListenBack: true);

    /// <summary>
    ///     A weak run: a D grade, three weak sections and a rushing verdict.
    /// </summary>
    public static FakeResultsScreen Weak() =>
        new("Goat", "Lead", "Drop C#", "D", "184,600", "61.2 %", "24", "612", "388",
            isFullCombo: false, isNewRecord: false, "vs. best: -428,900",
            BuildSections(
                ("Intro", "0:00", 0.94), ("Verse 1", "0:24", 0.58), ("Tapping A", "0:58", 0.31),
                ("Bridge", "1:14", 0.81),
                ("Chorus", "1:32", 0.72), ("Tapping B", "2:06", 0.22), ("Outro", "2:54", 0.64)),
            hasTiming: true, "Rushing", "-24.8 ms", "±41.6 ms", "486", "114", "212",
            Histogram(18, 44, 78, 92, 61, 34, 18, 9, 4, 2, 1),
            BuildLeaderboard(("1", "Mara", "702,400", "91.2 %", false),
                ("2", "Alex", "184,600", "61.2 %", true)),
            canListenBack: true);

    /// <summary>
    ///     The first run ever recorded, on a chart with no sections, with no
    ///     timing samples and one profile on the machine. All three panels are
    ///     empty at once and each keeps its slot.
    /// </summary>
    public static FakeResultsScreen FirstRun() =>
        new("Blackbird", "Lead", "Open G", "B", "288,400", "86.4 %", "112", "402", "64",
            isFullCombo: false, isNewRecord: false, "First run recorded — 288,400.",
            [], hasTiming: false, "", "", "", "", "", "", [], [],
            canListenBack: false);

    /// <summary>
    ///     A long progressive chart at its realistic maximum: thirty-two sections in all
    ///     three bands, and a leaderboard of twelve profiles on one machine.
    /// </summary>
    public static FakeResultsScreen Full()
    {
        string[] parts = ["Intro", "Verse", "Pre-chorus", "Chorus", "Riff", "Solo", "Bridge", "Interlude"];
        var sections = Enumerable.Range(0, 32)
            .Select(i => (parts[i % parts.Length] + " " + (i / parts.Length + 1).ToString(CultureInfo.InvariantCulture),
                TimeSpan.FromSeconds(i * 15).ToString(@"m\:ss", CultureInfo.InvariantCulture),
                0.55 + (i * 37 % 45) / 100.0))
            .ToArray();
        string[] names = ["Alex", "Mara", "Jonas", "Lena", "Felix", "Sophie", "Paul", "Hannah", "Noah", "Emma", "Ben", "Guest (Saturday)"];
        var leaderboard = names
            .Select((n, i) => ((i + 1).ToString(CultureInfo.InvariantCulture), n,
                (1_884_000 - i * 100_000).ToString("N0", CultureInfo.InvariantCulture),
                (0.982 - i * 0.031).ToString("P1", CultureInfo.InvariantCulture), i == 2))
            .ToArray();
        return new FakeResultsScreen("Pull Me Under", "Lead", "E Standard", "A", "1,641,400", "93.2 %", "604",
            "2,248", "164", isFullCombo: false, isNewRecord: false, "vs. best: -42,600",
            BuildSections(sections),
            hasTiming: true, "On time", "-2.1 ms", "±11.4 ms", "688", "702", "1,858",
            Histogram(3, 9, 21, 44, 71, 96, 70, 42, 20, 8, 2),
            BuildLeaderboard(leaderboard),
            canListenBack: true);
    }

    /// <summary>The stress case: long names in three scripts and eight-digit figures.</summary>
    public static FakeResultsScreen LongText() => WithTechniques(WithSetlist(LongTextRun(), FakeSetlistRun.LongText()),
        "Ohne die Technik gespielt: Bendings 12/48 · Slides 3/20 · Vibrato 9/31 · Palm Mutes 41/388");

    private static FakeResultsScreen LongTextRun() =>
        new(FakeFlowText.LongSongTitle, FakeFlowText.LongArrangement, FakeFlowText.LongTuning,
            "S", "12,345,678", "99.94 %", "12,204", "48,812", "0",
            isFullCombo: true, isNewRecord: true,
            "Neuer Bestwert! +4.204.118 Punkte über deinem bisherigen Bestwert von 8.141.560 — "
            + "und zum ersten Mal ein voller Combo auf diesem Arrangement.",
            BuildSections(
                ("Ein außergewöhnlich langer Abschnittsname für den Test", "0:00", 0.99),
                ("非常に長いセクション名のテストケース（第一コーラス）", "12:42", 0.74),
                ("The Extraordinarily Long Section Name Appreciation Society Interlude", "48:18", 0.41),
                ("Langsam; Lebhaft (Wiederholung mit erweitertem Gitarrensolo)", "1:24:06", 0.88)),
            hasTiming: true, "Leicht überhastet gespielt", "-0.04 ms", "±0.02 ms",
            "412,884", "396,118", "48,812,004",
            Histogram(4, 12, 28, 52, 88, 96, 74, 41, 22, 11, 5),
            BuildLeaderboard(("1", FakeFlowText.LongProfileName, "12,345,678", "99.94 %", true),
                ("2", "非常に長いプロフィール名のテストケースです", "9,876,543", "97.12 %", false),
                ("3", "The Extraordinarily Long Profile Name Appreciation Society", "4,204,118",
                    "88.40 %", false)),
            canListenBack: true);

    // ── IResultsScreen ────────────────────────────────────────────────────

    public IResultsSetlist Setlist { get; private set; } = FakeSetlistRun.Idle();

    public string SongTitle { get; }

    public string ArrangementName { get; }

    public string TuningLabel { get; }

    public string GradeLabel { get; }

    public string ScoreLabel { get; }

    public string AccuracyLabel { get; }

    public string StreakLabel { get; }

    public string HitsLabel { get; }

    public string MissesLabel { get; }

    public bool IsFullCombo { get; }

    public bool IsNewRecord { get; }

    public string ComparisonLabel { get; }

    public string TechniqueLabel { get; private set; } = "";

    public bool HasTechniqueLine => TechniqueLabel.Length > 0;

    public IReadOnlyList<IResultSection> Sections { get; }

    public bool HasSections => Sections.Count > 0;

    public bool HasTiming { get; }

    public string TimingHeadline { get; }

    public string TimingMeanLabel { get; }

    public string TimingSpreadLabel { get; }

    public string TimingEarlyLabel { get; }

    public string TimingLateLabel { get; }

    public string TimingOnTimeLabel { get; }

    public IReadOnlyList<ITimingBar> TimingBars { get; }

    public IReadOnlyList<ILeaderboardRow> Leaderboard { get; }

    public bool HasLeaderboard => Leaderboard.Count > 1;

    public ICommand RetryCommand { get; }

    public ICommand PracticeCommand { get; }

    public bool CanPractice => Sections.Any(s => s.IsWeak);

    public ICommand SongSelectCommand { get; }

    public ICommand OpenInEditorCommand { get; }

    public ICommand ListenBackCommand { get; }

    public bool CanListenBack { get; }

    public bool IsTakePlaying => _isTakePlaying;

    // ── the data ──────────────────────────────────────────────────────────

    private static IReadOnlyList<IResultSection> BuildSections(
        params (string Name, string Time, double Accuracy)[] rows) =>
        rows.Select(r => (IResultSection)new FakeResultSection(r.Name, r.Time, r.Accuracy)).ToList();

    /// <summary>
    ///     The counts are turned into DIP heights HERE rather than in the view:
    ///     a histogram column cannot be a <c>ProgressBar</c> and a star-sized
    ///     Grid row cannot be bound, so the fake and the view agree on
    ///     <see cref="TimingTrackHeight" /> instead.
    /// </summary>
    private static IReadOnlyList<ITimingBar> Histogram(params int[] counts)
    {
        var peak = counts.Length == 0 ? 1 : Math.Max(1, counts.Max());
        var centre = counts.Length / 2;
        return counts
            .Select((c, i) => (ITimingBar)new FakeTimingBar(
                Math.Max(2, TimingTrackHeight * c / peak), i == centre))
            .ToList();
    }

    private static IReadOnlyList<ILeaderboardRow> BuildLeaderboard(
        params (string Rank, string Name, string Score, string Accuracy, bool IsYou)[] rows) =>
        rows.Select(r => (ILeaderboardRow)new FakeLeaderboardRow(r.Rank, r.Name, r.Score, r.Accuracy,
            r.IsYou)).ToList();
}

/// <summary>One section of the chart, as the breakdown draws it.</summary>
public sealed class FakeResultSection : ObservableObject, IResultSection
{
    public FakeResultSection(string name, string timeLabel, double accuracy)
    {
        Name = name;
        TimeLabel = timeLabel;
        Accuracy = accuracy;
        AccuracyLabel = accuracy.ToString("P1", CultureInfo.CurrentCulture);
    }

    public string Name { get; }

    public string TimeLabel { get; }

    public string AccuracyLabel { get; }

    public double Accuracy { get; }

    public bool IsWeak => Accuracy < 0.75;

    public bool IsFair => Accuracy is >= 0.75 and < 0.90;

    public bool IsStrong => Accuracy >= 0.90;
}

/// <summary>One column of the timing histogram, already in DIPs.</summary>
public sealed class FakeTimingBar(double barHeight, bool isCentre) : ITimingBar
{
    public double BarHeight { get; } = barHeight;

    public bool IsCentre { get; } = isCentre;
}

/// <summary>One profile's best on this song.</summary>
public sealed class FakeLeaderboardRow(string rankLabel, string profileName, string scoreLabel,
    string accuracyLabel, bool isActiveProfile) : ObservableObject, ILeaderboardRow
{
    public string RankLabel { get; } = rankLabel;

    public string ProfileName { get; } = profileName;

    public string ScoreLabel { get; } = scoreLabel;

    public string AccuracyLabel { get; } = accuracyLabel;

    public bool IsActiveProfile { get; } = isActiveProfile;
}
