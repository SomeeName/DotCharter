using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.Dialogs;

namespace DotCharter.Presentation.Fakes.Dialogs;

/// <summary>A services-free <see cref="ICheckpointsScreen" />; Close answers true, the row verbs do nothing.</summary>
public sealed class FakeCheckpointsScreen : FakeDialog<bool>, ICheckpointsScreen
{
    private string _name;

    private FakeCheckpointsScreen(IReadOnlyList<ICheckpointRow> rows, string name = "", bool isBusy = false)
    {
        Checkpoints = rows;
        _name = name;
        IsBusy = isBusy;
        CreateCheckpointCommand = isBusy ? Disabled() : new RelayCommand(() => { });
        RestoreCommand = new RelayCommand<ICheckpointRow>(_ => { });
        DeleteCommand = new RelayCommand<ICheckpointRow>(_ => { });
        CloseCommand = Answer(true);
    }

    public static IReadOnlyList<FakeVariant<ICheckpointsScreen>> Variants { get; } =
    [
        new(FakeVariantNames.Default, Default),
        new(FakeVariantNames.Empty, Empty),
        new(FakeVariantNames.LongText, LongText),
        new(FakeVariantNames.Full, Full),
        new(FakeVariantNames.Busy, Busy)
    ];

    /// <summary>Three checkpoints, the middle one armed for delete.</summary>
    public static FakeCheckpointsScreen Default() => new(
    [
        new Row("before autofix", "Hysteria · 22/09/2026 18:40", "Delete"),
        new Row("verse done", "Hysteria · 22/09/2026 19:12", "Click again to delete"),
        new Row("solo charted", "Hysteria · 22/09/2026 20:05", "Delete")
    ], "chorus");

    /// <summary>What the dialog shows the first time anyone opens it.</summary>
    public static FakeCheckpointsScreen Empty() => new([]);

    public static FakeCheckpointsScreen LongText() => new(
    [
        new Row("Vor der automatischen Korrektur aller Griffhandpositionen im zweiten Refrain",
            "Schrei nach Liebe (Live-Version mit alternativer Stimmung) · 22.09.2026 18:40", "Löschen"),
        new Row("サビのコードをすべて確認した後のチェックポイント", "リズムギター · 2026/09/22 19:12", "もう一度クリックして削除")
    ], "Nach dem Solo");

    /// <summary>A long-lived project: the list scrolls.</summary>
    public static FakeCheckpointsScreen Full() => new(
    [
        .. Enumerable.Range(1, 30).Select(ICheckpointRow (i) =>
            new Row($"checkpoint {i}", $"Hysteria · 22/09/2026 {8 + i % 12:00}:{i * 7 % 60:00}", "Delete"))
    ]);

    /// <summary>A checkpoint being written: every verb is off.</summary>
    public static FakeCheckpointsScreen Busy() => new(
        [new Row("before autofix", "Hysteria · 22/09/2026 18:40", "Delete")], "writing…", isBusy: true);

    public string NewCheckpointName
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public bool IsBusy { get; }

    public ICommand CreateCheckpointCommand { get; }

    public bool HasCheckpoints => Checkpoints.Count > 0;

    public IReadOnlyList<ICheckpointRow> Checkpoints { get; }

    public ICommand RestoreCommand { get; }

    public ICommand DeleteCommand { get; }

    public ICommand CloseCommand { get; }

    private sealed class Row(string name, string detail, string deleteCaption) : ObservableObject, ICheckpointRow
    {
        public string Name { get; } = name;

        public string Detail { get; } = detail;

        public string DeleteCaption { get; } = deleteCaption;
    }
}
