using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.PlayMode;

namespace DotCharter.Presentation.Fakes.PlayMode;

/// <summary>
///     A services-free <see cref="IPausedScreen" />.
///     <para>
///         <see cref="NotStarted" /> is the variant that is easy to forget and
///         the one the current screen prints four zeroes in: pausing during the
///         count-in or before the first judged note is ordinary, and "0 · 0.0 %
///         · 0" reads as a bad run rather than as no run.
///     </para>
/// </summary>
public sealed class FakePausedScreen : ObservableObject, IPausedScreen
{
    private FakePausedScreen(string songTitle, bool hasRun, string scoreLabel, string accuracyLabel,
        string streakLabel, string elapsedLabel, string totalLabel, double progress,
        string sectionLabel, IReadOnlyList<IPauseTool> tools)
    {
        SongTitle = songTitle;
        HasRun = hasRun;
        ScoreLabel = scoreLabel;
        AccuracyLabel = accuracyLabel;
        StreakLabel = streakLabel;
        ElapsedLabel = elapsedLabel;
        TotalLabel = totalLabel;
        Progress = progress;
        SectionLabel = sectionLabel;
        Tools = tools;

        ResumeCommand = new RelayCommand(() => { });
        RestartCommand = new RelayCommand(() => { });
        EndRunCommand = new RelayCommand(() => { });
        LeaveToSongSelectCommand = new RelayCommand(() => { });
        LeaveToMenuCommand = new RelayCommand(() => { });
    }

    // ── the named variants ────────────────────────────────────────────────

    public static IReadOnlyList<FakeVariant<IPausedScreen>> Variants { get; } =
    [
        new(FakeVariantNames.Default, MidRun),
        new(FakeVariantNames.Empty, NotStarted),
        new(FakeVariantNames.LongText, LongText),
        new(FakeVariantNames.Full, Full),
        new("Setlist", InSetlist)
    ];

    /// <summary>Paused mid-run with a real score behind it.</summary>
    public static FakePausedScreen MidRun() =>
        new("Master of Puppets", hasRun: true, "612,400", "94.1 %", "218", "3:41", "8:35", 0.43,
            "Solo B", DefaultTools());

    /// <summary>Paused before the first judged note — the figures are hidden, not zeroed.</summary>
    public static FakePausedScreen NotStarted() =>
        new("Creep", hasRun: false, "", "", "", "0:00", "3:56", 0, "", DefaultTools());

    /// <summary>
    ///     Paused mid-run with the two run-scoped tools on top of the usual five: seven tiles,
    ///     so the two-column grid ends on a half-filled row.
    /// </summary>
    public static FakePausedScreen Full() =>
        new("Pull Me Under", hasRun: true, "1,204,800", "91.6 %", "412", "5:02", "8:11", 0.61,
            "Solo A",
            [
                .. DefaultTools(),
                new FakePauseTool("Practice speed", "Slow the song down from here"),
                new FakePauseTool("Metronome", "Click along on every beat")
            ]);

    /// <summary>The stress case: a long title, a long section, eight-digit figures.</summary>
    public static FakePausedScreen LongText() => WithSetlist(LongTextRun(), FakeSetlistRun.LongText());

    /// <summary>Paused on song 2 of a 5-song setlist: Skip song and End setlist are offered.</summary>
    public static FakePausedScreen InSetlist() => WithSetlist(MidRun(), FakeSetlistRun.MidRun());

    private static FakePausedScreen WithSetlist(FakePausedScreen screen, FakeSetlistRun setlist)
    {
        screen.Setlist = setlist;
        return screen;
    }

    private static FakePausedScreen LongTextRun() =>
        new(FakeFlowText.LongSongTitle, hasRun: true, "12,345,678", "99.94 %", "12,204",
            "3:41:22", "3:42:00", 0.995,
            "非常に長いセクション名のテストケース（第二コーラス、繰り返し）",
            [
                new FakePauseTool("Stimmgerät öffnen",
                    "Überprüfe deine Stimmung, ohne den Durchlauf zu beenden."),
                new FakePauseTool("リフ・リピーター",
                    "難しいパッセージをループさせて、ゆっくり練習できます。"),
                new FakePauseTool("Settings",
                    "Audio, gameplay and graphics — every one of them applies live, mid-run."),
                new FakePauseTool("VST plugins",
                    "Pick a VST3 amp and open its own editor without leaving the run."),
                new FakePauseTool("Rig",
                    "Dein Rig: Pedale, Amp und Box, ohne den Durchlauf zu verlassen.")
            ]);

    // ── IPausedScreen ─────────────────────────────────────────────────────

    public string SongTitle { get; }

    public bool HasRun { get; }

    public string ScoreLabel { get; }

    public string AccuracyLabel { get; }

    public string StreakLabel { get; }

    public string ElapsedLabel { get; }

    public string TotalLabel { get; }

    public double Progress { get; }

    public bool HasSection => SectionLabel.Length > 0;

    public string SectionLabel { get; }

    public ICommand ResumeCommand { get; }

    public ICommand RestartCommand { get; }

    public IReadOnlyList<IPauseTool> Tools { get; }

    public ICommand EndRunCommand { get; }

    public ICommand LeaveToSongSelectCommand { get; }

    public ICommand LeaveToMenuCommand { get; }

    public IPausedSetlist Setlist { get; private set; } = FakeSetlistRun.Idle();

    private static IReadOnlyList<IPauseTool> DefaultTools() =>
    [
        new FakePauseTool("Open tuner", "Check your tuning without ending the run"),
        new FakePauseTool("Riff repeater", "Loop and slow down a hard passage"),
        new FakePauseTool("Settings", "Audio, gameplay and graphics — live"),
        new FakePauseTool("VST plugins", "Pick a VST amp and open its editor"),
        new FakePauseTool("Rig", "Your rig: pedals, amp and cabinet")
    ];
}

/// <summary>One tile in the pause menu's tool grid.</summary>
public sealed class FakePauseTool : ObservableObject, IPauseTool
{
    public FakePauseTool(string title, string caption)
    {
        Title = title;
        Caption = caption;
        InvokeCommand = new RelayCommand(() => { });
    }

    public string Title { get; }

    public string Caption { get; }

    public ICommand InvokeCommand { get; }
}
