using System.Text.RegularExpressions;
using DotCharter.Services.Plugins;
using DotCharter.Services.Infrastructure.Features;
using DotCharter.Services.Library;
using DotCharter.UI;
using DotCharter.UI.ViewModels.Onboarding;
using DotCharter.UI.ViewModels.Shell;
using DotCharter.UI.Views.Shell.Menus;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.UI;

/// <summary>
///     The package-feature gate (#887), flags off: every entry point that READS game
///     packages or the game install is ABSENT when <see cref="IPackageFeatures" /> says
///     the build cannot, and present when it can. The headless half (the views) is
///     <c>Perf/PackageReaderOffUiPerfSmokeTests</c>.
/// </summary>
public sealed partial class PackageFeaturesGateTests
{
    private static ServiceCollection Composition(bool packageReader)
    {
        var services = new ServiceCollection();
        App.ConfigureServices(services, new JsonConfigurationService(), packageReader);
        return services;
    }

    /// <summary>Whether this build carries a plugin, here the package reader (none in <c>-Public</c> runs and public clones).</summary>
    private static bool PluginIsInThisBuild => PluginLoader.Declared(typeof(App).Assembly).Count > 0;

    [Fact]
    public void Without_the_reader_the_graph_still_resolves_and_registers_no_package_source_or_tone_anchors()
    {
        var off = Composition(packageReader: false);
        var on = Composition(packageReader: true);

        using (var provider = off.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }))
            Assert.Equal(PackageFeatures.None, provider.GetRequiredService<IPackageFeatures>());
        using (var provider = on.BuildServiceProvider())
        {
            // The package reader is the plugin (#889): with it the build reads packages and the
            // install, without it (a public build) the "on" composition is the "off" one.
            var features = provider.GetRequiredService<IPackageFeatures>();
            Assert.Equal(PluginIsInThisBuild, features.ReadsGamePackages);
            Assert.Equal(PluginIsInThisBuild, features.ReadsGameInstall);
        }

        // Public means public: the reader-off composition does not even look for the plugin.
        Assert.DoesNotContain(off, d => d.ServiceType == typeof(IGameInstallReader));
        Assert.Equal(PluginIsInThisBuild, on.Any(d => d.ServiceType == typeof(IGameInstallReader)));

        // .psarc and .dcsong with the reader, .dcsong alone without it.
        Assert.Equal(PluginIsInThisBuild ? 2 : 1, on.Count(d => d.ServiceType == typeof(ILibraryPackageSource)));
        Assert.Equal(1, off.Count(d => d.ServiceType == typeof(ILibraryPackageSource)));
        Assert.Equal(PluginIsInThisBuild, on.Any(d => d.ServiceType == typeof(DotCharter.Tone.Ai.IToneAnchorLibrary)));
        Assert.Equal(PluginIsInThisBuild, on.Any(d => d.ServiceType == typeof(IPackageToneImporter)));
        Assert.DoesNotContain(off, d => d.ServiceType == typeof(DotCharter.Tone.Ai.IToneAnchorLibrary));
        Assert.DoesNotContain(off, d => d.ServiceType == typeof(IPackageToneImporter));
        // The writer is not a reader: the package export stays either way.
        Assert.Contains(off, d => d.ServiceType == typeof(DotCharter.Presentation.Contracts.Dialogs.IRocksmithExportScreen));
    }

    [Fact]
    public void The_package_source_keeps_its_place_ahead_of_dcsong()
    {
        Assert.SkipUnless(PluginIsInThisBuild, "No package reader in this build (the optional plugin is absent).");
        var on = Composition(packageReader: true);

        var sources = on.Select((d, i) => (d, i)).Where(x => x.d.ServiceType == typeof(ILibraryPackageSource)).ToList();
        var library = on.Select((d, i) => (d, i)).First(x => x.d.ServiceType == typeof(ILibraryService)).i;

        Assert.Equal(2, sources.Count);
        Assert.All(sources, s => Assert.True(s.i < library));
        // The first source is the plugin's, the second the app's own .dcsong source.
        Assert.Contains(AssemblyOf(sources[0].d), PluginLoader.Declared(typeof(App).Assembly));
        Assert.Equal(typeof(App).Assembly.GetName().Name, AssemblyOf(sources[1].d));
    }

    private static string AssemblyOf(ServiceDescriptor descriptor) =>
        descriptor.ImplementationFactory?.Method.DeclaringType?.Assembly.GetName().Name ?? "";

    [Fact]
    public void A_dropped_package_is_just_an_unsupported_type_without_the_reader()
    {
        Assert.Equal(DroppedFileRoute.RocksmithPackage, DroppedFileRouter.Route("song_p.psarc", readsGamePackages: true));
        Assert.Equal(DroppedFileRoute.Unsupported, DroppedFileRouter.Route("song_p.psarc", readsGamePackages: false));
        // Everything else routes the same either way.
        Assert.Equal(DroppedFileRoute.Project, DroppedFileRouter.Route("song.dcsong", readsGamePackages: false));
    }

    [Fact]
    public void Onboarding_has_no_library_page_without_the_reader()
    {
        var off = new OnboardingStepMachine(includeLibrary: false);
        var on = new OnboardingStepMachine();

        var visited = new List<OnboardingStep> { off.Current };
        while (off.Next()) visited.Add(off.Current);

        Assert.DoesNotContain(OnboardingStep.Library, visited);
        Assert.Equal(OnboardingStepMachine.Order.Count - 1, off.Count);
        Assert.Equal(OnboardingStep.Done, visited[^1]);
        Assert.True(on.Next());
        Assert.Equal(OnboardingStep.Library, on.Current);
    }

    [Theory]
    [InlineData(PackageFeatures.GamePackagesId, true, false, false)]
    [InlineData(PackageFeatures.GamePackagesId, true, true, true)]
    [InlineData(PackageFeatures.GameInstallId, true, false, false)]
    [InlineData(ContestedFeatures.YoutubeImportId, false, true, false)]
    [InlineData(ContestedFeatures.YoutubeImportId, true, false, true)]
    public void A_marked_menu_row_stays_only_while_its_feature_is_on(string id, bool contested, bool packages, bool stays) =>
        Assert.Equal(stays, ContestedMenu.IsOn(id, contested, packages ? PackageFeatures.All : PackageFeatures.None));

    [Fact]
    public void A_package_row_is_kept_until_the_features_are_known() =>
        Assert.True(ContestedMenu.IsOn(PackageFeatures.GamePackagesId, contestedEnabled: true, packages: null));

    private sealed record Entry(string Id);

    [Fact]
    public void The_palette_drops_a_package_entry_only_when_the_build_cannot_read_packages()
    {
        IReadOnlyList<Entry> all = [new("library.readPackage"), new("file.exportRocksmith")];
        var registry = new Dictionary<string, string> { ["library.readPackage"] = PackageFeatures.GamePackagesId };

        var off = ContestedFeatures.FilterPalette(all, e => e.Id, enabled: true, PackageFeatures.None, registry);
        var on = ContestedFeatures.FilterPalette(all, e => e.Id, enabled: true, PackageFeatures.All, registry);

        Assert.Equal(["file.exportRocksmith"], off.Select(e => e.Id));
        Assert.Equal(all, on);
    }

    /// <summary>
    ///     Every UI or Presentation file that names a package reader's SEAM asks the gate in the
    ///     same file — the plugin composition (<c>App.Plugins.cs</c>) is the gate.
    /// </summary>
    [Fact]
    public void Every_reader_entry_point_in_the_ui_asks_the_package_features()
    {
        var root = TestPaths.RepoRoot();
        var offenders = new List<string>();
        var named = 0;
        foreach (var project in new[] { "DotCharter.UI", "DotCharter.Presentation" })
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, project), "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal)) continue;
                if (relative == "DotCharter.UI/App.Plugins.cs") continue;
                var code = SourceText.StripCommentsKeepingLines(File.ReadAllText(file));
                var reader = ReaderName().Match(code);
                if (!reader.Success) continue;
                named++;
                if (!AsksTheGate().IsMatch(code))
                    offenders.Add($"{relative}:{SourceText.LineAt(code, reader.Index)}: names {reader.Value} and never asks IPackageFeatures");
            }
        }

        Assert.True(named >= 4, $"the lint matched only {named} files - its reader list no longer names the entry points");
        Assert.True(offenders.Count == 0, LintReport.Of(
            "A reader entry point is absent when the build cannot read packages (#887)",
            offenders,
            "take IPackageFeatures (or a flag derived from it) and leave the entry out when ReadsGamePackages / ReadsGameInstall is false",
            "docs/agents/services/contested-features.md"));
    }

    [GeneratedRegex(@"\b(IGameInstallReader|IPackageToneImporter|PackageTones|DroppedFileRoute\.RocksmithPackage|ImportTonesFromPsarcAsync)\b")]
    private static partial Regex ReaderName();

    [GeneratedRegex(@"\b(ReadsGamePackages|ReadsGameInstall|readsGamePackages|CanImportFromPackages)\b")]
    private static partial Regex AsksTheGate();
}
