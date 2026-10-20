using System.Text.Json;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     <c>Get-HeadlessRaceRerunDecision</c> in <c>tools/ci/HeadlessShards.psm1</c>
///     — what <c>Invoke-Gate.ps1 -Headless</c>, and so <c>Merge-Train.ps1</c>,
///     does with a headless run's failures (#192): re-run the classes alone only
///     when EVERY failure carries a harness-fault needle and no class is absent;
///     stop on anything else. Executed against the module itself, over a TRX in
///     the shape the runner writes, with the real race stack.
/// </summary>
public sealed class HeadlessRaceRerunDecisionTests : IDisposable
{
    private readonly string _scratch = Path.Combine(
        Path.GetTempPath(), "dotcharter-race-rerun-" + Guid.NewGuid().ToString("N"));

    public HeadlessRaceRerunDecisionTests() => Directory.CreateDirectory(_scratch);

    public void Dispose() => BestEffortDelete.Directory(_scratch);

    // The stack the setup race leaves (#192, a real run's TRX, trimmed). The
    // session type's name is split in two because
    // HeadlessCollectionLintTests reads test source for that name to find
    // classes that boot a session, and this one only writes a TRX.
    private const string SessionType = "Headless" + "UnitTestSession";

    private const string RaceStack = """
           at Avalonia.Threading.Dispatcher.<VerifyAccess>g__ThrowVerifyAccess|17_0()
           at Avalonia.Rendering.DefaultRenderLoop.Add(IRenderLoopTask i)
           at Avalonia.Rendering.Composition.Server.ServerCompositor..ctor(IRenderLoop renderLoop, IPlatformGraphics platformGraphics, CompositionOptions options, BatchStreamObjectPool`1 batchObjectPool, BatchStreamMemoryPool batchMemoryPool)
           at Avalonia.Headless.AvaloniaHeadlessPlatform.Initialize(AvaloniaHeadlessPlatformOptions opts)
           at Avalonia.AppBuilder.SetupUnsafe()
        """ + "   at Avalonia.Headless." + SessionType + ".EnsureIsolatedApplication()";

    private const string RaceMessage =
        "System.InvalidOperationException : The calling thread cannot access this object because a different thread owns it.";

    private const string Gallery = "DotCharter.Tests.Perf.GalleryPagesUiPerfSmokeTests";
    private const string Settings = "DotCharter.Tests.Perf.SettingsConstructionUiPerfSmokeTests";

    [Fact]
    public void OnlyRaceFailures_AreReRun_ByClass()
    {
        var trx = WriteTrx(
            (Gallery + ".EveryGalleryPage(section: \"Rig\", page: \"A.B\")", RaceMessage, RaceStack),
            (Gallery + ".EveryGalleryPage(section: \"Rig\", page: \"C\")", RaceMessage, RaceStack),
            (Settings + ".EveryGroup(tabName: \"Keybinds\")", "System.InvalidProgramException : You've caused dispatcher loop", "   at Avalonia.Threading.Dispatcher.ResetForUnitTests()"));

        var decision = Decide(trx, absent: []);

        Assert.Equal("Rerun", decision.Action);
        Assert.Equal([Gallery, Settings], decision.Classes);
    }

    [Fact]
    public void ARaceFailureNextToAProductFailure_StopsTheRun_AndNamesTheProductFailure()
    {
        var trx = WriteTrx(
            (Gallery + ".EveryGalleryPage(section: \"Rig\", page: \"A\")", RaceMessage, RaceStack),
            (Settings + ".EveryGroup(tabName: \"Audio\")", "Assert.True() Failure: the row overflowed", $"   at {Settings}.EveryGroup(String tabName) in C:\\x\\SettingsConstructionUiPerfSmokeTests.cs:line 420"));

        var decision = Decide(trx, absent: []);

        Assert.Equal("Stop", decision.Action);
        Assert.Equal([Settings], decision.Classes);
        Assert.Contains("EveryGroup(tabName: \"Audio\")", decision.Reason);
    }

    [Fact]
    public void AnAbsentClass_StopsTheRun_EvenWhenEveryFailureIsTheRace()
    {
        var trx = WriteTrx((Gallery + ".EveryGalleryPage(section: \"Rig\", page: \"A\")", RaceMessage, RaceStack));

        var decision = Decide(trx, absent: [Settings]);

        Assert.Equal("Stop", decision.Action);
        Assert.Equal([Settings], decision.Classes);
    }

    [Fact]
    public void AGreenRun_Passes()
    {
        var decision = Decide(WriteTrx(), absent: []);

        Assert.Equal("Pass", decision.Action);
        Assert.Empty(decision.Classes);
    }

    /// <summary>The race message ALONE is not the signature: the same exception from a test body is a product bug.</summary>
    [Fact]
    public void TheThreadMessageWithoutTheSetupStack_IsNotTheRace()
    {
        var trx = WriteTrx((Gallery + ".EveryGalleryPage(section: \"Rig\", page: \"A\")", RaceMessage,
            $"   at Avalonia.Threading.Dispatcher.VerifyAccess()\n   at {Gallery}.EveryGalleryPage_BuildsAttachesAndMeasuresNonZero() in C:\\x\\GalleryPagesUiPerfSmokeTests.cs:line 57"));

        Assert.Equal("Stop", Decide(trx, absent: []).Action);
    }

    private sealed record Decision(string Action, string[] Classes, string Reason);

    private Decision Decide(string trx, string[] absent)
    {
        var absentList = string.Join(", ", absent.Select(a => $"'{a}'"));
        var json = HeadlessShardsModule.Run(_scratch, $$"""
            $failures = @(Read-HeadlessTrxFailures '{{trx.Replace("'", "''")}}')
            $d = Get-HeadlessRaceRerunDecision $failures @({{absentList}})
            [pscustomobject]@{ Action = $d.Action; Classes = (@($d.Classes) -join '|'); Reason = $d.Reason } | ConvertTo-Json -Compress
            """);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var classes = root.GetProperty("Classes").GetString() ?? "";
        return new Decision(
            root.GetProperty("Action").GetString()!,
            classes.Length == 0 ? [] : classes.Split('|'),
            root.GetProperty("Reason").GetString()!);
    }

    private string WriteTrx(params (string Name, string Message, string Stack)[] failures)
    {
        static string X(string s) => System.Security.SecurityElement.Escape(s);
        var results = string.Concat(failures.Select(f =>
            $"""<UnitTestResult testName="{X(f.Name)}" outcome="Failed"><Output><ErrorInfo><Message>{X(f.Message)}</Message><StackTrace>{X(f.Stack)}</StackTrace></ErrorInfo></Output></UnitTestResult>"""));
        var passed = $"""<UnitTestResult testName="{Gallery}.TheCatalogue_HasPages" outcome="Passed" />""";
        var total = failures.Length + 1;
        var path = Path.Combine(_scratch, Guid.NewGuid().ToString("N") + ".trx");
        File.WriteAllText(path,
            $"""<?xml version="1.0" encoding="utf-8"?><TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results>{results}{passed}</Results><ResultSummary outcome="{(failures.Length > 0 ? "Failed" : "Completed")}"><Counters total="{total}" executed="{total}" passed="1" failed="{failures.Length}" /></ResultSummary></TestRun>""");
        return path;
    }
}
