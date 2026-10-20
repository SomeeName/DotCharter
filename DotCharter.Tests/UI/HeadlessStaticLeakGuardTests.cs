using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using DotCharter.Tests.Perf;

namespace DotCharter.Tests.UI;

/// <summary>
///     Pins <see cref="HeadlessStaticLeakGuard" /> in the DEFAULT gate: the
///     headless fixture runs it after every dispatch, it finds the test statics
///     that can hold a window, and it names one that still does.
///     <para>
///         #250: <c>UiPerfSmokeTests._window</c> kept the last released
///         <c>MainWindow</c> alive, and Avalonia Grid's thread-shared
///         <c>TempDefinitions</c> array let that dead window's Grids pin whichever
///         LATER app laid a Grid out last — so
///         <c>Perf/IsolatedAppReleaseUiPerfSmokeTests</c> failed only when it ran
///         after that class in the same process. The headless half (a leaking
///         class fails its own dispatch) is proven by that suite.
///     </para>
/// </summary>
public sealed class HeadlessStaticLeakGuardTests
{
    [Fact]
    public void TheHeadlessFixture_ChecksForLeakedStatics_AfterEveryRelease()
    {
        var code = SourceText.StripComments(File.ReadAllText(
            Path.Combine(TestPaths.RepoRoot(), "DotCharter.Tests", "Perf", "HeadlessAppFixture.cs")));

        var dispatches = Regex.Matches(code, @"Get\(\)\.Dispatch\(").Count;
        var guarded = Regex.Matches(code,
            @"finally\s*\{\s*DrainAppBackgroundWork\(\);\s*ReleaseIsolatedApp\(\);\s*\}\s*"
            + @"HeadlessStaticLeakGuard\.ThrowIfAnyStaticHoldsAReleasedApp\(\);").Count;

        Assert.True(dispatches >= 4, $"found {dispatches} Dispatch overloads — the lint lost its subject");
        Assert.True(dispatches == guarded,
            $"{dispatches} Dispatch overloads but {guarded} run HeadlessStaticLeakGuard right after the release — "
            + "a static that keeps a released window pins that app and, through Grid's TempDefinitions, the next one.");
    }

    [Fact]
    public void TheGuard_FindsTheTestStaticsThatCanHoldAWindowOrAnApp()
    {
        var names = HeadlessStaticLeakGuard.Fields.Select(f => $"{f.DeclaringType!.Name}.{f.Name}").ToList();

        Assert.Contains("UiPerfSmokeTests._window", names);
        Assert.Contains("UiPerfSmokeTests._windowApp", names);
        Assert.Contains($"{nameof(LeakProbe)}.{nameof(LeakProbe.Held)}", names);
        Assert.DoesNotContain(names, n => n.EndsWith(".Gate", StringComparison.Ordinal)); // a Lock is not a visual
    }

    [Fact]
    public void TheGuard_NamesAStaticThatStillHoldsAVisual()
    {
        var field = typeof(LeakProbe).GetField(nameof(LeakProbe.Held))!;
        try
        {
            LeakProbe.Held = null;
            Assert.Empty(HeadlessStaticLeakGuard.Leaks([field]));

            // Uninitialized: a real constructor would claim a dispatcher for this
            // gate thread. The guard only asks what the value IS.
            LeakProbe.Held = RuntimeHelpers.GetUninitializedObject(typeof(Border));
            Assert.Equal(["LeakProbe.Held (Border)"], HeadlessStaticLeakGuard.Leaks([field]));

            LeakProbe.Held = RuntimeHelpers.GetUninitializedObject(typeof(Application));
            Assert.Equal(["LeakProbe.Held (Application)"], HeadlessStaticLeakGuard.Leaks([field]));

            // Cleared after it is reported, so only the case that leaked fails.
            HeadlessStaticLeakGuard.Release([field]);
            Assert.Null(LeakProbe.Held);
        }
        finally
        {
            LeakProbe.Held = null;
        }
    }

    private static class LeakProbe
    {
        public static object? Held;
    }
}
