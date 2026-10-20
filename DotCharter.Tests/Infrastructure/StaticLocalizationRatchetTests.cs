using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DotCharter.Tests.Infrastructure;

public sealed class StaticLocalizationRatchetTests
{
    /// <summary>Why a file still reads the singleton.</summary>
    private enum Reason
    {
        /// <summary>Not moved onto an injected <c>ILocalizationService</c> yet; the entry only shrinks.</summary>
        NotMigratedYet,

        /// <summary>A control, view, converter or markup extension that XAML constructs with no
        /// arguments, so there is no constructor to inject through. It may stay.</summary>
        XamlConstructed,
    }

    private sealed record Allowance(string Path, int Uses, Reason Why = Reason.NotMigratedYet);

    /// <summary>The view-model trees, each with the fewest files a scan of it must find: every
    /// file under them takes its localization through a constructor or a parameter, and none may
    /// read the singleton (#289; the rig's, which RigLab hosts too, since #549).</summary>
    private static readonly (string Tree, int MinFiles)[] ViewModelTrees =
    [
        ("DotCharter.UI/ViewModels/", 100),
        ("DotCharter.Tone.UI/ViewModels/", 40),
    ];

    /// <summary>The layers below the views: a service, a screen's view model or the rig's audio
    /// host is built by DI or by its owner, so none of them may read the singleton (#549).</summary>
    private static readonly (string Tree, int MinFiles)[] SingletonFreeProjects =
    [
        ("DotCharter.Presentation/", 100),
        ("DotCharter.Services/", 500),
        ("DotCharter.Tone/", 100),
    ];

    /// <summary>Where a <see cref="Reason.XamlConstructed" /> type can live.</summary>
    private static readonly string[] XamlConstructedFolders = ["/Controls/", "/Converters/", "/Views/", "/Localization/"];

    private static readonly string[] OwningProjects = ["DotCharter.Localization", "DotCharter.Tests"];

    private static readonly string[] CompositionRoots =
    [
        "DotCharter.UI/App.axaml.cs",
        "DotCharter.RigLab/App.axaml.cs",
        "DotCharter.RigLab/RigLabHost.cs",
    ];

    private static readonly Allowance[] Remaining =
    [
        new("DotCharter.Tone.UI/Controls/Fire/RigFaceplateView.cs", 1, Reason.XamlConstructed),
        new("DotCharter.Tone.UI/Localization/LocExtension.cs", 2, Reason.XamlConstructed),
        new("DotCharter.Tone.UI/Views/PlayMode/RigControlPresentationConverter.cs", 2, Reason.XamlConstructed),
        new("DotCharter.UI/Controls/ChartRenderer.ContextMenu.cs", 1, Reason.XamlConstructed),
        new("DotCharter.UI/Controls/ChartRenderer.cs", 2, Reason.XamlConstructed),
        new("DotCharter.UI/Controls/KeybindCaptureBox.cs", 1, Reason.XamlConstructed),
        new("DotCharter.UI/Controls/LivePlaySectionGraph.cs", 1, Reason.XamlConstructed),
        new("DotCharter.UI/Controls/Preview3DComposedCanvas.cs", 1, Reason.XamlConstructed),
        new("DotCharter.UI/Controls/Sheet/SheetMusicView.cs", 5, Reason.XamlConstructed),
        new("DotCharter.UI/Controls/TimelineGutterControl.cs", 1, Reason.XamlConstructed),
        new("DotCharter.UI/Converters/EqPresetLabelConverter.cs", 1, Reason.XamlConstructed),
        new("DotCharter.UI/Converters/KeyChordToGestureConverter.cs", 1, Reason.XamlConstructed),
        new("DotCharter.UI/Converters/LocalizedFormatConverter.cs", 1, Reason.XamlConstructed),
        new("DotCharter.UI/Converters/SeverityToLabelConverter.cs", 1, Reason.XamlConstructed),
        new("DotCharter.UI/Views/MainWindow.Docking.cs", 4, Reason.XamlConstructed),
        new("DotCharter.UI/Views/MainWindow.axaml.cs", 1, Reason.XamlConstructed),
    ];

    [Fact]
    public void NoFileUsesTheStaticLocalizationMoreThanItsAllowance()
    {
        var allowances = Remaining.ToDictionary(a => a.Path, a => a.Uses, StringComparer.Ordinal);
        var failures = new List<string>();

        foreach (var (path, uses) in CountUses())
        {
            var allowed = allowances.GetValueOrDefault(path);
            if (uses > allowed)
            {
                failures.Add($"new(\"{path}\", {uses}),");
            }
        }

        Assert.True(failures.Count == 0,
            "These files read LocalizationService.Instance more often than allowed. Take an " +
            "ILocalizationService through the constructor (or as a parameter) instead:\n  " +
            string.Join("\n  ", failures));
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
                    ? $"{allowance.Path}: no static uses left, delete the entry"
                    : $"{allowance.Path}: {uses} uses against an allowance of {allowance.Uses}, lower it");
            }
        }

        Assert.True(failures.Count == 0,
            "The static-localization allowance list may only shrink:\n  " + string.Join("\n  ", failures));
    }

    /// <summary>
    ///     No view model reads <c>LocalizationService.Instance</c>, and none has an
    ///     allowance to: a view model is built by DI or by its parent, so it can always
    ///     be handed its localization. A test hands it <c>FakeLocalization</c>.
    /// </summary>
    [Fact]
    public void NoViewModelReadsTheStaticLocalization() =>
        AssertNoReaderUnder(ViewModelTrees,
            "A view model takes an ILocalizationService through its constructor (DI fills it, or the " +
            "parent that builds it passes its own down); it never reads LocalizationService.Instance " +
            "and never gets an allowance");

    /// <summary>
    ///     Presentation, Services and Tone read the singleton nowhere, and none has an
    ///     allowance: every type there is built by DI or by an owner that can hand it its
    ///     localization (#549).
    /// </summary>
    [Fact]
    public void NoServiceOrScreenLayerFileReadsTheStaticLocalization() =>
        AssertNoReaderUnder(SingletonFreeProjects,
            "Below the views every type is built by DI or by its owner: take an ILocalizationService " +
            "through the constructor or as a parameter, never LocalizationService.Instance, and never an allowance");

    private static void AssertNoReaderUnder((string Tree, int MinFiles)[] trees, string rule)
    {
        var root = TestPaths.RepoRoot();
        var scanned = ProductSourceFiles(root)
            .Select(file => Path.GetRelativePath(root, file).Replace('\\', '/'))
            .ToList();
        foreach (var (tree, minFiles) in trees)
        {
            var files = scanned.Count(path => path.StartsWith(tree, StringComparison.Ordinal));
            Assert.True(files >= minFiles,
                $"only {files} files found under {tree} (at least {minFiles} expected); the tree moved and this fact went blind");
        }

        bool Covered(string path) => trees.Any(t => path.StartsWith(t.Tree, StringComparison.Ordinal));
        var readers = CountUses()
            .Where(c => Covered(c.Path))
            .Select(c => $"{c.Path}: {c.Uses} use(s)")
            .ToList();
        var allowed = Remaining.Where(a => Covered(a.Path)).Select(a => $"{a.Path}: has an allowance").ToList();

        Assert.True(readers.Count == 0 && allowed.Count == 0,
            rule + ":\n  " + string.Join("\n  ", readers.Concat(allowed)));
    }

    [Fact]
    public void EveryXamlConstructedAllowanceIsAViewControlConverterOrMarkup()
    {
        var misfiled = Remaining
            .Where(a => a.Why == Reason.XamlConstructed &&
                        !XamlConstructedFolders.Any(folder => a.Path.Contains(folder, StringComparison.Ordinal)))
            .Select(a => a.Path)
            .ToList();

        Assert.True(misfiled.Count == 0,
            "Only a type XAML constructs with no arguments may keep the singleton for that reason:\n  " +
            string.Join("\n  ", misfiled));
    }

    [Fact]
    public void EveryAllowanceIsListedOnce()
    {
        Assert.Equal(Remaining.Length, Remaining.Select(a => a.Path).Distinct(StringComparer.Ordinal).Count());
        Assert.All(Remaining, a => Assert.True(a.Uses > 0, a.Path));
    }

    [Fact]
    public void TheCounterSeesRealUsesAndIgnoresCommentsAndStrings()
    {
        const string source = """
            class Probe
            {
                // LocalizationService.Instance in a comment
                string Text = "LocalizationService.Instance in a string";
                string A => LocalizationService.Instance["Key"];
                string B => DotCharter.Localization.LocalizationService.Instance.Format("Key", 1);
            }
            """;

        Assert.Equal(2, UsesIn(source));
    }

    [Fact]
    public void TheCounterFollowsAliasesOfTheStaticInstance()
    {
        const string member = """
            class Probe
            {
                private static ILocalizationService L => LocalizationService.Instance;
                string A => L["One"];
                string B => L.Format("Two", 1);
            }
            """;
        const string local = """
            class Probe
            {
                string A()
                {
                    var loc = LocalizationService.Instance;
                    return loc["One"] + loc["Two"];
                }
            }
            """;
        const string usingAlias = """
            using Strings = DotCharter.Localization.LocalizationService;
            class Probe
            {
                string A => Strings.Instance["One"];
            }
            """;
        const string usingStatic = """
            using static DotCharter.Localization.LocalizationService;
            class Probe
            {
                string A => Instance["One"];
            }
            """;

        Assert.Equal(3, UsesIn(member));
        Assert.Equal(3, UsesIn(local));
        Assert.Equal(1, UsesIn(usingAlias));
        Assert.Equal(1, UsesIn(usingStatic));
    }

    [Fact]
    public void TheScanSeesTheProductTree()
    {
        var root = TestPaths.RepoRoot();
        var scanned = ProductSourceFiles(root).Count();

        Assert.True(scanned > 1000, $"only {scanned} product source files found; the scan is broken");
    }

    private static IEnumerable<(string Path, int Uses)> CountUses()
    {
        var root = TestPaths.RepoRoot();
        foreach (var file in ProductSourceFiles(root))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (CompositionRoots.Contains(relative, StringComparer.Ordinal))
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

    private static int UsesIn(string source)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var typeNames = new HashSet<string>(StringComparer.Ordinal) { "LocalizationService" };
        var importsStatically = false;
        foreach (var directive in root.DescendantNodes().OfType<UsingDirectiveSyntax>())
        {
            if (directive.NamespaceOrType is null ||
                !directive.NamespaceOrType.ToString().EndsWith("LocalizationService", StringComparison.Ordinal))
            {
                continue;
            }

            if (directive.Alias is not null)
            {
                typeNames.Add(directive.Alias.Name.Identifier.Text);
            }
            else if (directive.StaticKeyword.IsKind(SyntaxKind.StaticKeyword))
            {
                importsStatically = true;
            }
        }

        var accesses = root.DescendantNodes().OfType<ExpressionSyntax>()
            .Where(expression => IsStaticInstance(expression, typeNames, importsStatically))
            .ToList();
        var aliases = AliasNames(accesses);
        var aliasUses = root.DescendantNodes().OfType<IdentifierNameSyntax>()
            .Count(name => aliases.Contains(name.Identifier.Text));

        return accesses.Count + aliasUses;
    }

    private static bool IsStaticInstance(ExpressionSyntax expression, HashSet<string> typeNames, bool importsStatically)
    {
        switch (expression)
        {
            case MemberAccessExpressionSyntax access when access.Name.Identifier.Text == "Instance":
            {
                var owner = access.Expression switch
                {
                    IdentifierNameSyntax name => name.Identifier.Text,
                    MemberAccessExpressionSyntax qualified => qualified.Name.Identifier.Text,
                    _ => null,
                };

                return owner is not null && typeNames.Contains(owner);
            }

            case IdentifierNameSyntax name when importsStatically && name.Identifier.Text == "Instance":
                return name.Parent is not MemberAccessExpressionSyntax parent || parent.Expression == name;

            default:
                return false;
        }
    }

    private static HashSet<string> AliasNames(IEnumerable<ExpressionSyntax> accesses)
    {
        var aliases = new HashSet<string>(StringComparer.Ordinal);
        foreach (var access in accesses)
        {
            var value = access.Parent is BinaryExpressionSyntax coalesce && coalesce.IsKind(SyntaxKind.CoalesceExpression)
                ? coalesce
                : access;
            switch (value.Parent)
            {
                case ArrowExpressionClauseSyntax { Parent: PropertyDeclarationSyntax property }:
                    aliases.Add(property.Identifier.Text);
                    break;
                case EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax variable }:
                    aliases.Add(variable.Identifier.Text);
                    break;
                case AssignmentExpressionSyntax { Left: IdentifierNameSyntax target }:
                    aliases.Add(target.Identifier.Text);
                    break;
            }
        }

        return aliases;
    }

    private static IEnumerable<string> ProductSourceFiles(string root)
    {
        foreach (var project in Directory.EnumerateDirectories(root, "DotCharter.*"))
        {
            if (OwningProjects.Contains(Path.GetFileName(project), StringComparer.Ordinal))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (!relative.Contains("/bin/", StringComparison.Ordinal) && !relative.Contains("/obj/", StringComparison.Ordinal))
                {
                    yield return file;
                }
            }
        }
    }
}
