using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.Dialogs;

namespace DotCharter.Presentation.Fakes.Dialogs;

/// <summary>A services-free <see cref="IRecoveryScreen" />; Restore answers true, Ask later false.</summary>
public sealed class FakeRecoveryScreen : FakeDialog<bool>, IRecoveryScreen
{
    private IRecoverySession? _selected;

    private FakeRecoveryScreen(string subtitle, IReadOnlyList<IRecoverySession> sessions, bool busy = false)
    {
        Subtitle = subtitle;
        Sessions = sessions;
        _selected = sessions.FirstOrDefault();
        IsBusy = busy;
        // Busy: a restore is running, so every verb waits for it.
        RecoverSelectedCommand = busy ? Disabled() : Answer(true);
        DiscardCommand = new RelayCommand<IRecoverySession>(_ => { }, _ => !busy);
        DiscardAllCommand = busy ? Disabled() : Answer(true);
        AskLaterCommand = busy ? Disabled() : Answer(false);
    }

    public static IReadOnlyList<FakeVariant<IRecoveryScreen>> Variants { get; } =
    [
        new(FakeVariantNames.Default, Default),
        new(FakeVariantNames.Empty, Empty),
        new(FakeVariantNames.LongText, LongText),
        new(FakeVariantNames.Full, Full),
        new(FakeVariantNames.Busy, Busy)
    ];

    /// <summary>One saved project and one that was never saved.</summary>
    public static FakeRecoveryScreen Default() => new("2 snapshots were found",
    [
        new Row("Hysteria", false, "Project", @"C:\Songs\Muse\Hysteria.rscp", "22.09.2026 14:31"),
        new Row("Unsaved project — Plug In Baby", true, "Never saved", "There is no project file yet",
            "22.09.2026 14:02")
    ]);

    /// <summary>Every session handled: nothing left to show.</summary>
    public static FakeRecoveryScreen Empty() => new("0 snapshots were found", []);

    public static FakeRecoveryScreen LongText() => new("1 Schnappschuss wurde gefunden",
    [
        new Row("Ungespeichertes Projekt — ラルク・アン・シエル（リマスター・スペシャル・エディション）", true, "Nie gespeichert",
            "Für diese Sitzung gibt es noch keine Projektdatei auf dem Datenträger",
            "22.09.2026 14:31")
    ]);

    /// <summary>Several crashes' worth of snapshots: the list scrolls.</summary>
    public static FakeRecoveryScreen Full() => new("9 snapshots were found",
    [
        .. Enumerable.Range(1, 9).Select(IRecoverySession (i) => new Row($"Song {i}", i % 3 == 0,
            i % 3 == 0 ? "Never saved" : "Project",
            i % 3 == 0 ? "There is no project file yet" : $@"D:\Charts\Band {i}\Song {i}.rscp",
            $"2{i}.09.2026 1{i}:0{i}"))
    ]);

    /// <summary>A restore in flight: every action waits.</summary>
    public static FakeRecoveryScreen Busy() => new("1 snapshot was found",
        [new Row("Hysteria", false, "Project", @"C:\Songs\Muse\Hysteria.rscp", "22.09.2026 14:31")],
        busy: true);

    public string Subtitle { get; }

    public IReadOnlyList<IRecoverySession> Sessions { get; }

    public IRecoverySession? SelectedSession
    {
        get => _selected;
        set => SetProperty(ref _selected, value);
    }

    public bool IsBusy { get; }

    public ICommand RecoverSelectedCommand { get; }

    public ICommand DiscardCommand { get; }

    public ICommand DiscardAllCommand { get; }

    public ICommand AskLaterCommand { get; }

    private sealed record Row(string DisplayName, bool IsUntitled, string KindLabel, string PathLine, string SavedAt)
        : IRecoverySession;
}
