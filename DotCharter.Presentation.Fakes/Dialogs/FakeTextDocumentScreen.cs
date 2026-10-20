using System.Text;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.Dialogs;

namespace DotCharter.Presentation.Fakes.Dialogs;

/// <summary>A services-free <see cref="ITextDocumentScreen" />; Close answers true, Copy and Show do nothing.</summary>
public sealed class FakeTextDocumentScreen : FakeDialog<bool>, ITextDocumentScreen
{
    private FakeTextDocumentScreen(string title, string subtitle, string text, bool canShowFile, string emptyText = "")
    {
        Title = title;
        Subtitle = subtitle;
        Text = text;
        CanShowFile = canShowFile;
        EmptyText = emptyText;
        CopyCommand = new RelayCommand(() => { });
        ShowFileCommand = new RelayCommand(() => { });
        CloseCommand = Answer(true);
    }

    public static IReadOnlyList<FakeVariant<ITextDocumentScreen>> Variants { get; } =
    [
        new(FakeVariantNames.Default, Default),
        new(FakeVariantNames.Empty, Empty),
        new(FakeVariantNames.LongText, LongText),
        new(FakeVariantNames.Full, Full),
        new("License", License)
    ];

    private const string UsageTitle = "Usage statistics";
    private const string UsageSubtitle = "Counted on this computer only; nothing is uploaded.";

    public static FakeTextDocumentScreen Default() => new(UsageTitle, UsageSubtitle, UsageReport(12), true);

    /// <summary>Nothing counted yet, and no file written.</summary>
    public static FakeTextDocumentScreen Empty() =>
        new(UsageTitle, UsageSubtitle, "", false, "Nothing has been counted yet.");

    public static FakeTextDocumentScreen LongText() => new(
        "Lizenz und Hinweise zu Drittanbietern, die DotCharter verwendet",
        "使用状況の統計はこのコンピューター上でのみ集計され、何もアップロードされません。",
        "=== DotCharter Nutzungsstatistik ===\n" +
        "Zählungen bleiben lokal: keine Zeitleiste, nichts wird hochgeladen.\n\n" +
        "      12  AusgesprochenLangerBefehlsnameDerUmbrechenMussUndTrotzdemLesbarBleibenSollteAuchInEinerSchmalenSpalte  (zuletzt vor 6 Tagen)\n" +
        "       3  使用状況の統計はこのコンピューター上でのみ集計されます  (最終 3 日前)\n", true);

    /// <summary>A report the size of a long-used install: five hundred lines.</summary>
    public static FakeTextDocumentScreen Full() => new(UsageTitle, UsageSubtitle, UsageReport(500), true);

    /// <summary>The shape of LICENSE.md: headings, a fenced notice and long prose lines.</summary>
    public static FakeTextDocumentScreen License() => new("License & notices",
        "The terms DotCharter is under, and the notices of what it builds on.",
        "# License\n\n## 1 — DotCharter License\n\n```\nDotCharter — a guitar game and charting workstation.\n" +
        "Copyright (C) 2025-2026 the DotCharter authors and contributors.\n```\n\n" +
        string.Concat(Enumerable.Repeat(
            "This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even " +
            "the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU General " +
            "Public License for more details.\n\n", 30)), true);

    public string Title { get; }

    public string Subtitle { get; }

    public string Text { get; }

    public bool HasText => Text.Length > 0;

    public string EmptyText { get; }

    public ICommand CopyCommand { get; }

    public bool CanShowFile { get; }

    public ICommand ShowFileCommand { get; }

    public ICommand CloseCommand { get; }

    private static string UsageReport(int rows)
    {
        var text = new StringBuilder("=== DotCharter usage statistics ===\n")
            .Append("Counts are local and aggregate: no timeline is recorded and nothing is uploaded.\n\n")
            .Append($"--- command (180 available, {Math.Min(rows, 180)} used) ---\n");
        for (var i = 0; i < rows; i++)
            text.Append($"{rows - i,8}  Command{i:000}  (last {i % 9 + 1}d ago)\n");
        return text.ToString();
    }
}
