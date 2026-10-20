using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using DotCharter.Presentation.Contracts.Dialogs;

namespace DotCharter.Presentation.Fakes.Dialogs;

/// <summary>A services-free <see cref="IAlignToRecordingScreen" />; Apply answers true, Cancel false.</summary>
public sealed class FakeAlignToRecordingScreen : FakeDialog<bool>, IAlignToRecordingScreen
{
    private FakeAlignToRecordingScreen(string source, string status, bool problem, bool busy, IReadOnlyList<IAlignSectionRow> sections,
        string summary)
    {
        SourceText = source;
        StatusText = status;
        StatusIsProblem = problem;
        IsBusy = busy;
        Sections = sections;
        SummaryText = summary;
        AnalyzeCommand = busy ? Disabled() : new CommunityToolkit.Mvvm.Input.RelayCommand(() => { });
        ApplyCommand = sections.Count > 0 ? Answer(true) : Disabled();
        CancelCommand = Answer(false);
    }

    public static IReadOnlyList<FakeVariant<IAlignToRecordingScreen>> Variants { get; } =
    [
        new(FakeVariantNames.Default, Default),
        new(FakeVariantNames.Empty, Empty),
        new(FakeVariantNames.Full, Full),
        new(FakeVariantNames.Busy, Busy),
        new(FakeVariantNames.Error, Error),
        new(FakeVariantNames.LongText, LongText)
    ];

    /// <summary>An analysed song: the bridge was played differently from the tab and is offered unticked.</summary>
    public static FakeAlignToRecordingScreen Default() =>
        new("Song.ogg", "", false, false,
        [
            new Row("Intro · bars 1–4", 0.91, "91%", false, "±812 ms"),
            new Row("Verse · bars 5–20", 0.94, "94%", false, "±1043 ms"),
            new Row("Chorus · bars 21–28", 0.88, "88%", false, "±1190 ms"),
            new Row("Bridge · bars 29–36", 0.31, "31%", true, "±1302 ms"),
            new Row("Outro · bars 37–44", 0.86, "86%", false, "±1421 ms")
        ], "4 of 5 sections will move. Listened to the instrument stems.");

    /// <summary>A long song in 8-bar blocks: 30 sections, every fourth one weak.</summary>
    public static FakeAlignToRecordingScreen Full() =>
        new("Song.ogg", "", false, false,
            Enumerable.Range(0, 30).Select(i => (IAlignSectionRow)new Row($"Bars {i * 8 + 1}–{i * 8 + 8}", i % 4 == 3 ? 0.42 : 0.9,
                i % 4 == 3 ? "42%" : "90%", i % 4 == 3, $"±{300 + 17 * i} ms")).ToList(),
            "23 of 30 sections will move. Listened to the full mix.");

    /// <summary>Opened, not analysed yet.</summary>
    public static FakeAlignToRecordingScreen Empty() => new("Song.ogg", "", false, false, [], "");

    public static FakeAlignToRecordingScreen Busy() => new("Song.ogg", "Listening to the recording…", false, true, [], "");

    /// <summary>No audio loaded: the run cannot start and says why.</summary>
    public static FakeAlignToRecordingScreen Error() =>
        new("No audio loaded", "No audio is loaded. Attach the song's audio first (File ▸ Attach audio).", true, false, [], "");

    /// <summary>The longest section names and the German and Japanese summary.</summary>
    public static FakeAlignToRecordingScreen LongText() =>
        new("Ein sehr langer Dateiname für die Aufnahme des Liedes (Live-Version, remastered 2024).flac", "", false, false,
        [
            new Row("Zwischenspiel mit Gitarrensolo · Takte 101–148", 0.77, "77 %", false, "±12345 ms"),
            new Row("ギターソロのある間奏 · 小節 149–196", 0.12, "12 %", true, "±23456 ms"),
            new Row("Takte 197–244", 0, "keine Noten", true, "±0 ms")
        ], "1 von 3 Abschnitten wird verschoben. Gehört wurden die Instrumenten-Stems. 3 セクション中 1 つが移動します。");

    public string SourceText { get; }

    public bool IsWholeSong { get; set; } = true;

    public bool IsFromBar { get; set; }

    public bool IsLoopRegion { get; set; }

    public bool HasLoopRegion => true;

    public double FromBar { get; set; } = 1;

    public double BarCount => 44;

    public string LoopRegionText => "bars 9–16";

    public bool KeepAnchors { get; set; } = true;

    public string AnchorsText => "3 anchored beats stay where they are";

    public bool IsBusy { get; }

    public string StatusText { get; }

    public bool StatusIsProblem { get; }

    public bool HasResult => Sections.Count > 0;

    public IReadOnlyList<IAlignSectionRow> Sections { get; }

    public string SummaryText { get; }

    public ICommand AnalyzeCommand { get; }

    public ICommand ApplyCommand { get; }

    public ICommand CancelCommand { get; }

    private sealed class Row(string title, double confidence, string confidenceText, bool isLow, string shift)
        : ObservableObject, IAlignSectionRow
    {
        private bool _isApplied = !isLow;

        public string Title => title;

        public double Confidence => confidence;

        public string ConfidenceText => confidenceText;

        public bool IsLow => isLow;

        public string ShiftText => shift;

        public bool IsApplied
        {
            get => _isApplied;
            set => SetProperty(ref _isApplied, value);
        }

        public bool CanApply => true;
    }
}
