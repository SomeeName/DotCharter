using System.IO.Compression;
using DotCharter.Services.Infrastructure.Backup;
using DotCharter.Services.Infrastructure.Storage;
using DotCharter.Services.Update;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Update;

/// <summary>
///     An update package was filtered by whoever BUILT it and by nobody else.
///     The receiving side ran <c>AppPayload.Validate</c>, which asks only whether
///     the exe, the runtimeconfig and the host pair are present, and then
///     robocopied every archive entry over the live install.
///     <para>
///         A zip that did not come out of our release pipeline could therefore
///         ship the victim's own binaries plus one <c>appconfig.json</c>. On a
///         portable install that file is not merely settings: <c>WritablePaths</c>
///         treats its presence next to the exe as the definition of a portable
///         install, so planting it makes the attacker's config authoritative, down
///         to the update channel and every path the app reads. No prompt, no
///         trace.
///     </para>
///     <para>
///         The rule is the general one: <b>a package is filtered on RECEIPT,
///         never trusted because whoever built it filtered.</b>
///     </para>
///     <para>
///         <see cref="PayloadReceiptFilter.IsLocalState" /> is the predicate, and
///         it DERIVES from <c>ConfigBackupRules</c> rather than restating it — the
///         coupling lints at the bottom of this file are what keep the two lists
///         from drifting the way they once did over <c>themes</c>.
///     </para>
/// </summary>
public sealed class PayloadReceiptFilterTests : IDisposable
{
    private const string SelfContainedConfig =
        """{"runtimeOptions":{"tfm":"net10.0","includedFrameworks":[{"name":"Microsoft.NETCore.App","version":"10.0.9"}]}}""";

    private readonly string _dir = Directory.CreateTempSubdirectory("dotcharter-receipt-filter-").FullName;

    public void Dispose() => BestEffortDelete.Directory(_dir);

    // ---- the pure rule -------------------------------------------------------

    /// <summary>
    ///     One case per class of local state. Each is a file an update payload
    ///     never carries and the receiver already owns a copy of, so applying one
    ///     is at best data loss and at worst — the first row — a configuration
    ///     takeover.
    /// </summary>
    [Theory]
    [InlineData("appconfig.json")]                  // settings AND the portable-install marker
    [InlineData("keybinds.json")]
    [InlineData("player-profiles.json")]
    [InlineData("profiles/9f2c/tone-rig.json")]     // per-profile user data
    [InlineData("github-token.dat")]                // DPAPI credential stores
    [InlineData("tone3000-token.dat")]
    [InlineData("recovery/snapshot-1.rscp")]        // unsaved work
    [InlineData("themes/neon/theme.json")]
    [InlineData("logs/dotcharter.log")]
    [InlineData("dotcharter-update.log")]
    [InlineData("update-staging/DotCharter.exe")]
    [InlineData("lan-scoreboard.json")]
    public void EveryFileThatIsLocalStateIsRefusedOnReceipt(string relativePath)
    {
        // The premise: each of these really is classified as local state.
        Assert.True(PayloadReceiptFilter.IsLocalState(relativePath),
            $"{relativePath} is not classified as local state — this test is asserting the wrong thing");

        var refusal = PayloadReceiptFilter.Classify(relativePath);

        Assert.NotNull(refusal);
        Assert.Contains(relativePath.Replace('\\', '/'), refusal!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("DotCharter.exe")]
    [InlineData("DotCharter.runtimeconfig.json")]
    [InlineData("DotCharter.deps.json")]
    [InlineData("hostfxr.dll")]
    [InlineData("runtimes/win-x64/native/juce_sharp.dll")]
    [InlineData("de-DE/DotCharter.Localization.resources.dll")]
    [InlineData("tools/ffmpeg.exe")]
    [InlineData("payload-manifest.json")]
    [InlineData("payload-deletions.txt")]
    public void ARealPayloadFileIsNotRefused(string relativePath)
        => Assert.Null(PayloadReceiptFilter.Classify(relativePath));

    /// <summary>
    ///     A split payload (#413) keeps the app in <c>app/</c>. The rule must not be
    ///     one folder away from bypassed: a user-state name in there is contraband
    ///     too — and the app's own files in there are not.
    /// </summary>
    [Theory]
    [InlineData("app/appconfig.json")]
    [InlineData("app/keybinds.json")]
    [InlineData("app/github-token.dat")]
    [InlineData("app/profiles/9f2c/tone-rig.json")]
    [InlineData("app/logs/dotcharter.log")]
    [InlineData(@"APP\player-profiles.json")]
    public void UserStateUnderTheAppFolderIsRefusedToo(string relativePath)
    {
        Assert.True(PayloadReceiptFilter.IsLocalState(relativePath), relativePath);
        Assert.NotNull(PayloadReceiptFilter.Classify(relativePath));
    }

    [Theory]
    [InlineData("app/DotCharter.exe")]
    [InlineData("app/DotCharter.dll")]
    [InlineData("app/hostfxr.dll")]
    [InlineData("app/runtimes/win-x64/native/juce_sharp.dll")]
    [InlineData("app/NamCaptures/clean.nam")]
    [InlineData("app/payload-manifest.json")]
    [InlineData("voices/guitar.dcvp")]
    [InlineData("README.txt")]
    public void TheSplitPayloadsOwnFilesAreNotRefused(string relativePath)
        => Assert.Null(PayloadReceiptFilter.Classify(relativePath));

    [Theory]
    [InlineData("../outside.dll")]
    [InlineData("a/../../outside.dll")]
    [InlineData("C:/Windows/System32/evil.dll")]
    [InlineData("/etc/passwd")]
    [InlineData("")]
    public void APathThatIsNotPayloadRelativeIsRefused(string relativePath)
        => Assert.NotNull(PayloadReceiptFilter.Classify(relativePath));

    // ---- the apply path ------------------------------------------------------

    /// <summary>
    ///     The attack, end to end: a structurally perfect payload — right exe,
    ///     right runtimeconfig, right host pair, so <c>AppPayload.ValidateDirectory</c>
    ///     is happy — carrying one extra file. Nothing may be staged or launched,
    ///     and the message has to NAME the entry: a refusal nobody can act on is a
    ///     mystery failed update.
    /// </summary>
    [Fact]
    public void APackageCarryingAnAppConfigIsRefusedAndTheEntryIsNamed()
    {
        var install = NewInstall("install-config");
        var zipPath = Path.Combine(_dir, "hostile-config.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            // Entries at the archive root — the shape a release payload has, and
            // therefore the shape a hostile one imitates.
            WriteFlatPayload(zip);
            Add(zip, "appconfig.json", """{"StartInPlayMode":true}""");
        }

        using var service = new GitHubUpdateService(
            new Version(0, 48, 0), installDir: install, downloadDir: _dir);

        var error = Assert.Throws<InvalidDataException>(() => service.ApplyDownloadedUpdate(zipPath));

        Assert.Contains("appconfig.json", error.Message, StringComparison.OrdinalIgnoreCase);
        AssertNothingWasStaged(install);
    }

    [Fact]
    public void APackageCarryingSomebodyElsesProfileIsRefusedAndTheEntryIsNamed()
    {
        var install = NewInstall("install-profiles");
        var zipPath = Path.Combine(_dir, "hostile-profiles.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            WriteFlatPayload(zip);
            Add(zip, "profiles/x.json", "{}");
        }

        using var service = new GitHubUpdateService(
            new Version(0, 48, 0), installDir: install, downloadDir: _dir);

        var error = Assert.Throws<InvalidDataException>(() => service.ApplyDownloadedUpdate(zipPath));

        Assert.Contains("profiles/x.json", error.Message, StringComparison.OrdinalIgnoreCase);
        AssertNothingWasStaged(install);
    }

    /// <summary>
    ///     The other half, and the one that matters if this filter is ever
    ///     tightened: an ORDINARY release payload must sail through every step
    ///     <c>ApplyDownloadedUpdate</c> takes before it launches the updater.
    ///     <para>
    ///         Those steps are run here rather than the method itself, for the
    ///         reason <c>UpdateArchiveBoundsTests</c> gives: its success path
    ///         detaches a cmd.exe that waits for this process to exit and then
    ///         robocopies over the install directory.
    ///     </para>
    /// </summary>
    [Fact]
    public void AnHonestReleasePayloadPassesEveryCheckTheApplyPathMakes()
    {
        var zipPath = Path.Combine(_dir, "honest.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            WriteValidPayload(zip);
            Add(zip, "payload/payload-manifest.json", """{"files":[]}""");
            Add(zip, "payload/payload-deletions.txt", "# nothing\n");
            Add(zip, "payload/de-DE/DotCharter.Localization.resources.dll", "MZ");
            Add(zip, "payload/tools/ffmpeg.exe", "MZ");
            Add(zip, "payload/NamCaptures/readme.txt", "captures land here");
            Add(zip, "payload/PythonScripts/demucs.py", "# script");
            for (var i = 0; i < 40; i++) Add(zip, $"payload/runtimes/win-x64/native/lib{i}.dll", "MZ");
        }

        Assert.Null(AppPayload.CheckArchiveBounds(zipPath));

        var staging = Path.Combine(_dir, "honest-staging");
        var extraction = BoundedZipExtractor.ExtractBounded(
            zipPath, staging,
            AppPayload.InflationCeiling(new FileInfo(zipPath).Length),
            AppPayload.MaxArchiveEntries);
        Assert.True(extraction.Succeeded, $"refused with {extraction.Refusal}");

        var payloadDir = Path.Combine(staging, "payload");
        Assert.Null(AppPayload.ValidateDirectory(payloadDir, InstallLayout.SelfContained));
        Assert.Null(PayloadReceiptFilter.FindRefusal(payloadDir));
    }

    /// <summary>The walk names the first offender wherever it is nested, not just
    /// at the payload root.</summary>
    [Fact]
    public void TheWalkFindsAnOffenderNestedInsideTheTree()
    {
        var payload = Path.Combine(_dir, "nested-payload");
        Directory.CreateDirectory(Path.Combine(payload, "recovery"));
        File.WriteAllText(Path.Combine(payload, AppPayload.ExeName), "MZ");
        File.WriteAllText(Path.Combine(payload, "recovery", "snapshot.rscp"), "<project/>");

        var refusal = PayloadReceiptFilter.FindRefusal(payload);

        Assert.NotNull(refusal);
        Assert.Contains("recovery/snapshot.rscp", refusal!, StringComparison.OrdinalIgnoreCase);
    }

    // ---- helpers -------------------------------------------------------------

    // ---- the ConfigBackupRules coupling --------------------------------------

    /// <summary>
    ///     The structural guard. Two hand-maintained lists WILL drift — `themes`
    ///     was on <c>ConfigBackupRules</c>' user-data list and absent from the
    ///     payload filter's exclusions, which is exactly how a user's theme packs
    ///     became overwritable by an update. A tree the backup system treats as
    ///     the user's data is by definition not something a payload may carry.
    /// </summary>
    [Fact]
    public void Filter_RefusesEveryDirectoryTheBackupRulesCallUserData()
    {
        foreach (var dir in ConfigBackupRules.UserDataTopDirs)
        {
            Assert.True(PayloadReceiptFilter.IsLocalState($"{dir}/probe.bin"),
                $"ConfigBackupRules calls '{dir}' user data, but PayloadReceiptFilter would let an " +
                "update payload overwrite it.");
        }
    }

    /// <summary>
    ///     The FILE half of the same guard: a new root-level user-data file added
    ///     to <c>ConfigBackupRules.IncludedRootFiles</c> and forgotten here would
    ///     be robocopied over this machine's copy on the next update — silent data
    ///     loss, not merely a stale file.
    /// </summary>
    [Fact]
    public void Filter_RefusesEveryRootFileTheBackupRulesCallUserData()
    {
        foreach (var file in ConfigBackupRules.UserDataRootFiles)
        {
            Assert.True(PayloadReceiptFilter.IsLocalState(file),
                $"ConfigBackupRules calls '{file}' user data, but PayloadReceiptFilter would let an " +
                "update payload overwrite it.");
        }
    }

    /// <summary>A credential store is DPAPI-sealed to this machine, so one
    /// arriving in a payload carries nothing usable — but it still REPLACES this
    /// machine's, signing the user out of GitHub, the online server and
    /// TONE3000.</summary>
    [Fact]
    public void Filter_RefusesEveryCredentialStoreTheBackupRulesKnow()
    {
        foreach (var file in ConfigBackupRules.CredentialFileNames)
        {
            Assert.True(PayloadReceiptFilter.IsLocalState(file),
                $"ConfigBackupRules calls '{file}' a credential store, but PayloadReceiptFilter would " +
                "let an update payload replace it.");
        }
    }

    /// <summary>
    ///     Proves the DERIVATION is really wired, not just that both lists happen
    ///     to agree: these paths are named nowhere in
    ///     <c>PayloadReceiptFilter</c>'s own literals, so the only way they can be
    ///     refused is through <c>ConfigBackupRules</c>. This is the case that
    ///     fails if someone "simplifies" <c>IsLocalState</c> back to its literals.
    /// </summary>
    [Theory]
    [InlineData("practice-profiles.json")]
    [InlineData("recovery/deadbeef.rscp")]
    public void Filter_ReachesTheBackupRulesRatherThanRestatingThem(string relative)
    {
        Assert.True(ConfigBackupRules.ShouldInclude(relative),
            "fixture drift: this path must be one ConfigBackupRules classifies as user data");
        Assert.True(PayloadReceiptFilter.IsLocalState(relative));
    }

    /// <summary>Volatile machine state that is NOT on the backup allowlist —
    /// this machine's in-flight update record names paths on THIS disk, so a
    /// payload carrying one would offer a "finish the update" pointing at a
    /// staging tree that does not exist here.</summary>
    [Theory]
    [InlineData("dotcharter-update.cmd")]
    [InlineData("dotcharter-update-pending.json")]
    [InlineData("dotcharter-update-wait.tmp")]
    [InlineData("updates/DotCharter-v0.12.8-win-x64.zip")]
    [InlineData("library-index.db-wal")]
    [InlineData("multiplayer-received/song.psarc")]
    [InlineData("lan-avatars/ab12cd34.png")]
    [InlineData("GitHub-Token.DAT")]
    [InlineData("something.tmp")]
    public void Filter_RefusesVolatileMachineState(string relative)
        => Assert.True(PayloadReceiptFilter.IsLocalState(relative));

    /// <summary>…and the negative half, so the predicate cannot go absolute: a
    /// real payload file is never local state.</summary>
    [Theory]
    [InlineData("DotCharter.exe")]
    [InlineData("DotCharter.Services.dll")]
    [InlineData("runtimes/win-x64/native/juce_sharp.dll")]
    [InlineData("Assets/app-icon.png")]
    public void Filter_LeavesRealPayloadFilesAlone(string relative)
        => Assert.False(PayloadReceiptFilter.IsLocalState(relative));

    private static void AssertNothingWasStaged(string install)
    {
        Assert.False(Directory.Exists(Path.Combine(install, UpdaterScriptBuilder.StagingDirName)),
            "the staging tree survived a refusal");
        Assert.False(File.Exists(Path.Combine(install, UpdaterScriptBuilder.ScriptFileName)),
            "the updater script was written for a refused payload");
        Assert.False(File.Exists(UpdaterScriptBuilder.PendingStatePathFor(install)),
            "a refused payload left a resumable pending-apply record");
    }

    private string NewInstall(string name)
    {
        var dir = Path.Combine(_dir, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, AppPayload.ExeName), "MZ");
        File.WriteAllText(Path.Combine(dir, AppPayload.RuntimeConfigName), SelfContainedConfig);
        File.WriteAllText(Path.Combine(dir, AppPayload.DepsName), "{}");
        File.WriteAllText(Path.Combine(dir, AppPayload.HostFxrName), "MZ");
        File.WriteAllText(Path.Combine(dir, AppPayload.CoreClrName), "MZ");
        return dir;
    }

    /// <summary>A payload wrapped in one top-level folder, the shape a release
    /// zip has.</summary>
    private static void WriteValidPayload(ZipArchive zip)
    {
        Add(zip, "payload/" + AppPayload.ExeName, "MZ");
        Add(zip, "payload/" + AppPayload.RuntimeConfigName, SelfContainedConfig);
        Add(zip, "payload/" + AppPayload.DepsName, "{}");
        Add(zip, "payload/" + AppPayload.HostFxrName, "MZ");
        Add(zip, "payload/" + AppPayload.CoreClrName, "MZ");
    }

    /// <summary>A payload at the archive root, rather than under a wrapper
    /// folder — the other layout an incoming archive can carry.</summary>
    private static void WriteFlatPayload(ZipArchive zip)
    {
        Add(zip, AppPayload.ExeName, "MZ");
        Add(zip, AppPayload.RuntimeConfigName, SelfContainedConfig);
        Add(zip, AppPayload.DepsName, "{}");
        Add(zip, AppPayload.HostFxrName, "MZ");
        Add(zip, AppPayload.CoreClrName, "MZ");
    }

    private static void Add(ZipArchive zip, string name, string content)
    {
        using var stream = zip.CreateEntry(name).Open();
        stream.Write(System.Text.Encoding.UTF8.GetBytes(content));
    }
}
