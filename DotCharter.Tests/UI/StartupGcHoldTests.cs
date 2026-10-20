using System.Runtime;
using System.Text.RegularExpressions;
using DotCharter.UI.Diagnostics;

namespace DotCharter.Tests.UI;

/// <summary>
///     The boot-path GC hold (0.49.6): never throws, tolerates a blown budget,
///     reports what it did, and is actually wired around the boot in
///     <c>App.OnFrameworkInitializationCompleted</c>.
/// </summary>
[Collection("CwdSerialized")] // a real no-GC region is process-wide state
public sealed class StartupGcHoldTests
{
    [Fact]
    public void BeginEnd_NeverThrow_AndReportAnOutcome()
    {
        StartupGcHold.End(); // idempotent without a Begin
        var began = StartupGcHold.Begin();
        try
        {
            // Allocate a little inside the hold; a parallel test may blow it,
            // which End must report rather than throw.
            var junk = new byte[4 << 20];
            GC.KeepAlive(junk);
        }
        finally
        {
            StartupGcHold.End();
        }
        Assert.NotEqual(GCLatencyMode.NoGCRegion, GCSettings.LatencyMode);
        Assert.Contains(StartupGcHold.Outcome, new[] { "held", "blown", "unavailable", "off" });
        Assert.True(began || StartupGcHold.Outcome is "unavailable" or "off");
        Assert.Matches(new Regex(@"^gc=(held|blown|unavailable|off) gc0=\d+ gc1=\d+ gc2=\d+$"), StartupGcHold.Describe());
        StartupGcHold.End(); // and again: still nothing thrown
    }

    [Fact]
    public void TheBoot_BeginsTheHold_AndEndsItAtInteractive_AndSaysSoOnThePhaseLine()
    {
        var app = SourceText.StripComments(UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "App.axaml.cs"))));
        var begin = app.IndexOf("StartupGcHold.Begin();", StringComparison.Ordinal);
        var ctor = app.IndexOf("new MainWindow()", StringComparison.Ordinal);
        var end = app.IndexOf("StartupGcHold.End();", StringComparison.Ordinal);
        var line = app.IndexOf("StartupPhases:", StringComparison.Ordinal);
        Assert.True(begin > 0 && begin < ctor, "the hold must begin before the window is constructed");
        Assert.True(end > ctor && end < line, "the hold must end in the interactive post, before the phase line is written");
        Assert.Contains("StartupGcHold.Describe()", app);
        // …and the no-desktop path (headless boots) ends it too.
        Assert.Equal(2, Regex.Matches(app, @"StartupGcHold\.End\(\);").Count);
    }
}
