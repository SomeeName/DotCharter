using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Infrastructure;

[Collection("CwdSerialized")] // shares process-wide state via OverrideRootsForTesting
public class WritablePathsTests : IDisposable
{
    private readonly string _config;
    private readonly string _logs;

    public WritablePathsTests()
    {
        _config = Path.Combine(Path.GetTempPath(), "dc-wp-cfg-" + Guid.NewGuid().ToString("N"));
        _logs = Path.Combine(Path.GetTempPath(), "dc-wp-log-" + Guid.NewGuid().ToString("N"));
        WritablePaths.OverrideRootsForTesting(_config, _logs);
    }

    public void Dispose()
    {
        WritablePaths.OverrideRootsForTesting(null, null);
        try { if (Directory.Exists(_config)) Directory.Delete(_config, recursive: true); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[test cleanup] {ex.Message}"); }
        try { if (Directory.Exists(_logs)) Directory.Delete(_logs, recursive: true); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[test cleanup] {ex.Message}"); }
    }

    [Fact]
    public void ConfigRoot_HonorsOverride_AndCreatesDirectory()
    {
        var root = WritablePaths.ConfigRoot;
        Assert.Equal(_config, root);
        Assert.True(Directory.Exists(root));
    }

    [Fact]
    public void LogRoot_HonorsOverride_AndCreatesDirectory()
    {
        var root = WritablePaths.LogRoot;
        Assert.Equal(_logs, root);
        Assert.True(Directory.Exists(root));
    }

    [Fact]
    public void ResolveConfigFile_CombinesWithConfigRoot()
    {
        var p = WritablePaths.ResolveConfigFile("appconfig.json");
        Assert.Equal(Path.Combine(_config, "appconfig.json"), p);
    }

    [Fact]
    public void ResolveConfigFile_EmptyName_Throws()
    {
        Assert.Throws<ArgumentException>(() => WritablePaths.ResolveConfigFile(""));
        Assert.Throws<ArgumentException>(() => WritablePaths.ResolveConfigFile("   "));
    }

    [Fact]
    public void ResolveLogFile_NullOrEmpty_UsesDefaultFileNameInLogRoot()
    {
        var p1 = WritablePaths.ResolveLogFile("");
        var p2 = WritablePaths.ResolveLogFile("   ");
        Assert.Equal(Path.Combine(_logs, "dotcharter.log"), p1);
        Assert.Equal(Path.Combine(_logs, "dotcharter.log"), p2);
    }

    [Fact]
    public void ResolveLogFile_RelativePath_KeepsOnlyFileNameUnderLogRoot()
    {
        var p = WritablePaths.ResolveLogFile("logs/custom.log");
        Assert.Equal(Path.Combine(_logs, "custom.log"), p);
    }

    /// <summary>
    ///     <c>TryClaimRootsForTesting</c> must DECLINE while another override is
    ///     in force, and change nothing when it does.
    ///     <para>
    ///         That is the whole reason it exists rather than the unconditional
    ///         setter: the headless session claims the roots lazily, from a
    ///         collection that runs in parallel with this one, so a claim that
    ///         won unconditionally would repoint the roots out from under a test
    ///         mid-body — and that test would then read and WRITE the
    ///         developer's real <c>%APPDATA%\DotCharter</c>.
    ///     </para>
    /// </summary>
    [Fact]
    public void TryClaimRoots_DeclinesWhileAnotherOverrideIsInForce()
    {
        // The ctor already installed _config/_logs, so this stands in for
        // "another test is mid-body".
        var intruder = Path.Combine(Path.GetTempPath(), "dc-wp-intruder-" + Guid.NewGuid().ToString("N"));

        Assert.False(WritablePaths.TryClaimRootsForTesting(intruder, intruder));
        Assert.Equal(_config, WritablePaths.ConfigRoot);
        Assert.Equal(_logs, WritablePaths.LogRoot);
    }

    /// <summary>…and it WINS when nothing holds the roots, which is the case in
    /// the headless suite's own process (nothing there touches the setter).</summary>
    [Fact]
    public void TryClaimRoots_TakesTheRootsWhenNobodyHoldsThem()
    {
        var claimed = Path.Combine(Path.GetTempPath(), "dc-wp-claim-" + Guid.NewGuid().ToString("N"));
        WritablePaths.OverrideRootsForTesting(null, null);   // the ctor's override, stood down
        try
        {
            Assert.True(WritablePaths.TryClaimRootsForTesting(claimed, claimed));
            Assert.Equal(claimed, WritablePaths.ConfigRoot);
            Assert.Equal(claimed, WritablePaths.LogRoot);
        }
        finally
        {
            // Dispose() restores the null override; put the ctor's roots back so
            // the other cases in this class are unaffected either way.
            WritablePaths.OverrideRootsForTesting(_config, _logs);
            try { Directory.Delete(claimed, recursive: true); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[test cleanup] {ex.Message}"); }
        }
    }

    [Fact]
    public void ResolveLogFile_WritableAbsolutePath_KeptAsIs()
    {
        var customDir = Path.Combine(Path.GetTempPath(), "dc-wp-abs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(customDir);
        try
        {
            var requested = Path.Combine(customDir, "absolute.log");
            var resolved = WritablePaths.ResolveLogFile(requested);
            Assert.Equal(requested, resolved);
        }
        finally
        {
            try { Directory.Delete(customDir, recursive: true); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[test cleanup] {ex.Message}"); }
        }
    }

    // ---- the log-root rule (pure) ----------------------------------------

    private static string Rule(bool portableConfig, Func<string, bool> exists, Func<string, bool> usable) =>
        WritablePaths.ResolveLogRootFor(portableConfig, @"C:\app", @"C:\user\DotCharter", @"C:\tmp\DotCharter\logs", exists, usable);

    [Fact]
    public void LogRoot_FollowsAPortableConfig_BesideTheExe()
    {
        // No logs folder exists yet anywhere — the fresh portable install.
        var created = new List<string>();
        var root = Rule(portableConfig: true, exists: _ => false, usable: d => { created.Add(d); return true; });
        Assert.Equal(@"C:\app\logs", root);
        Assert.Equal([@"C:\app\logs"], created);
    }

    [Fact]
    public void LogRoot_WithoutAPortableConfig_GoesToTheUserProfile()
    {
        var root = Rule(portableConfig: false, exists: _ => false, usable: _ => true);
        Assert.Equal(@"C:\user\DotCharter\logs", root);
    }

    [Fact]
    public void LogRoot_StillHonorsAPreExistingPortableLogsFolder()
    {
        var root = Rule(portableConfig: false, exists: d => d == @"C:\app\logs", usable: _ => true);
        Assert.Equal(@"C:\app\logs", root);
    }

    [Fact]
    public void LogRoot_FallsBackPastAnUnwritableAppDir()
    {
        var root = Rule(portableConfig: true, exists: _ => false, usable: d => !d.StartsWith(@"C:\app", StringComparison.Ordinal));
        Assert.Equal(@"C:\user\DotCharter\logs", root);
    }

    /// <summary>
    ///     The standalone rig renames the per-user folder in <c>Main</c>. Once a
    ///     root has been handed out the rename must REFUSE: a path resolved under
    ///     the old name would go on being used, and settings would split between
    ///     two folders.
    /// </summary>
    [Fact]
    public void UseAppFolderName_RefusesOnceARootWasResolved()
    {
        _ = WritablePaths.ConfigRoot; // resolve (and cache) under the current name
        var before = WritablePaths.ConfigRoot;

        Assert.Throws<InvalidOperationException>(() => WritablePaths.UseAppFolderName("DotCharter RigLab"));
        Assert.Equal(before, WritablePaths.ConfigRoot);
    }
}
