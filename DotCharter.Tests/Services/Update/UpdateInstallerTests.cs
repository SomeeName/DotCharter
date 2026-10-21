using DotCharter.Services.Update;

namespace DotCharter.Tests.Services.Update;

/// <summary>
///     <see cref="UpdateInstaller" /> on its own, without a
///     <see cref="GitHubUpdateService" /> around it (#652): the reasons an install
///     refuses an in-place update, and the pending-apply record. File state in temp
///     directories only — nothing here reaches <c>LaunchUpdater</c>, which starts a
///     real <c>cmd.exe</c>.
/// </summary>
public sealed class UpdateInstallerTests
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

    /// <summary>The install and its staged payload, as the apply path leaves them
    /// the moment before the updater is launched.</summary>
    private static UpdateApplyState Stage(string install, string payloadConfig = SelfContainedConfig)
    {
        WritePayload(install, SelfContainedConfig);
        var payloadDir = Path.Combine(install, UpdaterScriptBuilder.StagingDirName, "DotCharter-v0.67.0-win-x64");
        WritePayload(payloadDir, payloadConfig);
        var zipPath = Path.Combine(install, "update.zip");
        File.WriteAllText(zipPath, "the record points at it");

        var state = new UpdateApplyState
        {
            PayloadDir = payloadDir,
            StagingRoot = UpdaterScriptBuilder.StagingRootFor(install),
            ZipPath = zipPath,
            Deletions = ["old.dll"],
            StagedUtc = new DateTimeOffset(2026, 10, 11, 9, 0, 0, TimeSpan.Zero),
            TargetVersion = "0.67.0",
        };
        File.WriteAllText(UpdaterScriptBuilder.PendingStatePathFor(install), state.ToJson());
        File.WriteAllText(UpdaterScriptBuilder.LegacyScriptPathFor(install), "@echo off\r\n");
        return state;
    }

    /// <summary>The installer as the service builds it: downloads live in the
    /// install (as on a portable install's <c>updates</c> folder), scripts in a
    /// scratch folder of the test's own.</summary>
    private static UpdateInstaller NewInstaller(string install)
        => new(install, downloadDir: install, scriptDir: Path.Combine(install, "scripts"));

    // ---- why an install refuses an in-place update -----------------------------

    [Fact]
    public void AFolderWithNoExe_IsNotAReleaseInstall()
    {
        using var temp = new TempDirectory();

        Assert.Equal(UpdateApplyBlockReason.NotAReleaseInstall, NewInstaller(temp.Path).GetApplyBlockReason());
    }

    [Fact]
    public void AnExeWithNoRuntimeConfig_IsAnUnknownLayout()
    {
        using var temp = new TempDirectory();
        File.WriteAllBytes(Path.Combine(temp.Path, AppPayload.ExeName), [0x4D, 0x5A]);

        Assert.Equal(UpdateApplyBlockReason.UnknownInstallLayout, NewInstaller(temp.Path).GetApplyBlockReason());
    }

    [Theory]
    [InlineData(SelfContainedConfig)]
    [InlineData(FrameworkDependentConfig)]
    public void AWritableReleaseInstall_IsNotBlocked(string runtimeConfig)
    {
        using var temp = new TempDirectory();
        WritePayload(temp.Path, runtimeConfig);

        Assert.Equal(UpdateApplyBlockReason.None, NewInstaller(temp.Path).GetApplyBlockReason());
        // The write probe cleans up after itself.
        Assert.Empty(Directory.GetFiles(temp.Path, ".dc-update-probe-*"));
    }

    /// <summary>A release install the user cannot write to (Program Files without
    /// elevation) is blocked, and an apply there refuses BEFORE it touches the
    /// staging tree or an earlier attempt's record.</summary>
    [Fact]
    public void AnInstallTheUserCannotWriteTo_IsBlocked_AndTheApplyRefusesBeforeTouchingAnything()
    {
        // NOTE: a bare `return` — CA1416 only recognizes an OperatingSystem.IsWindows()
        // early-return as the guard for the Windows-only ACL APIs below.
        if (!OperatingSystem.IsWindows()) return;

        using var temp = new TempDirectory();
        Stage(temp.Path);
        var installer = NewInstaller(temp.Path);
        Assert.Equal(UpdateApplyBlockReason.None, installer.GetApplyBlockReason());

        var install = new DirectoryInfo(temp.Path);
        var user = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        var deny = new System.Security.AccessControl.FileSystemAccessRule(
            user,
            System.Security.AccessControl.FileSystemRights.CreateFiles,
            System.Security.AccessControl.AccessControlType.Deny);
        var security = install.GetAccessControl();
        security.AddAccessRule(deny);
        install.SetAccessControl(security);
        try
        {
            Assert.Equal(UpdateApplyBlockReason.InstallDirNotWritable, installer.GetApplyBlockReason());

            var refusal = Assert.Throws<InvalidOperationException>(
                () => installer.ApplyDownloadedUpdate(Path.Combine(temp.Path, "update.zip"), new string('0', 64)));
            Assert.Contains(nameof(UpdateApplyBlockReason.InstallDirNotWritable), refusal.Message, StringComparison.Ordinal);
        }
        finally
        {
            var restore = install.GetAccessControl();
            restore.RemoveAccessRule(deny);
            install.SetAccessControl(restore);
        }

        // Refused at the gate: the earlier record and its staged payload are untouched.
        Assert.True(File.Exists(UpdaterScriptBuilder.PendingStatePathFor(temp.Path)));
        Assert.True(installer.GetPendingUpdateApply()!.CanResume);
    }

    // ---- the pending-apply record ----------------------------------------------

    [Fact]
    public void NothingStaged_IsNothingPending_AndResumeThrows()
    {
        using var temp = new TempDirectory();
        WritePayload(temp.Path, SelfContainedConfig);
        var installer = NewInstaller(temp.Path);

        Assert.Null(installer.GetPendingUpdateApply());
        Assert.Throws<InvalidOperationException>(installer.ResumePendingUpdateApply);
    }

    [Fact]
    public void AStagedPayload_IsPendingAndResumable_WithTheRecordItWasStagedWith()
    {
        using var temp = new TempDirectory();
        var staged = Stage(temp.Path);
        File.WriteAllLines(UpdaterScriptBuilder.LogPathFor(temp.Path),
            Enumerable.Range(1, 12).Select(i => $"line {i}"));

        var pending = NewInstaller(temp.Path).GetPendingUpdateApply();

        Assert.NotNull(pending);
        Assert.True(pending.CanResume);
        Assert.Null(pending.Problem);
        Assert.Equal(staged.PayloadDir, pending.State.PayloadDir);
        Assert.Equal(staged.ZipPath, pending.State.ZipPath);
        Assert.Equal(["old.dll"], pending.State.Deletions);
        Assert.Equal("0.67.0", pending.State.TargetVersion);
        Assert.Equal(UpdaterScriptBuilder.LogPathFor(temp.Path), pending.LogPath);
        // The last eight transcript lines, not the whole log.
        Assert.StartsWith("line 5", pending.LogTail, StringComparison.Ordinal);
        Assert.EndsWith("line 12", pending.LogTail, StringComparison.Ordinal);
    }

    [Fact]
    public void AStagedPayloadThatIsGone_IsPendingButNotResumable()
    {
        using var temp = new TempDirectory();
        var staged = Stage(temp.Path);
        Directory.Delete(staged.PayloadDir, recursive: true);
        var installer = NewInstaller(temp.Path);

        var pending = installer.GetPendingUpdateApply();

        Assert.NotNull(pending);
        Assert.False(pending.CanResume);
        Assert.Equal("the staged payload is gone", pending.Problem);
        var refusal = Assert.Throws<InvalidOperationException>(installer.ResumePendingUpdateApply);
        Assert.Contains("the staged payload is gone", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACrossLayoutPayload_IsPendingButNotResumable()
    {
        using var temp = new TempDirectory();
        Stage(temp.Path, payloadConfig: FrameworkDependentConfig);

        var pending = NewInstaller(temp.Path).GetPendingUpdateApply();

        Assert.NotNull(pending);
        Assert.False(pending.CanResume);
        Assert.False(string.IsNullOrWhiteSpace(pending.Problem));
    }

    [Fact]
    public void AnUnreadableRecord_IsPendingSoItCanBeDiscarded()
    {
        using var temp = new TempDirectory();
        WritePayload(temp.Path, SelfContainedConfig);
        File.WriteAllText(UpdaterScriptBuilder.PendingStatePathFor(temp.Path), "{ not json");

        var pending = NewInstaller(temp.Path).GetPendingUpdateApply();

        Assert.NotNull(pending);
        Assert.False(pending.CanResume);
        Assert.Equal(UpdaterScriptBuilder.StagingRootFor(temp.Path), pending.State.StagingRoot);
    }

    [Fact]
    public void Discard_RemovesEverythingStaged_ButKeepsTheTranscript()
    {
        using var temp = new TempDirectory();
        var staged = Stage(temp.Path);
        File.WriteAllText(UpdaterScriptBuilder.LogPathFor(temp.Path), "why it failed");
        var installer = NewInstaller(temp.Path);

        installer.DiscardPendingUpdateApply();

        Assert.False(Directory.Exists(staged.StagingRoot));
        Assert.False(File.Exists(staged.ZipPath));
        Assert.False(File.Exists(UpdaterScriptBuilder.LegacyScriptPathFor(temp.Path)));
        Assert.False(File.Exists(UpdaterScriptBuilder.PendingStatePathFor(temp.Path)));
        Assert.True(File.Exists(UpdaterScriptBuilder.LogPathFor(temp.Path)));
        Assert.Null(installer.GetPendingUpdateApply());
    }

    /// <summary>The service keeps <see cref="IAppUpdateService" /> and forwards the
    /// install verbs: it sees the installer's record and its discard is the installer's.</summary>
    [Fact]
    public void TheUpdateService_ForwardsTheInstallHalfToTheSameState()
    {
        using var temp = new TempDirectory();
        var staged = Stage(temp.Path);
        using var service = new GitHubUpdateService(new Version(0, 66, 0), installDir: temp.Path, downloadDir: temp.Path);

        Assert.Equal(UpdateApplyBlockReason.None, service.GetApplyBlockReason());
        Assert.Equal(staged.PayloadDir, service.GetPendingUpdateApply()?.State.PayloadDir);

        service.DiscardPendingUpdateApply();

        Assert.Null(NewInstaller(temp.Path).GetPendingUpdateApply());
    }
}
