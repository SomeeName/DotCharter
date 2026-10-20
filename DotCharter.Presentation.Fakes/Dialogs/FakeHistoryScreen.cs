using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.Dialogs;

namespace DotCharter.Presentation.Fakes.Dialogs;

/// <summary>A services-free <see cref="IHistoryScreen" />; Close answers true, a jump does nothing.</summary>
public sealed class FakeHistoryScreen : FakeDialog<bool>, IHistoryScreen
{
    private FakeHistoryScreen(IReadOnlyList<IHistoryNode> nodes, string summary)
    {
        Nodes = nodes;
        Summary = summary;
        JumpToCommand = new RelayCommand<IHistoryNode>(_ => { });
        CloseCommand = Answer(true);
    }

    public static IReadOnlyList<FakeVariant<IHistoryScreen>> Variants { get; } =
    [
        new(FakeVariantNames.Default, Default),
        new(FakeVariantNames.Empty, Empty),
        new(FakeVariantNames.LongText, LongText),
        new(FakeVariantNames.Full, Full)
    ];

    /// <summary>A short session with one abandoned branch: a current row and a branch row both show.</summary>
    public static FakeHistoryScreen Default() => new(
    [
        new Node("Initial state", "18:40:02", 0, false, false),
        new Node("Add elements", "18:41:15", 0, false, false),
        new Node("Upsert tone definition", "18:45:35", 16, false, true),
        new Node("Mutate beats", "18:46:32", 0, true, false)
    ], "4 states · 1 branch");

    public static FakeHistoryScreen Empty() => new([], "0 states · no branches");

    public static FakeHistoryScreen LongText() => new(
    [
        new Node("Ausgangszustand", "18:40:02", 0, false, false),
        new Node("Griffhandpositionen über alle Schwierigkeitsstufen hinweg neu verteilen", "18:41:15", 0, false, true),
        new Node("選択したコードをタイムライン上のすべての難易度レベルに伝播する", "18:46:32", 0, true, false)
    ], "3 Zustände · 1 Verzweigung");

    /// <summary>A long session with nested branches: the list scrolls and the insets step in.</summary>
    public static FakeHistoryScreen Full() => new(
    [
        .. Enumerable.Range(0, 40).Select(IHistoryNode (i) => new Node(
            i == 0 ? "Initial state" : $"Edit step {i}", $"18:{i % 60:00}:{i * 7 % 60:00}",
            i % 9 == 4 ? 16 : i % 9 == 5 ? 32 : 0, i == 39, i % 9 is 4 or 5))
    ], "40 states · 8 branches");

    public IReadOnlyList<IHistoryNode> Nodes { get; }

    public bool HasNodes => Nodes.Count > 0;

    public string Summary { get; }

    public ICommand JumpToCommand { get; }

    public ICommand CloseCommand { get; }

    private sealed record Node(string Description, string Time, double Indent, bool IsCurrent, bool IsBranch)
        : IHistoryNode;
}
