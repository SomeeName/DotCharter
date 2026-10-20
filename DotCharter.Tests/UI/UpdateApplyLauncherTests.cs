using DotCharter.Localization;
using DotCharter.Services.Update;

namespace DotCharter.Tests.UI;

/// <summary>
///     "Apply the update, THEN close the app" — and only in that order.
///     <para>
///         The field failure was the app closing while nothing was going to copy
///         the payload: the user was left staring at the old version with a full
///         download in <c>update-staging</c> and no way to reach it. Closing the
///         app is irreversible from the user's point of view, so the ordering is
///         a safety property and not a detail, and it has to hold at all three
///         call sites (GitHub install, LAN install, finish-interrupted).
///     </para>
/// </summary>
public sealed class UpdateApplyLauncherTests
{
    private static readonly ILocalizationService Loc = FakeLocalization.English();

    [Fact]
    public void WhenTheUpdaterStarts_TheAppIsClosedAndNothingIsReported()
    {
        var closed = 0;
        var logger = new FakeAppLogger();

        var failure = UpdateApplyLauncher.ApplyAndCloseOrReport(
            () => { }, () => closed++, Loc, logger, "test");

        Assert.Null(failure);
        Assert.Equal(1, closed);
    }

    /// <summary>
    ///     The whole point. A launch failure must leave the session ALIVE — the
    ///     payload is staged and valid, and the app is the only thing that can
    ///     still offer to finish it.
    /// </summary>
    [Fact]
    public void WhenTheUpdaterFailsToStart_TheAppIsNotClosedAndTheUserIsTold()
    {
        var closed = 0;
        var logger = new FakeAppLogger();

        var failure = UpdateApplyLauncher.ApplyAndCloseOrReport(
            () => throw new UpdaterLaunchException("boom", new InvalidOperationException("inner")),
            () => closed++, Loc, logger, "test");

        Assert.Equal(0, closed);
        Assert.Equal(Loc.Format("Update_UpdaterLaunchFailed", "boom"), failure);
        Assert.Contains(logger.Entries, e => e.Level == "ERROR");
    }

    /// <summary>A launch failure reads differently from "the update failed" —
    /// the payload is still there and the recovery route is real.</summary>
    [Fact]
    public void ALaunchFailureIsNotReportedAsAnOrdinaryUpdateFailure()
    {
        var logger = new FakeAppLogger();

        var launch = UpdateApplyLauncher.ApplyAndCloseOrReport(
            () => throw new UpdaterLaunchException("boom", new InvalidOperationException("inner")),
            () => { }, Loc, logger, "test");
        var ordinary = UpdateApplyLauncher.ApplyAndCloseOrReport(
            () => throw new InvalidDataException("boom"), () => { }, Loc, logger, "test");

        Assert.NotEqual(launch, ordinary);
        Assert.Equal(Loc.Format("Update_DownloadFailed", "boom"), ordinary);
    }

    [Fact]
    public void AnyOtherApplyFailure_AlsoKeepsTheAppAlive()
    {
        var closed = 0;
        var logger = new FakeAppLogger();

        var failure = UpdateApplyLauncher.ApplyAndCloseOrReport(
            () => throw new InvalidDataException("refusing to apply"),
            () => closed++, Loc, logger, "test");

        Assert.Equal(0, closed);
        Assert.NotNull(failure);
    }

    // ---- the ordering has to hold at EVERY call site, not just the tested one ----

    /// <summary>
    ///     The tests above prove the launcher orders things correctly. This proves
    ///     nothing goes AROUND it: a second inline "apply, then close" is exactly
    ///     the shape that shipped, and it would pass every test above while
    ///     failing in the field. Scans the whole UI project rather than naming
    ///     files, because a file split silently defeats a path-based lint.
    /// </summary>
    [Fact]
    public void EveryCallSiteThatAppliesAnUpdate_GoesThroughTheLauncher()
    {
        var offenders = new List<string>();
        var applySites = 0;

        foreach (var (root, file) in ProductSources())
        {
            var code = StripComments(UiSourceTree.ReadAllText(file));
            // The interface DECLARES the two verbs, the service IMPLEMENTS them
            // and UpdateInstaller is the install half the service forwards them
            // to (#652); every other file that names them is a caller.
            if (code.Contains("interface IAppUpdateService", StringComparison.Ordinal)) continue;
            if (code.Contains(": IAppUpdateService", StringComparison.Ordinal)) continue;
            if (code.Contains("public sealed class UpdateInstaller", StringComparison.Ordinal)) continue;
            var appliesAnUpdate =
                code.Contains("ApplyDownloadedUpdate(", StringComparison.Ordinal) ||
                code.Contains("ResumePendingUpdateApply", StringComparison.Ordinal);
            if (!appliesAnUpdate) continue;
            applySites++;
            if (code.Contains("ApplyAndCloseOrReport", StringComparison.Ordinal)) continue;
            offenders.Add(Path.GetRelativePath(root, file));
        }

        Assert.True(applySites >= 2, $"only {applySites} apply sites found — the download dialog and the update flow both apply");
        Assert.True(offenders.Count == 0,
            "these files apply an update without going through UpdateApplyLauncher, so nothing stops them " +
            "closing the app when the updater never started: " + string.Join(", ", offenders));
    }

    /// <summary>
    ///     …and the other half: nothing may CALL the close directly. The launcher
    ///     receives it as a method group and invokes it only after a successful
    ///     launch; a direct call anywhere else is an unguarded exit.
    /// </summary>
    [Fact]
    public void NothingClosesTheAppForAnUpdateDirectly()
    {
        var offenders = ProductSources()
            .Where(t => StripComments(UiSourceTree.ReadAllText(t.File))
                .Contains("CloseApplication();", StringComparison.Ordinal))
            .Select(t => Path.GetRelativePath(t.Root, t.File))
            .ToList();

        Assert.True(offenders.Count == 0,
            "the update close must be handed to UpdateApplyLauncher, never invoked: " +
            string.Join(", ", offenders));
    }

    /// <summary>Every product source that can reach the update service: the UI, the Presentation layer and Services.</summary>
    private static IEnumerable<(string Root, string File)> ProductSources()
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(typeof(UpdateApplyLauncherTests).Assembly.Location)!);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "DotCharter.sln")))
            dir = dir.Parent;

        Assert.SkipWhen(dir == null, "Repository root not found from the test output directory.");
        foreach (var project in new[] { "DotCharter.UI", "DotCharter.Presentation", "DotCharter.Services", "DotCharter.Tone" })
        {
            var root = Path.Combine(dir!.FullName, project);
            Assert.True(Directory.Exists(root), $"{project} not found — this lint would pass vacuously.");
            foreach (var file in UiSourceTree.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) continue;
                yield return (root, file);
            }
        }
    }

    /// <summary>A <c>see cref</c> or a "do not do this" comment is exactly as inert
    /// as no mention at all, so neither may satisfy or trip these lints.</summary>
    private static string StripComments(string source)
    {
        var lines = source.Split('\n')
            .Select(line =>
            {
                var cut = line.IndexOf("//", StringComparison.Ordinal);
                return cut >= 0 ? line[..cut] : line;
            });
        return string.Join('\n', lines);
    }
}
