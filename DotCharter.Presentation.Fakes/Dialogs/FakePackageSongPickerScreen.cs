using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.Dialogs;

namespace DotCharter.Presentation.Fakes.Dialogs;

/// <summary>A services-free <see cref="IPackageSongPickerScreen" />; Cancel answers null, Open answers the row's index.</summary>
public sealed class FakePackageSongPickerScreen : FakeDialog<int?>, IPackageSongPickerScreen
{
    private IPackageSongRow? _selected;

    private FakePackageSongPickerScreen(string packageName, IReadOnlyList<IPackageSongRow> rows)
    {
        PackageName = packageName;
        Songs = rows;
        _selected = rows.FirstOrDefault();
        OpenCommand = new RelayCommand(() =>
        {
            if (Selected is not null) Complete(rows.ToList().IndexOf(Selected));
        });
        CancelCommand = Answer(null);
    }

    public static IReadOnlyList<FakeVariant<IPackageSongPickerScreen>> Variants { get; } =
    [
        new(FakeVariantNames.Default, Default),
        new(FakeVariantNames.Empty, Empty),
        new(FakeVariantNames.LongText, LongText),
        new(FakeVariantNames.Full, Full)
    ];

    public static FakePackageSongPickerScreen Default() => new("pack_p.psarc",
    [
        new Row("Hysteria", "Muse · 3:47"),
        new Row("Starlight", "Muse · 4:00"),
        new Row("Time Is Running Out", "Muse · 3:56")
    ]);

    /// <summary>The picker is only opened for two or more songs; this is the floor.</summary>
    public static FakePackageSongPickerScreen Empty() => new("pack_p.psarc",
        [new Row("Hysteria", ""), new Row("Starlight", "")]);

    public static FakePackageSongPickerScreen LongText() => new(
        "ein_sehr_langer_paketname_mit_vielen_liedern_aus_der_gemeinschaft_p.psarc",
    [
        new Row("Schrei nach Liebe (Live-Version mit alternativer Stimmung und langem Titel)", "Die Ärzte · 3:47"),
        new Row("サビのコードをすべて確認した後に演奏する長いタイトルの曲", "リズムギター · 4:00")
    ]);

    /// <summary>A big compilation: the list scrolls.</summary>
    public static FakePackageSongPickerScreen Full() => new("compilation_p.psarc",
    [
        .. Enumerable.Range(1, 30).Select(IPackageSongRow (i) => new Row($"Song number {i}", $"Band {i} · {2 + i % 4}:{i * 7 % 60:00}"))
    ]);

    public string PackageName { get; }

    public IReadOnlyList<IPackageSongRow> Songs { get; }

    public IPackageSongRow? Selected
    {
        get => _selected;
        set => SetProperty(ref _selected, value);
    }

    public ICommand OpenCommand { get; }

    public ICommand CancelCommand { get; }

    private void Complete(int index) => Answer(index).Execute(null);

    private sealed class Row(string title, string detail) : IPackageSongRow
    {
        public string Title { get; } = title;

        public string Detail { get; } = detail;

        public override string ToString() => Title;
    }
}
