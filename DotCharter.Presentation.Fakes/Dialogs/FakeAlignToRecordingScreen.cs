using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using DotCharter.Presentation.Contracts.Dialogs;

namespace DotCharter.Presentation.Fakes.Dialogs;

/// <summary>A services-free <see cref="IAlignToRecordingScreen" />; Apply answers true, Cancel false.</summary>
public sealed class FakeAlignToRecordingScreen : FakeDialog<bool>, IAlignToRecordingScreen
{
    private const string NoNotes = "no notes: follows its neighbors";

    private bool _showSections;

    private FakeAlignToRecordingScreen(string source, string status, bool problem, bool busy, IReadOnlyList<IAlignSectionRow> sections,
        string summary, bool weak = false)
    {
        SourceText = source;
        StatusText = status;
        StatusIsProblem = problem;
        IsBusy = busy;
        Sections = sections;
        SummaryText = summary;
        IsWeakMatch = weak;
        _showSections = sections.Count > 0 && !weak;
        AnalyzeCommand = busy ? Disabled() : new CommunityToolkit.Mvvm.Input.RelayCommand(() => { });
        ApplyCommand = sections.Any(s => s.IsApplied) ? Answer(true) : Disabled();
        CancelCommand = Answer(false);
    }

    public static IReadOnlyList<FakeVariant<IAlignToRecordingScreen>> Variants { get; } =
    [
        new(FakeVariantNames.Default, Default),
        new(FakeVariantNames.Empty, Empty),
        new(FakeVariantNames.Full, Full),
        new(FakeVariantNames.Busy, Busy),
        new(FakeVariantNames.Error, Error),
        new(FakeVariantNames.LongText, LongText),
        new(nameof(WithoutNotes), WithoutNotes),
        new(nameof(WeakMatch), WeakMatch)
    ];

    /// <summary>An analysed song: the tab rests through the intro, and the bridge was played differently
    ///     from the tab and is offered unticked.</summary>
    public static FakeAlignToRecordingScreen Default() =>
        new("Song.ogg", "", false, false,
        [
            Row.Silent("Intro · bars 1–4", NoNotes),
            new Row("Verse · bars 5–20", 0.94, "94%", false, "±1043 ms"),
            new Row("Chorus · bars 21–28", 0.88, "88%", false, "±1190 ms"),
            new Row("Bridge · bars 29–36", 0.31, "31%", true, "±1302 ms"),
            new Row("Outro · bars 37–44", 0.86, "86%", false, "±1421 ms")
        ], "3 of 4 sections will move. Listened to the instrument stems.");

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

    /// <summary>A chart with a beat grid and no notes: there is nothing to match against the recording.</summary>
    public static FakeAlignToRecordingScreen WithoutNotes() =>
        new("Song.ogg", "This moves a tab's beats onto the recording, and this chart has no notes yet.", true, false, [], "");

    /// <summary>The wrong audio for this tab: no section matched, so the result is one line and the rows start hidden.</summary>
    public static FakeAlignToRecordingScreen WeakMatch() =>
        new("Another song.ogg",
            "The recording and the tab do not match well enough: no section reached the confidence needed to move it. " +
            "The usual causes are the wrong audio file, or a tab in a different key or with a different structure.", true, false,
            Enumerable.Range(0, 39).Select(i => (IAlignSectionRow)new Row($"Bars {i * 8 + 1}–{i * 8 + 8}", 0.1 + 0.01 * (i % 9),
                $"{10 + i % 9}%", true, $"±{40 + 3 * i} ms")).ToList(),
            "0 of 39 sections will move. Listened to the full mix.", weak: true);

    /// <summary>The longest section names and the German and Japanese summary.</summary>
    public static FakeAlignToRecordingScreen LongText() =>
        new("Ein sehr langer Dateiname für die Aufnahme des Liedes (Live-Version, remastered 2024).flac", "", false, false,
        [
            new Row("Zwischenspiel mit Gitarrensolo · Takte 101–148", 0.77, "77 %", false, "±12345 ms"),
            new Row("ギターソロのある間奏 · 小節 149–196", 0.12, "12 %", true, "±23456 ms"),
            Row.Silent("Zwischenspiel ohne Gitarre, nur Schlagzeug und Gesang · Takte 197–244", "keine Noten: folgt den Nachbarn")
        ], "1 von 2 Abschnitten wird verschoben. Gehört wurden die Instrumenten-Stems. 2 セクション中 1 つが移動します。");

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

    public bool IsWeakMatch { get; }

    public bool ShowSections
    {
        get => _showSections;
        set => SetProperty(ref _showSections, value && Sections.Count > 0);
    }

    public string SectionsToggleText => $"Show the sections ({Sections.Count})";

    public IReadOnlyList<IAlignSectionRow> Sections { get; }

    public string SummaryText { get; }

    public ICommand AnalyzeCommand { get; }

    public ICommand ApplyCommand { get; }

    public ICommand CancelCommand { get; }

    private sealed class Row(string title, double confidence, string confidenceText, bool isLow, string shift, string skipReason = "")
        : ObservableObject, IAlignSectionRow
    {
        private bool _isApplied = !isLow && skipReason.Length == 0;

        public static Row Silent(string title, string reason) => new(title, 0, "", false, "", reason);

        public string Title => title;

        public double Confidence => confidence;

        public string ConfidenceText => confidenceText;

        public bool IsLow => isLow;

        public string ShiftText => shift;

        public bool IsApplied
        {
            get => _isApplied;
            set => SetProperty(ref _isApplied, value && CanApply);
        }

        public bool CanApply => skipReason.Length == 0;

        public string SkipReason => skipReason;
    }
}
