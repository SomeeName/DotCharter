using System.Text.RegularExpressions;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     The static <c>App.Services</c> service locator belongs to the composition root
///     (<c>DotCharter.UI/App*.cs</c> and <c>Program.cs</c>). Everything else takes its
///     dependencies through its constructor or from its owner. The files below are
///     today's exceptions — XAML-constructed controls (parameterless constructors),
///     render-path code, and the main window's one documented resolution point — and
///     the list may only shrink: a file over its allowance, an unlisted file with a
///     use, and an allowance above the file's real count all fail.
/// </summary>
public sealed partial class ServiceLocatorRatchetTests
{
    private sealed record Allowance(string Path, int Uses);

    private static readonly string[] ScannedProjects = ["DotCharter.UI", "DotCharter.Tone.UI"];

    private const string ViewModelsPrefix = "DotCharter.UI/ViewModels/";

    private static readonly Allowance[] Remaining =
    [
        new("DotCharter.UI/Controls/ChartRenderer.cs", 7),
        new("DotCharter.UI/Controls/ComposedPresentPump.cs", 1),
        new("DotCharter.UI/Controls/LivePlaySectionGraph.cs", 1),
        new("DotCharter.UI/Controls/Markdown/MarkdownView.cs", 1),
        new("DotCharter.UI/Controls/MinimapOverviewControl.cs", 1),
        new("DotCharter.UI/Controls/Preview3DComposedCanvas.cs", 3),
        new("DotCharter.UI/Controls/Preview3DRenderDriver.cs", 7),
        new("DotCharter.UI/Controls/Sheet/SheetMusicView.cs", 1),
        new("DotCharter.UI/Controls/Timeline2D/VulkanTimelineBackend.cs", 2),
        new("DotCharter.UI/Views/MainWindow.axaml.cs", 1),
        new("DotCharter.UI/Views/Shell/DialogContainerView.axaml.cs", 1),
    ];

    [Fact]
    public void NoFileUsesTheServiceLocatorMoreThanItsAllowance()
    {
        var allowances = Remaining.ToDictionary(a => a.Path, a => a.Uses, StringComparer.Ordinal);
        var failures = new List<string>();

        foreach (var (path, uses) in CountUses())
        {
            var allowed = allowances.GetValueOrDefault(path);
            if (uses > allowed)
            {
                failures.Add($"{path}: {uses} uses against an allowance of {allowed}");
            }
        }

        Assert.True(failures.Count == 0,
            "These files read App.Services more often than allowed. Take the dependency through " +
            "the constructor, or have the owner hand it over:\n  " + string.Join("\n  ", failures));
    }

    [Fact]
    public void EveryAllowanceIsStillNeededAndExact()
    {
        var counts = CountUses().ToDictionary(c => c.Path, c => c.Uses, StringComparer.Ordinal);
        var failures = new List<string>();

        foreach (var allowance in Remaining)
        {
            var uses = counts.GetValueOrDefault(allowance.Path);
            if (uses != allowance.Uses)
            {
                failures.Add(uses == 0
                    ? $"{allowance.Path}: no App.Services uses left, delete the entry"
                    : uses < allowance.Uses
                        ? $"{allowance.Path}: {uses} uses against an allowance of {allowance.Uses}, lower it"
                        : $"{allowance.Path}: {uses} uses against an allowance of {allowance.Uses}, over it");
            }
        }

        Assert.True(failures.Count == 0,
            "The service-locator allowance list may only shrink:\n  " + string.Join("\n  ", failures));
    }

    [Fact]
    public void EveryAllowanceIsListedOnce()
    {
        Assert.Equal(Remaining.Length, Remaining.Select(a => a.Path).Distinct(StringComparer.Ordinal).Count());
        Assert.All(Remaining, a => Assert.True(a.Uses > 0, a.Path));
    }

    [Fact]
    public void NoViewModelHasAnAllowance() => Assert.DoesNotContain(Remaining, a => a.Path.StartsWith(ViewModelsPrefix, StringComparison.Ordinal));

    [Fact]
    public void TheCounterSeesRealUsesAndIgnoresComments()
    {
        const string source = """
            class Probe
            {
                // App.Services in a line comment
                /* App.Services in a block comment */
                object A => App.Services?.GetService(typeof(object));
                object B => DotCharter.UI.App.Services!;
                object C => MyApp.Services;
                object D => App.ServicesReady;
            }
            """;

        Assert.Equal(2, UsesIn(source));
    }

    [Fact]
    public void TheCompositionRootIsExemptAndNothingElse()
    {
        Assert.True(IsCompositionRoot("DotCharter.UI/App.axaml.cs"));
        Assert.True(IsCompositionRoot("DotCharter.UI/App.EditingServices.cs"));
        Assert.True(IsCompositionRoot("DotCharter.UI/Program.cs"));
        Assert.False(IsCompositionRoot("DotCharter.UI/ViewModels/Shell/AppDialogService.cs"));
        Assert.False(IsCompositionRoot("DotCharter.UI/Input/AppKeybindRegistrar.cs"));
        Assert.False(IsCompositionRoot("DotCharter.Tone.UI/App.axaml.cs"));
    }

    [Fact]
    public void TheScanSeesTheProductTree()
    {
        var root = TestPaths.RepoRoot();
        var scanned = ProductSourceFiles(root).ToList();

        Assert.True(scanned.Count > 500, $"only {scanned.Count} product source files found; the scan is broken");
        Assert.Contains(scanned, file => Relative(root, file) == "DotCharter.UI/Views/MainWindow.axaml.cs");
        Assert.Contains(scanned, file => Relative(root, file).StartsWith("DotCharter.Tone.UI/", StringComparison.Ordinal));
    }

    private static IEnumerable<(string Path, int Uses)> CountUses()
    {
        var root = TestPaths.RepoRoot();
        foreach (var file in ProductSourceFiles(root))
        {
            var relative = Relative(root, file);
            if (IsCompositionRoot(relative))
            {
                continue;
            }

            var uses = UsesIn(File.ReadAllText(file));
            if (uses > 0)
            {
                yield return (relative, uses);
            }
        }
    }

    private static int UsesIn(string source) => ServiceLocatorUse().Matches(SourceText.StripComments(source)).Count;

    private static bool IsCompositionRoot(string relative) => CompositionRootFile().IsMatch(relative);

    private static string Relative(string root, string file) => Path.GetRelativePath(root, file).Replace('\\', '/');

    private static IEnumerable<string> ProductSourceFiles(string root)
    {
        foreach (var project in ScannedProjects)
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, project), "*.cs", SearchOption.AllDirectories))
            {
                var relative = Relative(root, file);
                if (!relative.Contains("/bin/", StringComparison.Ordinal) && !relative.Contains("/obj/", StringComparison.Ordinal))
                {
                    yield return file;
                }
            }
        }
    }

    [GeneratedRegex(@"\bApp\s*\.\s*Services\b")]
    private static partial Regex ServiceLocatorUse();

    [GeneratedRegex(@"^DotCharter\.UI/(App\.[^/]+|Program)\.cs$")]
    private static partial Regex CompositionRootFile();
}
