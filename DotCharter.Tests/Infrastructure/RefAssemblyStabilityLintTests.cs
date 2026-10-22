using System.Xml.Linq;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     Pins the pair of targets in <c>Directory.Build.targets</c> that keep a
///     XAML-compiled project's reference assembly timestamp when its public surface
///     did not change, so a comment-only edit in DotCharter.UI does not recompile
///     DotCharter.UI.Gallery and DotCharter.Tests.
/// </summary>
/// <remarks>
///     <para>
///         Avalonia's compile step rewrites <c>obj/refint</c> and deletes <c>obj/ref</c>
///         before the copy, so the SDK's MVID check never skips it and the copy always
///         takes a fresh timestamp. The pair records <c>obj/ref</c>'s hash and time before
///         that delete and puts the time back when the bytes are equal. Both halves
///         fail silently when broken: a <c>BeforeTargets</c> on a target that no longer
///         exists never runs, and a pair that stops restoring only shows up as a longer
///         incremental build. This lint reads the targets file and the pinned Avalonia
///         package, so a renamed Avalonia target or an edit to either half is caught.
///     </para>
/// </remarks>
public sealed class RefAssemblyStabilityLintTests
{
    private const string AvaloniaDeleteTarget = "AvaloniaDeleteRefAssemblyBeforeOutputCopy";

    private static XElement TargetsFile() =>
        XDocument.Load(Path.Combine(TestPaths.RepoRoot(), "Directory.Build.targets")).Root!;

    private static XElement Target(string name) =>
        TargetsFile().Elements().SingleOrDefault(e => e.Name.LocalName == "Target" && (string?)e.Attribute("Name") == name)
        ?? throw new Xunit.Sdk.XunitException(LintReport.Of(
            $"Directory.Build.targets must declare the target {name}.",
            "Directory.Build.targets",
            "restore the reference-assembly timestamp pair; without it a UI comment edit rebuilds Gallery and Tests."));

    [Fact]
    public void The_record_target_runs_before_Avalonias_delete_of_the_reference_assembly()
    {
        var record = Target("DotCharterRecordRefAssembly");

        Assert.Equal(AvaloniaDeleteTarget, (string?)record.Attribute("BeforeTargets"));
        Assert.Contains(record.Elements(), e => e.Name.LocalName == "GetFileHash");
        Assert.Contains("TargetRefPath", (string?)record.Attribute("Condition"), StringComparison.Ordinal);
        Assert.Contains("DotCharterKeepRefAssemblyStamp", (string?)record.Attribute("Condition"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_restore_target_touches_the_reference_assembly_only_when_the_bytes_are_equal()
    {
        var restore = Target("DotCharterRestoreRefAssemblyTime");

        Assert.Equal("CopyFilesToOutputDirectory", (string?)restore.Attribute("AfterTargets"));
        Assert.Contains("DotCharterKeepRefAssemblyStamp", (string?)restore.Attribute("Condition"), StringComparison.Ordinal);
        var touch = Assert.Single(restore.Elements(), e => e.Name.LocalName == "Touch");
        Assert.Equal("$(TargetRefPath)", (string?)touch.Attribute("Files"));
        Assert.Equal("$(_DotCharterRefTimeBefore)", (string?)touch.Attribute("Time"));
        var condition = (string?)touch.Attribute("Condition") ?? "";
        Assert.Contains("FileHash", condition, StringComparison.Ordinal);
        Assert.Contains("$(_DotCharterRefHashBefore)", condition, StringComparison.Ordinal);
        Assert.Contains("==", condition, StringComparison.Ordinal);
    }

    [Fact]
    public void The_pinned_Avalonia_package_still_has_the_delete_target_the_record_target_hooks()
    {
        var version = PinnedAvaloniaVersion();
        var packages = Environment.GetEnvironmentVariable("NUGET_PACKAGES")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        var targets = Path.Combine(packages, "avalonia", version, "buildTransitive", "AvaloniaBuildTasks.targets");

        Assert.True(File.Exists(targets), $"the restored Avalonia {version} package has no {targets}; restore the solution first, or this lint would pass vacuously.");
        var avalonia = XDocument.Load(targets).Root!;
        Assert.Contains(avalonia.Elements(), e => e.Name.LocalName == "Target" && (string?)e.Attribute("Name") == AvaloniaDeleteTarget);
    }

    private static string PinnedAvaloniaVersion()
    {
        var packages = XDocument.Load(Path.Combine(TestPaths.RepoRoot(), "Directory.Packages.props")).Root!;
        return packages.Descendants()
            .Where(e => e.Name.LocalName == "PackageVersion" && (string?)e.Attribute("Include") == "Avalonia")
            .Select(e => (string?)e.Attribute("Version"))
            .Single()!;
    }
}
