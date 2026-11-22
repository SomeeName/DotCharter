using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.Dialogs;

namespace DotCharter.Presentation.Fakes.Dialogs;

/// <summary>A services-free <see cref="IBookmarksScreen" />; Close answers true, the row verbs do nothing.</summary>
public sealed class FakeBookmarksScreen : FakeDialog<bool>, IBookmarksScreen
{
    private const string Hint = "Each slot's key sets, jumps to or clears a bookmark at the playhead.";

    private FakeBookmarksScreen(string notSet, string hint, params string[] times)
    {
        Slots =
        [
            .. Enumerable.Range(0, 10).Select(IBookmarkRow (i) => i < times.Length
                ? new Row(i, times[i], true)
                : new Row(i, notSet, false))
        ];
        ShortcutHint = hint;
        CloseCommand = Answer(true);
    }

    public static IReadOnlyList<FakeVariant<IBookmarksScreen>> Variants { get; } =
    [
        new(FakeVariantNames.Default, Default),
        new(FakeVariantNames.Empty, Empty),
        new(FakeVariantNames.LongText, LongText),
        new(FakeVariantNames.Full, Full)
    ];

    /// <summary>Four slots set, six free.</summary>
    public static FakeBookmarksScreen Default() => new("Not set", Hint, "0:00.000", "0:01.500", "1:02.250", "2:05.900");

    public static FakeBookmarksScreen Empty() => new("Not set", Hint);

    public static FakeBookmarksScreen LongText() => new("Nicht gesetzt / 未設定",
        "Umschalt+0 bis 9 setzt, springt zu oder löscht ein Lesezeichen an der Abspielposition. 再生位置のブックマークを設定、移動、削除します。",
        "60:00.123", "0:00.001");

    /// <summary>Every slot set.</summary>
    public static FakeBookmarksScreen Full() => new("Not set", Hint, [.. Enumerable.Range(0, 10).Select(i => $"{i}:{i * 7 % 60:00}.{i * 113 % 1000:000}")]);

    public IReadOnlyList<IBookmarkRow> Slots { get; }

    public string ShortcutHint { get; }

    public ICommand CloseCommand { get; }

    private sealed class Row(int slot, string timeText, bool isSet) : ObservableObject, IBookmarkRow
    {
        public string Slot { get; } = slot.ToString();

        public string Chord { get; } = "Shift+" + slot;

        public string TimeText { get; } = timeText;

        public ICommand GoToCommand { get; } = new RelayCommand(() => { }, () => isSet);

        public ICommand SetCommand { get; } = new RelayCommand(() => { });

        public ICommand ClearCommand { get; } = new RelayCommand(() => { }, () => isSet);
    }
}
