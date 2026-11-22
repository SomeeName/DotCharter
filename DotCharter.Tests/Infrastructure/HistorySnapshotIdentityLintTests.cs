using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DotCharter.Tests.Infrastructure;

/// <summary>History snapshots retain ownership tokens; detached copies are restricted to audited non-history consumers.</summary>
public sealed class HistorySnapshotIdentityLintTests(ITestOutputHelper output)
{
    private const int DetachedCopyCeiling = 25;
    private static readonly HashSet<string> CloneMethods = ["Clone", "CloneArrangement", "CloneLevel", "CloneLinkGroups"];

    // This exception set only shrinks. Each key permits exactly one existing detached call.
    private static readonly Dictionary<string, string> DetachedCopies = new(StringComparer.Ordinal)
    {
        ["DotCharter.Presentation/Screens/Dialogs/CheckpointsViewModel.cs::CreateAsync::Clone"] = "Immutable checkpoint input is serialized to disk; it is never restored by history.",
        ["DotCharter.Presentation/Screens/Dialogs/RocksmithExportViewModel.cs::DlcBuilderInput::Clone"] = "Export input is isolated for serialization and never applied to the live graph.",
        ["DotCharter.Presentation/Screens/Dialogs/RocksmithExportViewModel.Build.cs::RunExportAsync::Clone"] = "The export worker transforms a disposable build graph, not an undo snapshot.",
        ["DotCharter.Services/Session/ProjectSaver.cs::SaveProjectCoreAsync::Clone"] = "A save worker serializes this isolated graph; undo never consumes it.",
        ["DotCharter.Services/Session/ProjectAutoBackup.cs::CreateBackupAsync::Clone"] = "Backup serialization needs a detached graph and does not restore editor history.",
        ["DotCharter.Services/Ai/Chart/AiStructureGate.cs::.ctor::Clone"] = "The structure validator mutates private scratch data; proposals become separate actions.",
        ["DotCharter.Services/Ai/Chart/AiChartRuleGate.cs::.ctor::CloneArrangement"] = "The rule gate owns disposable scratch levels and never installs them on the live chart.",
        ["DotCharter.Services/Ai/Chart/AiChartPassRun.cs::RunCoreAsync::CloneArrangement"] = "AI reads a worker snapshot; accepted proposals are translated into live-target actions.",
        ["DotCharter.Services/Ai/Chart/AiChartPassRun.cs::RunStructureAsync::Clone"] = "AI structure requests read a copy; acceptance builds actions against the current owners.",
        ["DotCharter.Services/Ai/Chart/AiChartPassRun.cs::RunExplainAsync::Clone"] = "Explanation reads a copy and never proposes or applies chart mutations.",
        ["DotCharter.Services/Export/Rocksmith/Rs7Projection.cs::.ctor::CloneArrangement"] = "RS7 projection transforms an output graph without restoring it into the editor.",
        ["DotCharter.Services/Package/DcSong/DcSongChart.cs::ForPackage::Clone"] = "Song packaging serializes a disposable graph and never replays it as history.",
        ["DotCharter.Services/Parsing/Rscp/RscpWriter.cs::RegeneratedArrangements::CloneArrangement"] = "Writer-side regeneration affects output only, never the editor graph.",
        ["DotCharter.Services/Export/DynamicDifficulty/DlcBuilderBuildSteps.cs::Apply::CloneArrangement"] = "Derived build transforms operate on an export copy, not a history snapshot.",
        ["DotCharter.Services/Infrastructure/Recovery/CrashRecoveryService.cs::WriteSnapshotAsync::Clone"] = "Crash recovery serializes an isolated graph and loads a new session on recovery.",
        ["DotCharter.Services/Editing/Quality/QualityReportSnapshot.cs::Take::Clone"] = "Quality reporting reads private scratch data; fixes use separate live-target actions.",
        ["DotCharter.Services/Editing/Quality/QualityReportCopies.cs::Copy::Clone"] = "The spare report graph is overwritten for analysis and is never installed as history.",
        ["DotCharter.Services/Audio/LivePlay/DynamicDifficultyLadder.cs::For::CloneArrangement"] = "Gameplay builds a derived ladder and never restores it into editing history.",
        ["DotCharter.UI/ViewModels/Shell/ExportMenuViewModel.cs::ExportEditorOnFire::Clone"] = "The output writer reads an isolated graph; it cannot mutate the editor owners.",
        ["DotCharter.UI/ViewModels/Shell/ExportMenuViewModel.cs::ExportSheetPdf::Clone"] = "PDF rendering reads an immutable worker graph and never commits it to history.",
        ["DotCharter.UI/ViewModels/Shell/ExportMenuViewModel.cs::SnapshotForExport::CloneArrangement"] = "Selected-arrangement export reads an independent output graph only.",
        ["DotCharter.UI/ViewModels/Shell/ExportMenuViewModel.cs::SnapshotForExport::CloneLevel"] = "An orphan selected level is copied for output only and is never restored to history.",
        ["DotCharter.UI/Views/Shell/MainWindowDiagnostics.cs::RunEditGcProbe::Clone"] = "The explicit diagnostic measures a disposable copy and never records it as history.",
        ["DotCharter.UI/ViewModels/Editor/Inspector/ExportReadinessViewModel.ChartReport.cs::InitializeChartReport::Clone"] = "The readiness report receives a read-only snapshot; chart fixes create live-target actions.",
    };

    [Fact]
    public void Every_product_snapshot_retains_identities_or_is_an_audited_detached_copy()
    {
        var calls = ProductCalls().ToList();
        Assert.NotEmpty(calls);
        var failures = new List<string>();
        foreach (var call in calls)
        {
            output.WriteLine($"{call.Key}:{call.Line} retained={call.Retained}");
            if (call.Retained) continue;
            if (call.MethodGroup || call.HistoryOwned || !DetachedCopies.ContainsKey(call.Key))
                failures.Add($"{call.Key}:{call.Line}: history/helper snapshots require retainIdentities: true");
        }

        Assert.True(DetachedCopies.Count <= DetachedCopyCeiling, "The detached exception set may only shrink.");
        foreach (var (key, reason) in DetachedCopies)
        {
            Assert.False(string.IsNullOrWhiteSpace(reason));
            var count = calls.Count(c => !c.Retained && c.Key == key);
            if (count != 1) failures.Add($"{key}: expected one audited detached call, found {count}; remove stale exceptions");
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public void Link_follow_up_capture_and_restore_use_the_identity_snapshot_boundary()
    {
        var path = "DotCharter.Services/State/LinkFollowUpAction.cs";
        var tree = Parse(File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), path)));
        var capture = Assert.Single(Calls(tree, path), c => c.Key.Contains("::Capture::", StringComparison.Ordinal));
        Assert.True(capture.Retained);
        Assert.EndsWith("::CloneLinkGroups", capture.Key);
        var restore = Assert.Single(tree.DescendantNodes().OfType<MethodDeclarationSyntax>(), m => m.Identifier.ValueText == "Restore");
        var calls = restore.DescendantNodes().OfType<InvocationExpressionSyntax>().ToList();
        Assert.Contains(calls, c => c.Expression.ToString() == "ChartProjectRestorer.RestoreLinkGroups");
        Assert.DoesNotContain(calls, c => c.Expression is MemberAccessExpressionSyntax m &&
            m.Name.Identifier.ValueText is "Clone" or "Clear" or "Add");
    }

    [Fact]
    public void All_public_snapshot_factories_are_classified()
    {
        var factories = typeof(ChartProjectCloner).GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(m => m.ReturnType == typeof(ChartProject) || m.ReturnType == typeof(Arrangement) || m.ReturnType == typeof(ChartLevel) || m.ReturnType == typeof(List<LinkGroup>))
            .Select(m => m.Name).Distinct().Order(StringComparer.Ordinal);
        Assert.Equal(CloneMethods.Order(StringComparer.Ordinal), factories);
    }

    [Theory]
    [InlineData("class NewAction : IChartAction { void Execute() { var s = ChartProjectCloner.Clone(p); } }")]
    [InlineData("using C = DotCharter.Core.Utilities.ChartProjectCloner; class NewAction : IChartAction { void Execute() { var s = C.Clone(p, false); } }")]
    [InlineData("using static DotCharter.Core.Utilities.ChartProjectCloner; class NewAction : IChartAction { void Execute() { var s = CloneArrangement(a); } }")]
    [InlineData("class Helper { void Capture() { var s = global::DotCharter.Core.Utilities.ChartProjectCloner.CloneLevel(l, retainIdentities: false); } }")]
    [InlineData("class Helper { void Capture() { var s = ChartProjectCloner.Clone(p, keepIds); } }")]
    [InlineData("class NewAction : IChartAction { void Execute() { var factory = ChartProjectCloner.Clone; } }")]
    [InlineData("using static DotCharter.Core.Utilities.ChartProjectCloner; class NewAction : IChartAction { void Execute() { var factory = Clone; } }")]
    public void Unclassified_history_actions_and_helpers_cannot_take_detached_snapshots(string source)
    {
        var call = Assert.Single(Calls(Parse(source), "new.cs"));
        Assert.False(call.Retained);
        Assert.False(DetachedCopies.ContainsKey(call.Key));
    }

    [Theory]
    [InlineData("class A { void Save() { var s = ChartProjectCloner.Clone(p, true); } }")]
    [InlineData("using C = DotCharter.Core.Utilities.ChartProjectCloner; class A { void Save() { var s = C.CloneArrangement(a, retainIdentities: true); } }")]
    [InlineData("using static DotCharter.Core.Utilities.ChartProjectCloner; class A { void Save() { var s = CloneLevel(l, retainIdentities: true); } }")]
    public void Retained_snapshot_arguments_are_recognized(string source) =>
        Assert.True(Assert.Single(Calls(Parse(source), "new.cs")).Retained);

    [Fact]
    public void Global_aliases_are_checked_and_comments_strings_and_nameof_are_not_calls()
    {
        var tree = Parse("class A { void Save() { var s = GlobalClone.Clone(p); } }");
        Assert.False(Assert.Single(Calls(tree, "new.cs", ["GlobalClone"])).Retained);
        var nonCalls = Parse("class A { string S = \"ChartProjectCloner.Clone(p)\"; string N = nameof(ChartProjectCloner.Clone); /* ChartProjectCloner.Clone(p) */ }");
        Assert.Empty(Calls(nonCalls, "new.cs"));
    }

    private static CompilationUnitSyntax Parse(string source) =>
        CSharpSyntaxTree.ParseText(source, cancellationToken: TestContext.Current.CancellationToken)
            .GetCompilationUnitRoot(TestContext.Current.CancellationToken);

    private sealed record SnapshotCall(string Key, int Line, bool Retained, bool MethodGroup, bool HistoryOwned);

    private static IEnumerable<SnapshotCall> ProductCalls()
    {
        var root = TestPaths.RepoRoot();
        var projects = Directory.EnumerateDirectories(root, "DotCharter.*")
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "tools"), "DotCharter.*.csproj", SearchOption.AllDirectories)
                .Select(p => Path.GetDirectoryName(p)!))
            .Where(p => !Path.GetFileName(p).Contains(".Tests", StringComparison.Ordinal)).Distinct();
        foreach (var project in projects)
        {
            var trees = Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories)
                .Where(p => !p.Replace('\\', '/').Contains("/obj/", StringComparison.Ordinal) &&
                            !p.Replace('\\', '/').Contains("/bin/", StringComparison.Ordinal))
                .Select(p => (Path: p, Source: File.ReadAllText(p)))
                .Where(p => p.Source.Contains(nameof(ChartProjectCloner), StringComparison.Ordinal) ||
                            CloneMethods.Any(m => p.Source.Contains(m, StringComparison.Ordinal)))
                .Select(p => (p.Path, Root: Parse(p.Source))).ToList();
            var globalAliases = trees.SelectMany(t => t.Root.Usings)
                .Where(u => u.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword) && IsCloner(u.Name) && u.Alias != null)
                .Select(u => u.Alias!.Name.Identifier.ValueText).ToList();
            var globalStatic = trees.SelectMany(t => t.Root.Usings)
                .Any(u => u.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword) && IsCloner(u.Name) && u.StaticKeyword.IsKind(SyntaxKind.StaticKeyword));
            foreach (var (path, tree) in trees)
            {
                foreach (var call in Calls(tree, Path.GetRelativePath(root, path).Replace('\\', '/'), globalAliases, globalStatic))
                    yield return call;
            }
        }
    }

    private static bool IsCloner(NameSyntax? name) => name?.GetLastToken().ValueText == nameof(ChartProjectCloner);

    private static IEnumerable<SnapshotCall> Calls(CompilationUnitSyntax tree, string path,
        IEnumerable<string>? globalAliases = null, bool globalStatic = false)
    {
        var aliases = new HashSet<string>(globalAliases ?? [], StringComparer.Ordinal) { nameof(ChartProjectCloner) };
        foreach (var directive in tree.Usings.Where(u => u.Alias != null && IsCloner(u.Name)))
            aliases.Add(directive.Alias!.Name.Identifier.ValueText);
        var staticImport = globalStatic || tree.Usings.Any(u => IsCloner(u.Name) && u.StaticKeyword.IsKind(SyntaxKind.StaticKeyword));
        foreach (var invocation in tree.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var method = invocation.Expression switch
            {
                MemberAccessExpressionSyntax m when aliases.Contains(m.Expression.GetLastToken().ValueText) => m.Name.Identifier.ValueText,
                SimpleNameSyntax n when staticImport && CloneMethods.Contains(n.Identifier.ValueText) => n.Identifier.ValueText,
                _ => null
            };
            if (method == null) continue;
            var args = invocation.ArgumentList.Arguments;
            var keep = args.FirstOrDefault(a => a.NameColon?.Name.Identifier.ValueText == "retainIdentities")
                       ?? (args.Count > 1 ? args[1] : null);
            yield return Site(invocation, path, method, keep?.Expression.IsKind(SyntaxKind.TrueLiteralExpression) == true, false);
        }

        foreach (var member in tree.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
        {
            if (!aliases.Contains(member.Expression.GetLastToken().ValueText) || !CloneMethods.Contains(member.Name.Identifier.ValueText)) continue;
            if (member.Parent is InvocationExpressionSyntax) continue;
            if (member.Ancestors().OfType<InvocationExpressionSyntax>().Any(i => i.Expression is IdentifierNameSyntax n && n.Identifier.ValueText == "nameof")) continue;
            yield return Site(member, path, member.Name.Identifier.ValueText, false, true);
        }

        if (!staticImport) yield break;
        foreach (var name in tree.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            if (!CloneMethods.Contains(name.Identifier.ValueText) || name.Parent is MemberAccessExpressionSyntax) continue;
            if (name.Parent is InvocationExpressionSyntax call && call.Expression == name) continue;
            if (name.Ancestors().OfType<InvocationExpressionSyntax>().Any(i => i.Expression is IdentifierNameSyntax n && n.Identifier.ValueText == "nameof")) continue;
            yield return Site(name, path, name.Identifier.ValueText, false, true);
        }
    }

    private static SnapshotCall Site(SyntaxNode node, string path, string method, bool retained, bool group)
    {
        var owner = node.Ancestors().OfType<BaseMethodDeclarationSyntax>().FirstOrDefault();
        var member = owner switch
        {
            MethodDeclarationSyntax m => m.Identifier.ValueText,
            ConstructorDeclarationSyntax => ".ctor",
            _ => node.Ancestors().OfType<PropertyDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText ?? "<initializer>"
        };
        var history = node.Ancestors().OfType<TypeDeclarationSyntax>().Any(t => t.BaseList?.Types.Any(b =>
            b.Type.ToString().EndsWith("IChartAction", StringComparison.Ordinal) || b.Type.ToString().EndsWith("IMergeableAction", StringComparison.Ordinal)) == true);
        return new SnapshotCall($"{path}::{member}::{method}", node.GetLocation().GetLineSpan().StartLinePosition.Line + 1, retained, group, history);
    }
}
