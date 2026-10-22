namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     The export's slow end-to-end scenarios, one nested class each. A nested class is its own
///     xunit collection, so the scenarios run side by side instead of back to back: every one
///     builds its own fixture repositories under its own scratch folder and runs pwsh with its
///     own release cache, so they share nothing.
/// </summary>
public sealed partial class PublicSnapshotExportTests
{
    /// <summary>Two exports on the previous snapshot: the one-commit shape, the tags, the gitlink.</summary>
    public sealed class OneCommitOnThePreviousSnapshot
    {
        [Fact]
        public void Export_IsOneCommitOnThePreviousSnapshot()
            => Export_IsOneCommitOnThePreviousSnapshot_WithoutTrailers_AndPointsAtThePublicJuceSharp();
    }

    /// <summary>The kept work tree, with its build outputs parked while the scans run.</summary>
    public sealed class KeptWorkTree
    {
        [Fact]
        public void Export_KeepsItsWorkTree()
            => Export_KeepsItsWorkTree_AndParksTheBuildOutputsWhileTheScansRun();
    }

    /// <summary>The release cache: a green gate on record is reused, and -ForceGate runs it anyway.</summary>
    public sealed class GateRecord
    {
        [Fact]
        public void Gate_ReusesAGreenGateOnRecord()
            => Gate_ReusesAGreenGateOnRecord_StillBuilds_AndForceGateRunsIt();
    }

    /// <summary>A push that holds pushes both repos, and an unchanged JuceSharp is left as it is.</summary>
    public sealed class PushThatHolds
    {
        [Fact]
        public void Push_WhenEverythingHolds_PushesBothRepos()
            => Push_WhenEverythingHolds_PushesBothRepos_AndAnUnchangedJuceSharpIsLeftAsItIs();
    }

    /// <summary>The refusals that come before the export's long steps (the gate, the leak scans).</summary>
    public sealed class EarlyPushRefusals
    {
        public static TheoryData<string, string> Rows()
        {
            var rows = new TheoryData<string, string>();
            foreach (var (failure, expected) in PushRefusals)
            {
                if (failure is not ("no-gitleaks" or "main-moved")) rows.Add(failure, expected);
            }

            return rows;
        }

        [Theory]
        [MemberData(nameof(Rows))]
        public void Push_Refuses_AndLeavesBothPublicReposUntouched(string failure, string expected)
            => PublicSnapshotExportTests.Push_Refuses_AndLeavesBothPublicReposUntouched(failure, expected);
    }

    /// <summary>The push with no gitleaks installed: the slowest refusal, so its own collection.</summary>
    public sealed class PushWithoutGitleaks
    {
        [Fact]
        public void Push_Refuses_AndLeavesBothPublicReposUntouched()
            => PublicSnapshotExportTests.Push_Refuses_AndLeavesBothPublicReposUntouched("no-gitleaks", RefusalMessage("no-gitleaks"));
    }

    /// <summary>The push whose target moves while the gate runs: the second slowest refusal.</summary>
    public sealed class PushWhoseTargetMoves
    {
        [Fact]
        public void Push_Refuses_AndLeavesBothPublicReposUntouched()
            => PublicSnapshotExportTests.Push_Refuses_AndLeavesBothPublicReposUntouched("main-moved", RefusalMessage("main-moved"));
    }
}
