using System.Globalization;
using System.Windows.Input;
using DotCharter.Presentation.Contracts.Dialogs;

namespace DotCharter.Presentation.Fakes.Dialogs;

/// <summary>A services-free <see cref="IAiToneGenerateScreen" />; Generate answers its text, Cancel null.</summary>
public sealed class FakeAiToneGenerateScreen : FakeDialog<string?>, IAiToneGenerateScreen
{
    private const int Cap = 500;

    private FakeAiToneGenerateScreen(string notes)
    {
        Notes = notes;
        GenerateCommand = Answer(notes);
        CancelCommand = Answer(null);
    }

    public static IReadOnlyList<FakeVariant<IAiToneGenerateScreen>> Variants { get; } =
    [
        new(FakeVariantNames.Default, Default),
        new(FakeVariantNames.Empty, Empty),
        new(FakeVariantNames.LongText, LongText)
    ];

    public static FakeAiToneGenerateScreen Default() =>
        new("The verse is a cranked Marshall, the chorus adds a chorus pedal, the solo a wah.");

    public static FakeAiToneGenerateScreen Empty() => new("");

    /// <summary>German and Japanese text at the cap.</summary>
    public static FakeAiToneGenerateScreen LongText() =>
        new(string.Concat(Enumerable.Repeat("Der Refrain klingt nach einem übersteuerten Röhrenverstärker. 歪んだアンプ。 ", 10))[..Cap]);

    public string Notes { get; set; }

    public int MaxLength => Cap;

    public string LengthText => string.Create(CultureInfo.InvariantCulture, $"{Notes.Length} / {Cap}");

    public ICommand GenerateCommand { get; }

    public ICommand CancelCommand { get; }
}
