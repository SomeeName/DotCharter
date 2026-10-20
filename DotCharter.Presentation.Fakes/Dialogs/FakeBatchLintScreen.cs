using System.Windows.Input;
using DotCharter.Presentation.Contracts.Dialogs;

namespace DotCharter.Presentation.Fakes.Dialogs;

/// <summary>A services-free <see cref="IBatchLintScreen" />; Run answers true, Cancel false.</summary>
public sealed class FakeBatchLintScreen : FakeDialog<bool>, IBatchLintScreen
{
    private bool _applyAutoFixes;
    private bool _saveFixedProjects;

    private FakeBatchLintScreen(string folder, IReadOnlyList<ILintResultRow> results, string status,
        bool applyFixes = false, bool isBusy = false, double progress = 0, string currentFile = "")
    {
        FolderPath = folder;
        Results = results;
        Status = status;
        _applyAutoFixes = applyFixes;
        IsBusy = isBusy;
        Progress = progress;
        CurrentFile = currentFile;
        BrowseFolderCommand = Disabled();
        RunCommand = isBusy ? Disabled() : Answer(true);
        CancelCommand = Answer(false);
    }

    public static IReadOnlyList<FakeVariant<IBatchLintScreen>> Variants { get; } =
    [
        new(FakeVariantNames.Default, Default),
        new(FakeVariantNames.Empty, Empty),
        new(FakeVariantNames.LongText, LongText),
        new(FakeVariantNames.Full, Full),
        new(FakeVariantNames.Busy, Busy)
    ];

    public static FakeBatchLintScreen Default() => new(@"D:\Charts\Muse",
    [
        new Row("hysteria.rscp", @"D:\Charts\Muse", "3 issues, 2 fixed", false, true),
        new Row("plug_in_baby.rscp", @"D:\Charts\Muse", "0 issues", false, false),
        new Row("knights_of_cydonia.rscp", @"D:\Charts\Muse", "The file is not a DotCharter project.", true, false)
    ], "3 files · 3 issues · 2 fixes · 1 saved · 1 failed", applyFixes: true);

    /// <summary>Nothing picked and nothing run: the first open.</summary>
    public static FakeBatchLintScreen Empty() => new("", [], "");

    public static FakeBatchLintScreen LongText() => new(
        @"D:\Charts\Die Ärzte – Liveaufnahmen mit alternativer Stimmung\Zweite Fassung",
        [
            new Row("schrei_nach_liebe_live_alternative_stimmung.rscp",
                @"D:\Charts\Die Ärzte – Liveaufnahmen mit alternativer Stimmung\Zweite Fassung",
                "12 Probleme, 9 automatisch behoben — ungültige Griffhandpositionen wurden neu gesetzt", false, false),
            new Row("リズムギター_ドロップD.rscp", @"D:\Charts\日本語のフォルダ", "プロジェクトを読み込めませんでした。", true, false)
        ], "2 Dateien · 12 Probleme · 9 Korrekturen · 0 gespeichert · 1 fehlgeschlagen");

    /// <summary>A library-sized folder: the list scrolls.</summary>
    public static FakeBatchLintScreen Full() => new(@"D:\Rocksmith\Customs",
    [
        .. Enumerable.Range(1, 50).Select(ILintResultRow (i) =>
            new Row($"custom_song_{i:000}.rscp", @"D:\Rocksmith\Customs", $"{i % 7} {(i % 7 == 1 ? "issue" : "issues")}", i % 13 == 0, false))
    ], "50 files · 150 issues · 0 fixes · 0 saved · 3 failed");

    /// <summary>A run in flight: the bar, the current file and a live Cancel.</summary>
    public static FakeBatchLintScreen Busy() => new(@"D:\Rocksmith\Customs", [], "Linting 400 projects…",
        applyFixes: true, isBusy: true, progress: 0.37, currentFile: "custom_song_148.rscp");

    public string FolderPath { get; }

    public ICommand BrowseFolderCommand { get; }

    public bool ApplyAutoFixes
    {
        get => _applyAutoFixes;
        set
        {
            if (SetProperty(ref _applyAutoFixes, value)) OnPropertyChanged(nameof(CanSaveFixedProjects));
        }
    }

    public bool SaveFixedProjects
    {
        get => _saveFixedProjects;
        set => SetProperty(ref _saveFixedProjects, value);
    }

    public bool CanSaveFixedProjects => _applyAutoFixes;

    public bool HasResults => Results.Count > 0;

    public IReadOnlyList<ILintResultRow> Results { get; }

    public bool IsBusy { get; }

    public double Progress { get; }

    public string CurrentFile { get; }

    public string Status { get; }

    public ICommand RunCommand { get; }

    public ICommand CancelCommand { get; }

    private sealed record Row(string FileName, string Folder, string Detail, bool Failed, bool Saved) : ILintResultRow;
}
