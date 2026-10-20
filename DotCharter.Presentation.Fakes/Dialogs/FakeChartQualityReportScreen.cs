using System.Windows.Input;
using DotCharter.Presentation.Contracts.Dialogs;

namespace DotCharter.Presentation.Fakes.Dialogs;

/// <summary>A services-free <see cref="IChartQualityReportScreen" />; Close answers true.</summary>
public sealed class FakeChartQualityReportScreen : FakeDialog<bool>, IChartQualityReportScreen
{
    private FakeChartQualityReportScreen(string verdict, bool isReady, string counts,
        IReadOnlyList<IQualityArrangementSummary> arrangements, IReadOnlyList<IQualityFindingGroup> groups,
        string audioStatus, bool isBusy = false, string easierLevelsNote = "",
        IEofRoundTripSection? eofRoundTrip = null, bool isChecking = false)
    {
        IsChecking = isChecking;
        HasVerdict = verdict.Length > 0;
        EofRoundTrip = eofRoundTrip ?? FakeEofRoundTripSection.Lossless();
        EasierLevelsNote = easierLevelsNote;
        Verdict = verdict;
        IsReady = isReady;
        Counts = counts;
        Arrangements = arrangements;
        Groups = groups;
        AudioStatus = audioStatus;
        IsBusy = isBusy;
        VerifyAudioCommand = isBusy ? Disabled() : Inert();
        RecheckCommand = Inert();
        CloseCommand = Answer(true);
    }

    public static IReadOnlyList<FakeVariant<IChartQualityReportScreen>> Variants { get; } =
    [
        new(FakeVariantNames.Default, Default),
        new(FakeVariantNames.Empty, Empty),
        new(FakeVariantNames.LongText, LongText),
        new(FakeVariantNames.Full, Full),
        new(FakeVariantNames.Busy, Busy),
        new("Checking", Checking),
        new("Rechecking", Rechecking)
    ];

    /// <summary>A typical chart: one export blocker, a few rule findings, audio not checked yet.</summary>
    public static FakeChartQualityReportScreen Default() => new("2 things stop the export", false,
        "2 errors · 3 important · 4 warnings",
        [
            new Summary("Lead", "Difficulty 6.4", "2 errors · 2 important · 3 warnings", false, true),
            new Summary("Rhythm", "Difficulty 4.1", "1 important · 1 warning", false, false),
            new Summary("Bass", "Difficulty 3.2", "Clean", true, false)
        ],
        [
            new Group("Stops the export (2)", true, false,
            [
                new Finding("Lead · level 12 · 0:42.250", "Fret number 25 exceeds the in-game maximum of 24.", "Rules · Export", true, false, ""),
                new Finding("Lead · 1:58.000", "The fret-hand position does not cover these notes.", "Export", true, true, "Fill the fret-hand positions of Lead")
            ]),
            new Group("Important (3)", false, true,
            [
                new Finding("Lead · level 12 · 0:12.500", "Hammer-on onto an open string.", "Rules", true, false, ""),
                new Finding("Lead · level 12 · 1:04.750", "Note sits outside its fret-hand position.", "Rules", true, false, ""),
                new Finding("Rhythm · level 8 · 2:31.125", "Chord has no chord template.", "Rules", true, true, "Auto-fix Rhythm: clean up the chart")
            ]),
            new Group("Worth a look (4)", false, false,
            [
                new Finding("Lead", "The arrangement has no base tone.", "Export", false, true, "Open the Tone designer on Lead"),
                new Finding("Lead · level 12 · 0:31.000", "Technique note has no sustain.", "Rules", true, false, ""),
                new Finding("Lead · level 12 · 3:02.375", "Phrase change on a linked note.", "Rules", true, false, ""),
                new Finding("Rhythm · level 8 · 0:08.000", "Anchor close to an unpitched slide.", "Rules", true, false, "")
            ])
        ],
        "Not checked against the audio yet.", easierLevelsNote: "94 on easier levels",
        eofRoundTrip: FakeEofRoundTripSection.Typical());

    /// <summary>A chart every check passes: the verdict and nothing else.</summary>
    public static FakeChartQualityReportScreen Empty() => new("Ready to ship", true, "No findings",
        [
            new Summary("Lead", "Difficulty 6.4", "Clean", true, false),
            new Summary("Rhythm", "Difficulty 4.1", "Clean", true, false)
        ],
        [], "312 of 318 sounds confirmed by the audio.");

    public static FakeChartQualityReportScreen LongText() => new("2 Dinge verhindern den Export", false,
        "2 Fehler · 1 wichtig · 1 Warnung",
        [
            new Summary("Leadgitarre (alternative Stimmung, Kapodaster im dritten Bund)", "Schwierigkeit 7,8",
                "2 Fehler · 1 wichtig · 1 Warnung", false, true),
            new Summary("リズムギター（ドロップD）", "難易度 5.0", "問題なし", true, false)
        ],
        [
            new Group("Verhindert den Export (2)", true, false,
            [
                new Finding("Leadgitarre (alternative Stimmung) · Stufe 12 · 0:42.250",
                    "Die Bundnummer 25 überschreitet das im Spiel erlaubte Maximum von 24; das Spiel lädt den Chart sonst nicht.",
                    "Regeln · Export", true, false, ""),
                new Finding("リズムギター · レベル 8 · 1:58.000", "フレットハンドの位置がこれらの音をカバーしていません。", "エクスポート", true, true,
                    "リズムギターのフレットハンド位置を埋める")
            ]),
            new Group("Wichtig (1)", false, true,
            [
                new Finding("Leadgitarre · Stufe 12 · 0:12.500", "Hammer-on auf eine leere Saite.", "Regeln", true, false, "")
            ]),
            new Group("Einen Blick wert (1)", false, false,
            [
                new Finding("Leadgitarre", "Das Arrangement hat keinen Grundsound.", "Export", false, true, "Den Sound-Designer für die Leadgitarre öffnen")
            ])
        ],
        "Noch nicht mit dem Audio abgeglichen.",
        eofRoundTrip: FakeEofRoundTripSection.LongText());

    /// <summary>A long session's chart: many findings in every group, five arrangements.</summary>
    public static FakeChartQualityReportScreen Full() => new("14 things stop the export", false,
        "14 errors · 40 important · 66 warnings",
        [
            .. Enumerable.Range(1, 5).Select(IQualityArrangementSummary (i) =>
                new Summary($"Arrangement {i}", $"Difficulty {3 + i * 0.9:0.0}", $"{i} errors · {i * 8} important · {i * 13} warnings", false, true))
        ],
        [
            new Group("Stops the export (14)", true, false, Many(14, "Notes overlap on the same string.", "Rules · Export", fix: false)),
            new Group("Important (40)", false, true, Many(40, "Note sits outside its fret-hand position.", "Rules", fix: false)),
            new Group("Worth a look (66)", false, false, Many(66, "Charted E3 at {0} — audio says F3.", "Audio", fix: false))
        ],
        "318 of 412 sounds confirmed by the audio; 66 doubts.", eofRoundTrip: FakeEofRoundTripSection.Full());

    /// <summary>The audio check in flight.</summary>
    public static FakeChartQualityReportScreen Busy() => new("Ready to ship", true, "No findings",
        [new Summary("Lead", "Difficulty 6.4", "Clean", true, false)],
        [], "Checking every charted pitch against the audio…", isBusy: true, eofRoundTrip: FakeEofRoundTripSection.Checking());

    /// <summary>The dialog just opened: the first check runs off the UI thread and nothing is known yet.</summary>
    public static FakeChartQualityReportScreen Checking() => new("", false, "", [], [], "Not checked against the audio yet.",
        isChecking: true);

    /// <summary>Check again after a Fix: the previous report stays on screen under the busy row.</summary>
    public static FakeChartQualityReportScreen Rechecking()
    {
        var shown = Default();
        return new FakeChartQualityReportScreen(shown.Verdict, shown.IsReady, shown.Counts, shown.Arrangements, shown.Groups,
            shown.AudioStatus, easierLevelsNote: shown.EasierLevelsNote, eofRoundTrip: shown.EofRoundTrip, isChecking: true);
    }

    private static ICommand Inert() => new CommunityToolkit.Mvvm.Input.RelayCommand(() => { });

    private static IReadOnlyList<IQualityFinding> Many(int count, string message, string source, bool fix) =>
    [
        .. Enumerable.Range(0, count).Select(IQualityFinding (i) =>
        {
            var time = $"{i / 60}:{i % 60:00}.{i * 125 % 1000:000}";
            return new Finding($"Arrangement {1 + i % 5} · level {10 + i % 6} · {time}",
                string.Format(System.Globalization.CultureInfo.InvariantCulture, message, time), source, true, fix, "");
        })
    ];

    public string Verdict { get; }
    public bool IsReady { get; }
    public bool IsChecking { get; }
    public bool HasVerdict { get; }
    public string Counts { get; }
    public IReadOnlyList<IQualityArrangementSummary> Arrangements { get; }
    public IReadOnlyList<IQualityFindingGroup> Groups { get; }
    public bool HasFindings => Groups.Count > 0;
    public bool ShowEasierLevels { get; set; }
    public bool HasEasierLevelFindings => EasierLevelsNote.Length > 0;
    public string EasierLevelsNote { get; }
    public bool IsBusy { get; }
    public string AudioStatus { get; }
    public bool CanVerifyAudio => !IsBusy;
    public ICommand VerifyAudioCommand { get; }
    public ICommand RecheckCommand { get; }
    public IEofRoundTripSection EofRoundTrip { get; }
    public bool FocusEofRoundTrip
    {
        get;
        set
        {
            field = value;
            if (value) EofRoundTrip.IsExpanded = true;
        }
    }
    public ICommand CloseCommand { get; }

    private sealed record Summary(string Name, string Difficulty, string Counts, bool IsClean, bool HasErrors)
        : IQualityArrangementSummary;

    private sealed record Group(string Title, bool IsBlocking, bool IsImportant, IReadOnlyList<IQualityFinding> Items)
        : IQualityFindingGroup;

    private sealed class Finding(string location, string message, string source, bool hasTime, bool hasFix, string fixDescription)
        : IQualityFinding
    {
        private static readonly ICommand Inert = new CommunityToolkit.Mvvm.Input.RelayCommand(() => { });

        public string Location { get; } = location;
        public string Message { get; } = message;
        public string Source { get; } = source;
        public bool HasTime { get; } = hasTime;
        public ICommand JumpCommand => Inert;
        public bool HasFix { get; } = hasFix;
        public string FixDescription { get; } = fixDescription;
        public ICommand FixCommand => Inert;
    }
}
