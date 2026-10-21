using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.Dialogs;

namespace DotCharter.Presentation.Fakes.Dialogs;

/// <summary>A services-free <see cref="ILinkGroupsScreen" />; Close answers true, the row verbs do nothing.</summary>
public sealed class FakeLinkGroupsScreen : FakeDialog<bool>, ILinkGroupsScreen
{
    private FakeLinkGroupsScreen(IReadOnlyList<ILinkGroupRow> groups, IReadOnlyList<ILinkSuggestionRow> suggestions)
    {
        Groups = groups;
        Suggestions = suggestions;
        RenameCommand = new RelayCommand<ILinkGroupRow>(_ => { });
        DeleteCommand = new RelayCommand<ILinkGroupRow>(_ => { });
        GoToCommand = new RelayCommand<ILinkPlacementRow>(_ => { });
        LinkSelectedCommand = suggestions.Count > 0 ? new RelayCommand(() => { }) : Disabled();
        CloseCommand = Answer(true);
    }

    public static IReadOnlyList<FakeVariant<ILinkGroupsScreen>> Variants { get; } =
    [
        new(FakeVariantNames.Default, Default),
        new(FakeVariantNames.Empty, Empty),
        new(FakeVariantNames.LongText, LongText),
        new(FakeVariantNames.Full, Full)
    ];

    /// <summary>Two groups and, as after an import's Review, two suggestions.</summary>
    public static FakeLinkGroupsScreen Default() => new(
    [
        Group("A", "Chorus", "Lead ×3, Rhythm ×3", "Lead · bars 17–24", "Lead · bars 41–48", "Lead · bars 65–72",
            "Rhythm · bars 17–24", "Rhythm · bars 41–48", "Rhythm · bars 65–72"),
        Group("B", "Verse riff", "Lead ×2", "Lead · bars 5–8", "Lead · bars 29–32")
    ],
    [
        new Suggestion("Intro · 4 measures", "Lead ×2, Rhythm ×2 · bars 1–4, 81–84 · from the score's repeats", true),
        new Suggestion("Bridge · 1 measure", "Rhythm ×4 · bar 53, bar 54, bar 55, bar 56", false)
    ]);

    /// <summary>What the dialog shows before anything is linked.</summary>
    public static FakeLinkGroupsScreen Empty() => new([], []);

    public static FakeLinkGroupsScreen LongText() => new(
    [
        Group("A", "Refrain mit Zwischenspiel und Wiederholung", "Leadgitarre (alternative Stimmung) ×3, Rhythmusgitarre ×3",
            "Leadgitarre (alternative Stimmung) · Takte 17–24", "Rhythmusgitarre · Takte 17–24"),
        Group("B", "サビのリフ", "リード ×2", "リード · 小節 5–8", "リード · 小節 29–32")
    ],
    [
        new Suggestion("Zwischenspiel nach dem zweiten Refrain · 12 Takte",
            "Leadgitarre (alternative Stimmung) ×2, Rhythmusgitarre ×2 · Takte 57–68, 93–104 · aus den Wiederholungen der Partitur", true)
    ]);

    /// <summary>A song linked throughout: the list scrolls.</summary>
    public static FakeLinkGroupsScreen Full() => new(
    [
        .. Enumerable.Range(0, 14).Select(i => Group(((char)('A' + i)).ToString(), $"Section {i + 1}", "Lead ×2, Rhythm ×2",
            $"Lead · bars {i * 4 + 1}–{i * 4 + 4}", $"Lead · bars {i * 4 + 61}–{i * 4 + 64}",
            $"Rhythm · bars {i * 4 + 1}–{i * 4 + 4}", $"Rhythm · bars {i * 4 + 61}–{i * 4 + 64}"))
    ],
    [
        .. Enumerable.Range(1, 8).Select(ILinkSuggestionRow (i) => new Suggestion($"Riff {i} · 2 measures", $"Lead ×2 · bars {i * 2}–{i * 2 + 1}", i % 2 == 0))
    ]);

    public bool HasGroups => Groups.Count > 0;

    public IReadOnlyList<ILinkGroupRow> Groups { get; }

    public ICommand RenameCommand { get; }

    public ICommand DeleteCommand { get; }

    public ICommand GoToCommand { get; }

    public bool HasSuggestions => Suggestions.Count > 0;

    public IReadOnlyList<ILinkSuggestionRow> Suggestions { get; }

    public ICommand LinkSelectedCommand { get; }

    public ICommand CloseCommand { get; }

    private static GroupRow Group(string letter, string name, string summary, params string[] placements) =>
        new(letter, name, summary, [.. placements.Select(ILinkPlacementRow (p) => new PlacementRow(p))]);

    private sealed class GroupRow(string letter, string name, string summary, IReadOnlyList<ILinkPlacementRow> placements)
        : ObservableObject, ILinkGroupRow
    {
        private string _name = name;

        public string Letter { get; } = letter;

        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }

        public string Summary { get; } = summary;

        public IReadOnlyList<ILinkPlacementRow> Placements { get; } = placements;
    }

    private sealed record PlacementRow(string Label) : ILinkPlacementRow;

    private sealed class Suggestion(string title, string detail, bool isSelected) : ObservableObject, ILinkSuggestionRow
    {
        private bool _isSelected = isSelected;

        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }

        public string Title { get; } = title;

        public string Detail { get; } = detail;
    }
}
