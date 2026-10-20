using System.Windows.Input;
using DotCharter.Presentation.Contracts.Dialogs;

namespace DotCharter.Presentation.Fakes.Dialogs;

/// <summary>A services-free <see cref="ILibraryDoctorScreen" />; Close answers true.</summary>
public sealed class FakeLibraryDoctorScreen : FakeDialog<bool>, ILibraryDoctorScreen
{
    private FakeLibraryDoctorScreen(IReadOnlyList<ILibraryDoctorGroup> groups, string summary, bool isRunning = false,
        double progress = 0)
    {
        Groups = groups;
        Summary = summary;
        IsRunning = isRunning;
        Progress = progress;
        RunCommand = isRunning ? Disabled() : Answer(true);
        CloseCommand = Answer(true);
    }

    public static IReadOnlyList<FakeVariant<ILibraryDoctorScreen>> Variants { get; } =
    [
        new(FakeVariantNames.Default, Default),
        new(FakeVariantNames.Empty, Empty),
        new(FakeVariantNames.LongText, LongText),
        new(FakeVariantNames.Full, Full),
        new(FakeVariantNames.Busy, Busy)
    ];

    public static FakeLibraryDoctorScreen Default() => new(
    [
        new Group("Duplicate packages", 2, false, true,
        [
            new Issue("Muse — Hysteria", "hysteria_p.psarc", "Also in hysteria_v2_p.psarc"),
            new Issue("Tool — Schism", "schism_p.psarc", "Also in schism_rs1_p.psarc")
        ]),
        new Group("Unreadable packages", 1, true, false,
            [new Issue("(unknown)", "broken_p.psarc", "The package header is truncated.")])
    ], "3 issues across 2,160 songs in 1,904 packages.");

    /// <summary>A healthy library: the clean bill of health is the content.</summary>
    public static FakeLibraryDoctorScreen Empty() => new([], "No issues across 2,160 songs.");

    public static FakeLibraryDoctorScreen LongText() => new(
    [
        new Group("Doppelte Pakete (gleicher Song-Schlüssel in mehreren Dateien gefunden)", 1, false, true,
        [
            new Issue("Die Ärzte — Schrei nach Liebe (Live-Version mit alternativer Stimmung)",
                "die_aerzte_schrei_nach_liebe_live_alternative_tuning_p.psarc",
                "同じソングキーを持つ別のパッケージがライブラリ内に見つかりました。")
        ])
    ], "1 Problem in 2.160 Songs aus 1.904 Paketen gefunden.");

    /// <summary>Every kind of finding, each with a screenful of rows: the list scrolls.</summary>
    public static FakeLibraryDoctorScreen Full() => new(
    [
        .. new[] { "Duplicate packages", "Missing audio", "Invalid tuning", "Unknown tuning", "No arrangements", "Unreadable packages" }
            .Select(ILibraryDoctorGroup (title, g) => new Group(title, 6, g % 2 == 1, g % 2 == 0,
            [
                .. Enumerable.Range(1, 6).Select(ILibraryDoctorIssue (n) =>
                    new Issue($"Artist {n} — Song {g * 6 + n}", $"song_{g * 6 + n}_p.psarc", "Detail of the finding."))
            ]))
    ], "36 issues across 2,160 songs in 1,904 packages.");

    /// <summary>A check in flight: the bar moves and Run is off.</summary>
    public static FakeLibraryDoctorScreen Busy() => new([], "Scanning the library…", true, 0.42);

    public IReadOnlyList<ILibraryDoctorGroup> Groups { get; }

    public bool HasIssues => Groups.Count > 0;

    public bool IsRunning { get; }

    public double Progress { get; }

    public string Summary { get; }

    public ICommand RunCommand { get; }

    public ICommand CloseCommand { get; }

    private sealed record Group(string Title, int Count, bool IsError, bool IsWarning,
        IReadOnlyList<ILibraryDoctorIssue> Issues) : ILibraryDoctorGroup;

    private sealed record Issue(string SongDisplay, string FileName, string Detail) : ILibraryDoctorIssue;
}
