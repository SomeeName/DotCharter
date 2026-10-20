using System.Text.RegularExpressions;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Services.Infrastructure.Shell;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>THIRD-PARTY-NOTICES.md, then the GPL text in LICENSE, for Help ▸ License &amp; notices and About.</summary>
public static partial class LicenseDocument
{
    public const string NoticesFileName = "THIRD-PARTY-NOTICES.md";
    public const string LicenseFileName = "LICENSE";

    /// <summary>Reads both files: call it off the UI thread.</summary>
    public static TextDocumentRequest Request(IBundledDocuments documents, ILocalizationService localization)
    {
        ArgumentNullException.ThrowIfNull(documents);
        var notices = documents.ReadText(NoticesFileName) ?? "";
        var licence = (documents.ReadText(LicenseFileName) ?? "").Trim();
        var text = string.Join("\n\n", new[] { Plain(notices), licence }.Where(part => part.Length > 0));
        var path = notices.Length > 0 ? documents.Resolve(NoticesFileName)
            : licence.Length > 0 ? documents.Resolve(LicenseFileName) : "";
        return new TextDocumentRequest(localization["Document_License_Title"], localization["Document_License_Subtitle"],
            text, path, localization["Document_License_Missing"]);
    }

    /// <summary>
    ///     The Markdown read as prose: fences and heading marks dropped, bold and code
    ///     unwrapped, a link reduced to its words. The file itself is one click away.
    /// </summary>
    public static string Plain(string markdown)
    {
        var lines = (markdown ?? "").Replace("\r\n", "\n").Split('\n')
            .Where(line => !line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            .Select(line => Heading().Replace(line, ""));
        var text = Link().Replace(string.Join("\n", lines), "$1");
        return Emphasis().Replace(text, "$1$2").Trim();
    }

    [GeneratedRegex(@"^#{1,6}\s+")]
    private static partial Regex Heading();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]*\)")]
    private static partial Regex Link();

    [GeneratedRegex(@"\*\*(.+?)\*\*|`([^`\n]+)`", RegexOptions.Singleline)]
    private static partial Regex Emphasis();
}
