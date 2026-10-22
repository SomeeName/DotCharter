using System.Text.RegularExpressions;
using System.Xml.Linq;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Services.Infrastructure.Features;
using DotCharter.Services.Plugins;
using DotCharter.UI;
using DotCharter.UI.Views.Shell.Menus;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.UI;

/// <summary>
///     The package → <c>.dcsong</c> conversion exists only where a plugin reads packages: the File ▸ Import
///     row, the palette entry and the converter service are all absent from a build without one, and no
///     tracked product project declares a release tool (the tools a release ships come from
///     <c>plugins/</c> alone). The launcher's flow is <c>Presentation/Dialogs/PackageConversionLauncherTests</c>,
///     the headless half <c>Perf/PackageReaderOffUiPerfSmokeTests</c>, the payload checks
///     <c>Infrastructure/ReleasePluginRefusalTests</c>.
/// </summary>
public sealed class PackageConversionGateTests
{
    private const string PaletteId = "file.convertPackages";

    private static bool PluginIsInThisBuild => PluginLoader.Declared(typeof(App).Assembly).Count > 0;

    private sealed record Entry(string Id);

    [Fact]
    public void The_palette_entry_is_gated_by_the_game_package_feature_and_dropped_without_it()
    {
        Assert.Equal(PackageFeatures.GamePackagesId, ContestedFeatures.PackagePaletteCommandIds[PaletteId]);
        IReadOnlyList<Entry> all = [new(PaletteId), new("file.importMidi")];

        var off = ContestedFeatures.FilterPalette(all, e => e.Id, enabled: true, PackageFeatures.None);
        var on = ContestedFeatures.FilterPalette(all, e => e.Id, enabled: true, PackageFeatures.All);

        Assert.Equal(["file.importMidi"], off.Select(e => e.Id));
        Assert.Equal(all, on);
    }

    [Fact]
    public void The_file_menu_row_is_marked_as_reading_game_packages_so_a_build_without_the_reader_prunes_it()
    {
        var file = Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Shell", "Menus", "FileMenu.axaml");
        var rows = XDocument.Load(UiSourceTree.Resolve(file)).Descendants()
            .Where(e => e.Name.LocalName == "MenuItem" && e.Attributes().Any(a => a.Value.Contains("Imports.ConvertPackagesCommand", StringComparison.Ordinal)))
            .ToList();

        var row = Assert.Single(rows);
        var mark = row.Attributes().SingleOrDefault(a => a.Name.LocalName == "ContestedMenu.Feature");
        Assert.NotNull(mark);
        Assert.Equal(PackageFeatures.GamePackagesId, mark.Value);
        // The marking is what prunes it: a package row is removed when the build reads no packages.
        Assert.False(RowStays(mark.Value, packages: false));
        Assert.True(RowStays(mark.Value, packages: true));
    }

    private static bool RowStays(string id, bool packages) =>
        ContestedMenu.IsOn(id, contestedEnabled: true, packages ? PackageFeatures.All : PackageFeatures.None);

    [Fact]
    public void The_converter_is_registered_only_by_a_plugin_and_never_by_the_app_itself()
    {
        var off = new ServiceCollection();
        App.ConfigureServices(off, new JsonConfigurationService(), plugins: false);
        var on = new ServiceCollection();
        App.ConfigureServices(on, new JsonConfigurationService(), plugins: true);

        Assert.DoesNotContain(off, d => d.ServiceType == typeof(IPackageSongConverter));
        // In a public build (no plugin declared) the "on" composition has none either.
        Assert.Equal(PluginIsInThisBuild, on.Any(d => d.ServiceType == typeof(IPackageSongConverter)));
        // The launcher is the app's and always there; it reads the converter as an optional sequence.
        Assert.Contains(off, d => d.ServiceType == typeof(PackageConversionLauncher));
    }

    [Fact]
    public void No_tracked_project_declares_a_release_tool()
    {
        var root = TestPaths.RepoRoot();
        Assert.SkipUnless(Path.Exists(Path.Combine(root, ".git")),
            "Not a git checkout (no .git) — the tracked-file list is unknowable here.");

        var tracked = PersonalDataLintTests.TrackedFiles(root).Where(p => p.EndsWith(".csproj", StringComparison.Ordinal)).ToList();
        // A scan over nothing would pass; and the pattern must hit what a plugin's tool project writes.
        Assert.Contains("DotCharter.UI/DotCharter.UI.csproj", tracked);
        Assert.Matches(ReleaseToolMarker(), "<DotCharterReleaseTool>true</DotCharterReleaseTool>");

        var offenders = tracked
            .Where(p => File.Exists(Path.Combine(root, p)) && ReleaseToolMarker().IsMatch(File.ReadAllText(Path.Combine(root, p))))
            .ToList();
        Assert.True(offenders.Count == 0, LintReport.Of(
            "A tracked project declares a release tool",
            offenders,
            "the public tree ships no console tool beside the app; a plugin's own repository declares its tools (docs/agents/services/contested-features.md § Optional plugins)",
            "docs/agents/services/contested-features.md"));
    }

    private static Regex ReleaseToolMarker() => new(@"<DotCharterReleaseTool>\s*true\s*</DotCharterReleaseTool>", RegexOptions.CultureInvariant);
}
