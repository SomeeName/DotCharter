using DotCharter.Services.Update;

namespace DotCharter.Tests.Services.Update;

/// <summary>
///     Recovery for an update that was staged but never applied.
///     <para>
///         <b>The failure this exists for.</b> The updater script hung before
///         copying anything (a console-less <c>cmd.exe</c> deadlocks on a
///         pipeline — see <c>UpdaterBreakawayGuardTests</c>). The app had already
///         closed, so on the next start the user was looking at the OLD version
///         with a full, valid payload sitting in <c>update-staging</c> and no way
///         to reach it. They copied the files over the install by hand.
///     </para>
///     <para>
///         Everything here is file state — no process is ever launched, which is
///         also why <c>ApplyDownloadedUpdate</c> is not called: it ends in a real
///         <c>cmd.exe</c>. The staged tree is written directly, exactly as the
///         apply path leaves it.
///     </para>
/// </summary>
public sealed class PendingUpdateApplyTests
{
    private const string SelfContainedConfig =
        """{"runtimeOptions":{"tfm":"net10.0","includedFrameworks":[{"name":"Microsoft.NETCore.App","version":"10.0.9"}]}}""";

    private const string FrameworkDependentConfig =
        """{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}""";

    private static void WritePayload(string dir, string runtimeConfig)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, AppPayload.ExeName), [0x4D, 0x5A]);
        File.WriteAllText(Path.Combine(dir, AppPayload.RuntimeConfigName), runtimeConfig);
        File.WriteAllText(Path.Combine(dir, AppPayload.DepsName), "{}");
        File.WriteAllBytes(Path.Combine(dir, AppPayload.HostFxrName), [0x4D, 0x5A]);
        File.WriteAllBytes(Path.Combine(dir, AppPayload.CoreClrName), [0x4D, 0x5A]);
    }

    /// <summary>An install with a payload staged under it, exactly as the apply
    /// path leaves things the moment before the updater is launched.</summary>
    private static string StageAnUpdate(
        string install, string payloadConfig = SelfContainedConfig, IReadOnlyList<string>? deletions = null)
    {
        WritePayload(install, SelfContainedConfig);

        var payloadDir = Path.Combine(install, UpdaterScriptBuilder.StagingDirName, "DotCharter-v0.46.0-win-x64");
        WritePayload(payloadDir, payloadConfig);

        var zipPath = Path.Combine(install, "update.zip");
        File.WriteAllText(zipPath, "not really a zip, but the record points at it");

        var state = new UpdateApplyState
        {
            PayloadDir = payloadDir,
            StagingRoot = UpdaterScriptBuilder.StagingRootFor(install),
            ZipPath = zipPath,
            Deletions = deletions ?? [],
            StagedUtc = new DateTimeOffset(2026, 8, 8, 21, 30, 0, TimeSpan.Zero),
            TargetVersion = "0.46.0",
        };
        File.WriteAllText(UpdaterScriptBuilder.PendingStatePathFor(install), state.ToJson());
        File.WriteAllText(UpdaterScriptBuilder.LegacyScriptPathFor(install), "@echo off\r\n");
        return payloadDir;
    }

    private static GitHubUpdateService ServiceFor(string install)
        => new(new Version(0, 45, 0), installDir: install, downloadDir: install);

    [Fact]
    public void AnInstallWithNothingStaged_ReportsNothingPending()
    {
        using var temp = new TempDirectory();
        WritePayload(temp.Path, SelfContainedConfig);
        using var service = ServiceFor(temp.Path);

        Assert.Null(service.GetPendingUpdateApply());
    }

    [Fact]
    public void AStagedPayload_IsFoundAndResumable()
    {
        using var temp = new TempDirectory();
        var payloadDir = StageAnUpdate(temp.Path);
        using var service = ServiceFor(temp.Path);

        var pending = service.GetPendingUpdateApply();

        Assert.NotNull(pending);
        Assert.True(pending!.CanResume);
        Assert.Null(pending.Problem);
        Assert.Equal(payloadDir, pending.State.PayloadDir);
        Assert.Equal("0.46.0", pending.State.TargetVersion);
    }

    /// <summary>
    ///     <c>PayloadDeletions.TakeFrom</c> REMOVES <c>payload-deletions.txt</c>
    ///     from the staged payload so it is never copied into the install — which
    ///     means a resume that only had the staging tree could not reconstruct the
    ///     list, and every file the new version dropped would survive the no-purge
    ///     copy forever. The record carries them instead.
    /// </summary>
    [Fact]
    public void TheDeletionsSurviveInTheRecord_BecauseThePayloadNoLongerCarriesThem()
    {
        using var temp = new TempDirectory();
        var payloadDir = StageAnUpdate(temp.Path);

        // The real staging sequence: the payload ships the list, and TakeFrom
        // reads it and REMOVES it so it is never copied into the install.
        File.WriteAllText(Path.Combine(payloadDir, PayloadDeletions.FileName),
            PayloadDeletions.Write(["runtimes/win-x64/native/gone.dll"]));
        var taken = PayloadDeletions.TakeFrom(payloadDir);
        Assert.Equal(["runtimes/win-x64/native/gone.dll"], taken);
        Assert.False(File.Exists(Path.Combine(payloadDir, PayloadDeletions.FileName)),
            "TakeFrom must remove the list from the payload — that is why the record has to carry it");

        // …so the ONLY surviving copy is the one in the record, and a resume that
        // lost it would leave every dropped file in the install forever (the copy
        // runs with no purge).
        var state = new UpdateApplyState
        {
            PayloadDir = payloadDir,
            StagingRoot = UpdaterScriptBuilder.StagingRootFor(temp.Path),
            ZipPath = Path.Combine(temp.Path, "update.zip"),
            Deletions = taken,
            StagedUtc = DateTimeOffset.UtcNow,
        };
        File.WriteAllText(UpdaterScriptBuilder.PendingStatePathFor(temp.Path), state.ToJson());

        using var service = ServiceFor(temp.Path);
        Assert.Equal(["runtimes/win-x64/native/gone.dll"], service.GetPendingUpdateApply()!.State.Deletions);
    }

    [Fact]
    public void AStagedPayloadThatIsGone_IsReportedUnresumableWithAReason()
    {
        using var temp = new TempDirectory();
        StageAnUpdate(temp.Path);
        Directory.Delete(UpdaterScriptBuilder.StagingRootFor(temp.Path), recursive: true);
        using var service = ServiceFor(temp.Path);

        var pending = service.GetPendingUpdateApply();

        Assert.NotNull(pending);
        Assert.False(pending!.CanResume);
        Assert.Contains("gone", pending.Problem);
    }

    /// <summary>
    ///     The same guard the original apply ran. A framework-dependent payload
    ///     copied over a self-contained install strands the old <c>hostfxr.dll</c>
    ///     next to a new runtimeconfig and the app dies with "You must install
    ///     .NET" — unfixable by installing the runtime. A resume is just as
    ///     irreversible as a first apply, so it re-checks rather than trusting the
    ///     record.
    /// </summary>
    [Fact]
    public void ACrossLayoutStagedPayload_IsUnresumableAndResumeThrows()
    {
        using var temp = new TempDirectory();
        StageAnUpdate(temp.Path, payloadConfig: FrameworkDependentConfig);
        using var service = ServiceFor(temp.Path);

        var pending = service.GetPendingUpdateApply();
        Assert.NotNull(pending);
        Assert.False(pending!.CanResume);
        Assert.Contains("framework-dependent", pending.Problem);

        var error = Assert.Throws<InvalidOperationException>(() => service.ResumePendingUpdateApply());
        Assert.Contains("cannot be finished", error.Message);
    }

    /// <summary>A record we cannot parse still means a staging tree is sitting on
    /// this disk. Reporting nothing would leave it there forever.</summary>
    [Fact]
    public void AnUnreadableRecord_StillSurfacesSoItCanBeDiscarded()
    {
        using var temp = new TempDirectory();
        StageAnUpdate(temp.Path);
        File.WriteAllText(UpdaterScriptBuilder.PendingStatePathFor(temp.Path), "{ this is not json");
        using var service = ServiceFor(temp.Path);

        var pending = service.GetPendingUpdateApply();

        Assert.NotNull(pending);
        Assert.False(pending!.CanResume);
        Assert.Contains("could not be read", pending.Problem);
    }

    /// <summary>The transcript is the one file worth asking a user for, so the UI
    /// gets its tail without having to know where it lives.</summary>
    [Fact]
    public void TheUpdaterTranscriptTail_IsCarriedToTheCaller()
    {
        using var temp = new TempDirectory();
        StageAnUpdate(temp.Path);
        File.WriteAllLines(UpdaterScriptBuilder.LogPathFor(temp.Path),
            ["[..] DotCharter updater started.", "[..] TIMEOUT - pid 1234 was still running after 300 seconds."]);
        using var service = ServiceFor(temp.Path);

        var pending = service.GetPendingUpdateApply();

        Assert.Equal(UpdaterScriptBuilder.LogPathFor(temp.Path), pending!.LogPath);
        Assert.Contains("TIMEOUT", pending.LogTail);
    }

    /// <summary>
    ///     Discard clears the payload, the script and the record — but KEEPS the
    ///     transcript. Deleting that with the rest would destroy the only evidence
    ///     of why the update failed at the exact moment the user gives up on it.
    /// </summary>
    [Fact]
    public void Discard_RemovesThePayloadTheScriptAndTheRecord_ButKeepsTheTranscript()
    {
        using var temp = new TempDirectory();
        StageAnUpdate(temp.Path);
        File.WriteAllText(UpdaterScriptBuilder.LogPathFor(temp.Path), "[..] COPY FAILED\r\n");
        using var service = ServiceFor(temp.Path);

        service.DiscardPendingUpdateApply();

        Assert.False(Directory.Exists(UpdaterScriptBuilder.StagingRootFor(temp.Path)));
        Assert.False(File.Exists(UpdaterScriptBuilder.LegacyScriptPathFor(temp.Path)));
        Assert.False(File.Exists(UpdaterScriptBuilder.PendingStatePathFor(temp.Path)));
        Assert.False(File.Exists(Path.Combine(temp.Path, "update.zip")));
        Assert.True(File.Exists(UpdaterScriptBuilder.LogPathFor(temp.Path)));

        // …and the install itself is untouched.
        Assert.True(File.Exists(Path.Combine(temp.Path, AppPayload.ExeName)));
        Assert.Null(service.GetPendingUpdateApply());
    }

    /// <summary>
    ///     A NEW apply replaces the staging tree, so a record left by an earlier
    ///     attempt describes a payload that no longer exists the moment
    ///     <c>ApplyDownloadedUpdate</c> starts — and every refusal below that
    ///     point (a bad archive, contraband, a layout mismatch) throws with the
    ///     tree already gone. The stale record used to survive, so one unrelated
    ///     failed download left the app warning on every start that "an update was
    ///     staged" and that "the staged payload is gone".
    ///     <para>
    ///         The zip here is deliberately not a zip: the apply throws while
    ///         checking the archive bounds, long before anything is launched, which
    ///         is what makes this runnable in the default gate at all.
    ///     </para>
    /// </summary>
    [Fact]
    public void AnApplyThatIsRefused_LeavesNoStaleRecordFromTheEarlierAttempt()
    {
        using var temp = new TempDirectory();
        StageAnUpdate(temp.Path);
        using var service = ServiceFor(temp.Path);
        Assert.NotNull(service.GetPendingUpdateApply()); // the earlier attempt's record

        var junk = Path.Combine(temp.Path, "not-an-archive.zip");
        File.WriteAllText(junk, "PK-ish, but not a zip");

        service.TrustDownloadForTests(junk);
        Assert.ThrowsAny<Exception>(() => service.ApplyDownloadedUpdate(junk));

        // Nothing is staged any more, so nothing may claim to be.
        Assert.False(Directory.Exists(UpdaterScriptBuilder.StagingRootFor(temp.Path)));
        Assert.False(File.Exists(UpdaterScriptBuilder.PendingStatePathFor(temp.Path)));
        Assert.Null(service.GetPendingUpdateApply());
    }

    [Fact]
    public void Resume_WithNothingStaged_Throws()
    {
        using var temp = new TempDirectory();
        WritePayload(temp.Path, SelfContainedConfig);
        using var service = ServiceFor(temp.Path);

        Assert.Throws<InvalidOperationException>(() => service.ResumePendingUpdateApply());
    }

    // ---- the record itself ---------------------------------------------------

    [Fact]
    public void TheRecord_RoundTrips()
    {
        var state = new UpdateApplyState
        {
            PayloadDir = @"C:\Games\DotCharter\update-staging\DotCharter-v0.46.0-win-x64",
            StagingRoot = @"C:\Games\DotCharter\update-staging",
            ZipPath = @"C:\Users\Test\updates\DotCharter-v0.46.0-win-x64.zip",
            Deletions = ["a/b.dll", "c.dll"],
            StagedUtc = new DateTimeOffset(2026, 8, 8, 21, 30, 0, TimeSpan.Zero),
            TargetVersion = "0.46.0",
        };

        var parsed = UpdateApplyState.TryParse(state.ToJson());

        Assert.NotNull(parsed);
        Assert.Equal(state.PayloadDir, parsed!.PayloadDir);
        Assert.Equal(state.StagingRoot, parsed.StagingRoot);
        Assert.Equal(state.ZipPath, parsed.ZipPath);
        Assert.Equal(state.Deletions, parsed.Deletions);
        Assert.Equal(state.StagedUtc, parsed.StagedUtc);
        Assert.Equal(state.TargetVersion, parsed.TargetVersion);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    // No payload directory: half a record must never drive a copy over an install.
    [InlineData("""{"SchemaVersion":1,"PayloadDir":""}""")]
    // A schema this build does not understand.
    [InlineData("""{"SchemaVersion":99,"PayloadDir":"C:\\x"}""")]
    [InlineData("""{"SchemaVersion":0,"PayloadDir":"C:\\x"}""")]
    public void TheRecord_RefusesAnythingItCannotFullyUnderstand(string json) => Assert.Null(UpdateApplyState.TryParse(json));
}
