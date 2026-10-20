using System.Windows.Input;
using DotCharter.Presentation.Contracts.Dialogs;

namespace DotCharter.Presentation.Fakes.Dialogs;

/// <summary>A services-free <see cref="IKeyboardShortcutsScreen" />; Close answers true.</summary>
public sealed class FakeKeyboardShortcutsScreen : FakeDialog<bool>, IKeyboardShortcutsScreen
{
    private string _filterText;

    private FakeKeyboardShortcutsScreen(string filter, IReadOnlyList<IShortcutGroup> groups)
    {
        _filterText = filter;
        Groups = groups;
        CloseCommand = Answer(true);
    }

    public static IReadOnlyList<FakeVariant<IKeyboardShortcutsScreen>> Variants { get; } =
    [
        new(FakeVariantNames.Default, Default),
        new(FakeVariantNames.Empty, Empty),
        new(FakeVariantNames.LongText, LongText),
        new(FakeVariantNames.Full, Full)
    ];

    public static FakeKeyboardShortcutsScreen Default() => new("",
    [
        new Group("File", [new Row("Save project", "Ctrl+S"), new Row("Open project", "Ctrl+O")]),
        new Group("Transport", [new Row("Play / pause", "Space"), new Row("Metronome", "M")]),
        new Group("Edit", [new Row("Paste special", "Ctrl+Shift+V"), new Row("Undo", "Ctrl+Z")])
    ]);

    /// <summary>A filter that matches nothing: the empty line shows instead of the list.</summary>
    public static FakeKeyboardShortcutsScreen Empty() => new("zzz", []);

    public static FakeKeyboardShortcutsScreen LongText() => new("",
    [
        new Group("Bearbeiten (Griffbrett und Notenlinien)",
        [
            new Row("Ausgewählte Noten in Powerchords umwandeln (große Form, mit Oktave)", "Strg+Umschalt+Alt+P"),
            new Row("選択したコードを単音に分割してタイムラインに配置する", "Ctrl+Shift+Alt+K")
        ])
    ]);

    /// <summary>Every category the palette has, each with a screenful of rows: the list scrolls.</summary>
    public static FakeKeyboardShortcutsScreen Full() => new("",
    [
        .. new[] { "File", "Edit", "Transport", "View", "Tempo", "Arrangement", "Vocals", "Tools", "Help" }
            .Select(IShortcutGroup (category, i) => new Group(category,
            [
                .. Enumerable.Range(1, 8).Select(IShortcutRow (n) =>
                    new Row($"{category} command {n}", $"Ctrl+Shift+{(char)('A' + (i + n) % 26)}"))
            ]))
    ]);

    public string FilterText
    {
        get => _filterText;
        set => SetProperty(ref _filterText, value);
    }

    public IReadOnlyList<IShortcutGroup> Groups { get; }

    public bool HasNoMatches => Groups.Count == 0;

    public ICommand CloseCommand { get; }

    private sealed record Group(string Name, IReadOnlyList<IShortcutRow> Rows) : IShortcutGroup;

    private sealed record Row(string Title, string Chord) : IShortcutRow;
}
