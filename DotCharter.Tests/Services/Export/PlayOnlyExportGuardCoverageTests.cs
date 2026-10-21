using System.Text.RegularExpressions;
using DotCharter.Services.Export;

namespace DotCharter.Tests.Services.Export;

/// <summary>
///     #822: play-only content is refused, and "every exporter refuses
///     it" is only true while every exporter is on this list. Three checks:
///     <list type="number">
///         <item>each listed entry point's source still names the guard;</item>
///         <item>every type in <c>DotCharter.Services</c> named <c>*Exporter</c> or
///         <c>*PackageBuilder</c> is listed or exempted with a reason — a new
///         MIDI exporter, say, fails here the day it compiles;</item>
///         <item>every <c>[RelayCommand]</c> named <c>Export*</c> or <c>Open*Export*</c>
///         in the UI and Presentation projects (the File ▸ Export menu wiring and its
///         siblings) sits in a listed file or is exempted with a reason.</item>
///     </list>
///     Behaviour — that each one REFUSES — is <see cref="PlayOnlyExportGuardTests" />.
/// </summary>
public sealed class PlayOnlyExportGuardCoverageTests
{
    private const string Guard = "PlayOnlyExportGuard.";

    /// <summary>Every place chart content leaves the app, and the token its guard is spelled with.</summary>
    internal static readonly (string What, string Source, string Token)[] GuardedEntryPoints =
    [
        ("Rocksmith package (.psarc)", "DotCharter.Services/Export/RocksmithPackageBuilder.cs", Guard),
        ("Rocksmith XML (Save As .xml)", "DotCharter.Services/Parsing/Xml/XmlChartSerializer.cs", Guard),
        ("Rocksmith XML bundle (DTRS XML/)", "DotCharter.Services/Infrastructure/Documents/ProjectDocumentService.cs", Guard),
        ("Editor on Fire (.eof)", "DotCharter.Services/Parsing/Eof/EofExporter.cs", Guard),
        ("Editor on Fire notes", "DotCharter.Services/Parsing/Eof/EofNoteBuilder.cs", Guard),
        ("Guitar Pro (.gp)", "DotCharter.Services/Export/GuitarPro/GpExporter.cs", Guard),
        ("Sheet PDF (alphaTex)", "DotCharter.Services/Export/Sheet/SheetPdfExporter.cs", Guard),
        ("DotCharter song (.dcsong)", "DotCharter.Services/Package/DcSong/DcSongWriter.cs", Guard),
        ("Save companions (.eof, .rs2dlc, .psarc)", "DotCharter.Services/Export/SaveCompanionService.cs", Guard),
        ("DLC Builder project (export dialog)", "DotCharter.Services/Export/DlcBuilder/DlcBuilderProjectWriter.cs", Guard),
        ("Phrase catalog (note data on disk)", "DotCharter.Services/Editing/Catalog/PhraseCatalogService.cs", Guard),
        ("Clipboard / phrase paste into another chart", "DotCharter.Services/Editing/Catalog/PhrasePasteCompatibility.cs", "KeyPlayOnly"),
        ("Save As .xml message", "DotCharter.Services/Session/ProjectSaver.cs", "PlayOnlyContentException"),
        ("File ▸ Export: Guitar Pro, EOF, sheet PDF", "DotCharter.UI/ViewModels/Shell/ExportMenuViewModel.cs", Guard),
        ("File ▸ Export: Rocksmith package, DotCharter song", "DotCharter.UI/ViewModels/Shell/ShellDialogsViewModel.cs", Guard),
        ("Lyrics: LRC, UltraStar", "DotCharter.UI/ViewModels/Editor/LyricsInspectorViewModel.cs", Guard),
        ("Phrase catalog capture", "DotCharter.UI/ViewModels/Editor/PhraseCatalogViewModel.cs", Guard),
        ("Rocksmith export dialog", "DotCharter.Presentation/Screens/Dialogs/RocksmithExportViewModel.Build.cs", Guard),
        ("DotCharter song dialog", "DotCharter.Presentation/Screens/Dialogs/DcSongExportViewModel.Write.cs", Guard)
    ];

    /// <summary>Exporter-named types that write no chart content, each with the reason.</summary>
    private static readonly Dictionary<string, string> ExemptTypes = new(StringComparer.Ordinal)
    {
        ["AlphaTexExporter"] = "the sheet PANE's renderer (viewing); the file it feeds is SheetPdfExporter, guarded",
        ["DiagnosticsBundleExporter"] = "logs and settings, no chart",
        ["DcSongExporter"] = "the app's door to DcSongWriter, whose WriteAsync is guarded"
    };

    /// <summary>Export-named commands that write no chart content, each with the reason.</summary>
    private static readonly Dictionary<string, string> ExemptCommands = new(StringComparer.Ordinal)
    {
        ["ExportProfileAsync"] = "the performance profile of the running app, no chart",
        ["ExportBundleAsync"] = "the diagnostics bundle, no chart"
    };

    [Fact]
    public void Every_listed_entry_point_still_names_the_guard()
    {
        var root = TestPaths.RepoRoot();
        var missing = GuardedEntryPoints
            .Where(e => !SourceText.StripComments(File.ReadAllText(Path.Combine(root, e.Source))).Contains(e.Token))
            .Select(e => $"{e.What}: {e.Source} no longer contains '{e.Token}'")
            .ToList();

        Assert.True(missing.Count == 0, string.Join(Environment.NewLine, missing));
    }

    [Fact]
    public void Every_exporter_type_is_guarded_or_exempted_with_a_reason()
    {
        var root = TestPaths.RepoRoot();
        var services = Path.Combine(root, "DotCharter.Services");
        var listed = GuardedEntryPoints.Select(e => Path.GetFullPath(Path.Combine(root, e.Source)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var exporterTypes = typeof(PlayOnlyExportGuard).Assembly.GetTypes()
            .Where(t => !t.IsNested && !t.IsInterface && !t.Name.Contains('<'))
            .Where(t => t.Name.EndsWith("Exporter", StringComparison.Ordinal) ||
                        t.Name.EndsWith("PackageBuilder", StringComparison.Ordinal))
            .Select(t => t.Name)
            .Distinct()
            .ToList();
        Assert.Contains("GpExporter", exporterTypes);
        Assert.Contains("RocksmithPackageBuilder", exporterTypes);

        var sources = Directory.EnumerateFiles(services, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Select(f => (Path: Path.GetFullPath(f), Text: File.ReadAllText(f)))
            .ToList();

        var unguarded = new List<string>();
        foreach (var name in exporterTypes)
        {
            if (ExemptTypes.ContainsKey(name)) continue;
            var declaration = new Regex($@"\b(class|record|struct)\s+{Regex.Escape(name)}\b");
            var file = sources.FirstOrDefault(s => declaration.IsMatch(s.Text));
            if (file.Path is null || !listed.Contains(file.Path)) unguarded.Add(name);
        }

        Assert.True(unguarded.Count == 0,
            "Exporter types that are neither guarded by PlayOnlyExportGuard nor exempted (#822): " +
            string.Join(", ", unguarded));
        Assert.All(ExemptTypes.Keys, name => Assert.Contains(name, exporterTypes));
    }

    [Fact]
    public void Every_export_command_is_guarded_or_exempted_with_a_reason()
    {
        var root = TestPaths.RepoRoot();
        var listed = GuardedEntryPoints.Select(e => Path.GetFullPath(Path.Combine(root, e.Source)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var command = new Regex(
            @"\[RelayCommand[^\]]*\]\s*(?:\[[^\]]*\]\s*)*(?:private|public|internal|protected)[^(\n]*?\s(?<name>(?:Export\w*|Open\w*Export\w*))\s*\(");

        var found = new List<string>();
        var unguarded = new List<string>();
        foreach (var project in new[] { "DotCharter.UI", "DotCharter.Presentation" })
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, project), "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
                foreach (Match m in command.Matches(SourceText.StripComments(File.ReadAllText(file))))
                {
                    var name = m.Groups["name"].Value;
                    found.Add(name);
                    if (ExemptCommands.ContainsKey(name)) continue;
                    if (!listed.Contains(Path.GetFullPath(file))) unguarded.Add($"{name} in {Path.GetRelativePath(root, file)}");
                }
            }
        }

        // Non-vacuity: the scan sees the File ▸ Export menu.
        Assert.Contains("ExportGuitarPro", found);
        Assert.Contains("OpenRocksmithExport", found);
        Assert.True(unguarded.Count == 0,
            "Export commands outside a guarded file (#822): " + string.Join(", ", unguarded));
        Assert.All(ExemptCommands.Keys, name => Assert.Contains(name, found));
    }
}
