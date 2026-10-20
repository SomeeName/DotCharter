using System.Windows.Input;
using DotCharter.Presentation.Contracts.Dialogs;

namespace DotCharter.Presentation.Fakes.Dialogs;

/// <summary>A services-free <see cref="IRsXmlImportScreen" />; Close answers true.</summary>
public sealed class FakeRsXmlImportScreen : FakeDialog<bool>, IRsXmlImportScreen
{
    private FakeRsXmlImportScreen(string fileName, bool busy, string error)
    {
        FileName = fileName;
        IsBusy = busy;
        ErrorMessage = error;
        CloseCommand = Answer(true);
    }

    public static IReadOnlyList<FakeVariant<IRsXmlImportScreen>> Variants { get; } =
    [
        new(FakeVariantNames.Default, Default),
        new(FakeVariantNames.Empty, Empty),
        new(FakeVariantNames.LongText, LongText),
        new(FakeVariantNames.Busy, Busy)
    ];

    /// <summary>The state a user actually sees for longer than a moment: a failed merge with its reason.</summary>
    public static FakeRsXmlImportScreen Default() =>
        new("lead_arrangement.xml", false, "Import failed: the root element <nonsense> is not a song, vocals or showlights document.");

    public static FakeRsXmlImportScreen Empty() => new("", false, "");

    public static FakeRsXmlImportScreen LongText() =>
        new("Sehr_langer_Dateiname_mit_Leadgitarre_und_alternativer_Stimmung_ボーカル.xml", false,
            "Import fehlgeschlagen: Das Wurzelelement ist kein Song-, Gesangs- oder Showlight-Dokument; " +
            "ファイルを確認してください。");

    public static FakeRsXmlImportScreen Busy() => new("lead_arrangement.xml", true, "");

    public string FileName { get; }

    public bool IsBusy { get; }

    public string ErrorMessage { get; }

    public ICommand CloseCommand { get; }
}
