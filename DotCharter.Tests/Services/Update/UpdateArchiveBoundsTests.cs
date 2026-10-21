using System.IO.Compression;
using System.Text;
using DotCharter.Services.Infrastructure.Storage;
using DotCharter.Services.Update;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Update;

/// <summary>
///     An update archive is an UNTRUSTED archive on both apply paths — the LAN
///     peer's package obviously, and the GitHub one because the same
///     <c>ApplyDownloadedUpdate</c> expands whatever it is handed. The repo rule
///     is that such an archive goes through <c>BoundedZipExtractor</c>, never
///     <c>ZipFile.ExtractToDirectory</c>, and it was broken twice here:
///     <list type="number">
///         <item>
///             <c>AppPayload.ValidateArchive</c> enumerates
///             <c>archive.Entries</c>, materialising one <c>ZipArchiveEntry</c>
///             per member with NO <c>CheckDeclaredEntryCount</c> pre-check —
///             precisely the failure the extractor's own doc-comment describes.
///         </item>
///         <item>
///             <c>GitHubUpdateService.ApplyDownloadedUpdate</c> called
///             <c>ZipFile.ExtractToDirectory</c> with no member-count and no
///             decompressed-byte budget at all.
///         </item>
///     </list>
/// </summary>
public sealed class UpdateArchiveBoundsTests : IDisposable
{
    private const string SelfContainedConfig =
        """{"runtimeOptions":{"tfm":"net10.0","includedFrameworks":[{"name":"Microsoft.NETCore.App","version":"10.0.9"}]}}""";

    private readonly string _dir = Directory.CreateTempSubdirectory("dotcharter-update-bounds-").FullName;

    public void Dispose() => BestEffortDelete.Directory(_dir);

    private string Path_(string name) => Path.Combine(_dir, name);

    private static void WriteSelfContainedInstall(string dir)
    {
        File.WriteAllBytes(Path.Combine(dir, AppPayload.ExeName), [0x4D, 0x5A]);
        File.WriteAllText(Path.Combine(dir, AppPayload.RuntimeConfigName), SelfContainedConfig);
        File.WriteAllText(Path.Combine(dir, AppPayload.DepsName), "{}");
        File.WriteAllBytes(Path.Combine(dir, AppPayload.HostFxrName), [0x4D, 0x5A]);
        File.WriteAllBytes(Path.Combine(dir, AppPayload.CoreClrName), [0x4D, 0x5A]);
    }

    private static void WriteValidPayload(ZipArchive zip)
    {
        Add(zip, AppPayload.ExeName, "MZ");
        Add(zip, AppPayload.RuntimeConfigName, SelfContainedConfig);
        Add(zip, AppPayload.DepsName, "{}");
        Add(zip, AppPayload.HostFxrName, "MZ");
        Add(zip, AppPayload.CoreClrName, "MZ");
    }

    private static void Add(ZipArchive zip, string name, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open());
        writer.Write(content);
    }

    // ---- the declared-member-count pre-check --------------------------------

    /// <summary>
    ///     The cheap attack: a small archive of many EMPTY members. It costs the
    ///     sender almost nothing and costs the receiver one live object per
    ///     member the instant anything touches <c>Entries</c>. The count is
    ///     therefore read from the end-of-central-directory record, before the
    ///     archive is opened at all.
    /// </summary>
    [Fact]
    public void AnArchiveDeclaringMoreMembersThanAPayloadCanHaveIsRefusedBeforeItIsOpened()
    {
        var zipPath = Path_("many.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            WriteValidPayload(zip);
            for (var i = 0; i < AppPayload.MaxArchiveEntries + 8; i++) zip.CreateEntry($"pad/{i}.bin");
        }

        // The point of the fixture: it is tiny and declares tens of thousands of members.
        Assert.True(new FileInfo(zipPath).Length < 2 * 1024 * 1024);

        var problem = AppPayload.CheckArchiveBounds(zipPath);
        Assert.NotNull(problem);
        Assert.Contains("members", problem!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 })] // too short to hold an EOCD
    [InlineData(new byte[0])]
    public void SomethingThatIsNotAZipIsRefused(byte[] bytes)
    {
        var path = Path_("not-a-zip.zip");
        File.WriteAllBytes(path, bytes);

        Assert.NotNull(AppPayload.CheckArchiveBounds(path));
    }

    [Fact]
    public void AnEmptyArchiveIsRefused()
    {
        var path = Path_("empty.zip");
        using (var _ = ZipFile.Open(path, ZipArchiveMode.Create)) { }

        Assert.Equal("it contains no entries", AppPayload.CheckArchiveBounds(path));
    }

    [Fact]
    public void AnOrdinaryPayloadArchivePassesTheBounds()
    {
        // The guard must not refuse a real release: a self-contained publish is
        // under a thousand members, far inside the cap.
        var path = Path_("ok.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            WriteValidPayload(zip);
            for (var i = 0; i < 900; i++) Add(zip, $"runtimes/win-x64/native/lib{i}.dll", "MZ");
        }

        Assert.Null(AppPayload.CheckArchiveBounds(path));
    }

    // ---- the runtimeconfig read inside ValidateArchive ----------------------

    /// <summary>
    ///     <c>ValidateArchive</c> reads the payload's runtimeconfig to classify
    ///     it, and read it WHOLE with a bare <c>StreamReader.ReadToEnd</c> — so a
    ///     member under that name inflating to gigabytes was materialised as a
    ///     managed string just to answer "self-contained?". It now reads under a
    ///     ceiling; a truncated read simply fails to parse, which is the right
    ///     answer for a payload whose runtimeconfig is a bomb.
    /// </summary>
    [Fact]
    public void AnAbsurdRuntimeConfigIsNotReadWholeAndSoDoesNotClassify()
    {
        var path = Path_("bomb-config.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            Add(zip, AppPayload.ExeName, "MZ");
            Add(zip, AppPayload.DepsName, "{}");
            Add(zip, AppPayload.HostFxrName, "MZ");
            Add(zip, AppPayload.CoreClrName, "MZ");

            // 64 MiB of VALID, self-contained-looking runtimeconfig JSON, padded
            // in the middle so the whole document parses but no prefix of it
            // does. That is what makes this test non-vacuous: reading it WHOLE
            // (the old behaviour) classifies it happily as self-contained and
            // returns "no problem"; reading it under a ceiling truncates it, the
            // JSON no longer parses, and the payload is refused.
            using var entry = zip.CreateEntry(AppPayload.RuntimeConfigName).Open();
            entry.Write(Encoding.ASCII.GetBytes(
                "{\"runtimeOptions\":{\"includedFrameworks\":[" +
                "{\"name\":\"Microsoft.NETCore.App\",\"version\":\"10.0.9\"}],\"pad\":\""));
            var chunk = Encoding.ASCII.GetBytes(new string('A', 64 * 1024));
            for (var i = 0; i < 1024; i++) entry.Write(chunk);
            var tail = Encoding.ASCII.GetBytes("\"}}");
            entry.Write(tail);
        }

        // The point of the fixture: 64 MiB of managed string from a small file.
        Assert.True(new FileInfo(path).Length < 1024 * 1024);

        using var archive = ZipFile.OpenRead(path);
        var problem = AppPayload.ValidateArchive(archive, InstallLayout.SelfContained);

        Assert.NotNull(problem);
        Assert.Contains("does not describe a known runtime layout", problem!, StringComparison.Ordinal);
    }

    /// <summary>An ordinary runtimeconfig is still read and still classifies —
    /// the ceiling must not cost a real payload its layout.</summary>
    [Fact]
    public void AnOrdinaryRuntimeConfigStillClassifies()
    {
        var path = Path_("ordinary-config.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create)) WriteValidPayload(zip);

        using var archive = ZipFile.OpenRead(path);
        Assert.Null(AppPayload.ValidateArchive(archive, InstallLayout.SelfContained));
    }

    // ---- the extraction budget on the apply path ----------------------------

    /// <summary>
    ///     Both apply paths land in <c>ApplyDownloadedUpdate</c>, which expanded
    ///     the archive with a bare <c>ZipFile.ExtractToDirectory</c> — no member
    ///     count, no byte budget. A LAN peer is unauthenticated, so the archive
    ///     is fully attacker-authored. Nothing may be staged or launched.
    ///     <para>
    ///         The budget is overridden down for the test: the shipped ceiling
    ///         floors at 512 MiB and proving it bites through would cost the gate
    ///         half a gigabyte of writes. The FORMULA is pinned purely below.
    ///     </para>
    /// </summary>
    [Fact]
    public void AnArchiveThatInflatesPastTheBudgetIsRefusedAndLeavesNoStaging()
    {
        var install = Path_("install");
        Directory.CreateDirectory(install);
        WriteSelfContainedInstall(install);

        // 8 MiB of zeros in a ~8 KB archive — the ordinary zip bomb shape.
        var zipPath = Path_("bomb.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            WriteValidPayload(zip);
            using var entry = zip.CreateEntry("payload/blob.bin").Open();
            var chunk = new byte[1024 * 1024];
            for (var i = 0; i < 8; i++) entry.Write(chunk);
        }

        Assert.True(new FileInfo(zipPath).Length < 128 * 1024);

        using var service = new GitHubUpdateService(
            new Version(0, 1, 0), installDir: install, downloadDir: _dir)
        {
            ExtractionBudgetOverrideForTests = 1024 * 1024
        };

        service.TrustDownloadForTests(zipPath);
        var error = Assert.Throws<InvalidDataException>(() => service.ApplyDownloadedUpdate(zipPath));

        Assert.Contains("BudgetExceeded", error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(install, UpdaterScriptBuilder.StagingDirName)));
        Assert.False(File.Exists(Path.Combine(install, UpdaterScriptBuilder.ScriptFileName)));
    }

    [Fact]
    public void AnArchiveWithTooManyMembersIsRefusedOnTheApplyPathToo()
    {
        var install = Path_("install2");
        Directory.CreateDirectory(install);
        WriteSelfContainedInstall(install);

        var zipPath = Path_("many-apply.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            WriteValidPayload(zip);
            for (var i = 0; i < AppPayload.MaxArchiveEntries + 8; i++) zip.CreateEntry($"pad/{i}.bin");
        }

        using var service = new GitHubUpdateService(
            new Version(0, 1, 0), installDir: install, downloadDir: _dir);

        service.TrustDownloadForTests(zipPath);
        Assert.Throws<InvalidDataException>(() => service.ApplyDownloadedUpdate(zipPath));
        Assert.False(Directory.Exists(Path.Combine(install, UpdaterScriptBuilder.StagingDirName)));
    }

    /// <summary>The shipped ceiling: floors at 512 MiB so a bomb stops there,
    /// and scales at 8× so a ~414 MB release package — which expands to well
    /// under that — is never refused.</summary>
    [Fact]
    public void TheInflationCeilingFloorsAndScales()
    {
        Assert.Equal(512L * 1024 * 1024, AppPayload.InflationCeiling(0));
        Assert.Equal(512L * 1024 * 1024, AppPayload.InflationCeiling(1024));
        Assert.Equal(512L * 1024 * 1024, AppPayload.InflationCeiling(-1)); // no negative ceiling
        Assert.Equal(8L * 1024 * 1024 * 1024, AppPayload.InflationCeiling(1024L * 1024 * 1024));
        Assert.True(AppPayload.InflationCeiling(414L * 1024 * 1024) > 3L * 1024 * 1024 * 1024);
    }

    /// <summary>
    ///     The bounds must not refuse a real payload. This runs the EXACT
    ///     extraction the apply path runs — same extractor, same shipped
    ///     constants — rather than calling <c>ApplyDownloadedUpdate</c>, whose
    ///     success path detaches a cmd.exe updater that waits for this process to
    ///     exit and then robocopies over the install directory.
    /// </summary>
    [Fact]
    public void AnOrdinaryPayloadIsExtractedWholeUnderTheShippedBounds()
    {
        var zipPath = Path_("good.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            WriteValidPayload(zip);
            for (var i = 0; i < 900; i++) Add(zip, $"runtimes/win-x64/native/lib{i}.dll", "MZ");
        }

        Assert.Null(AppPayload.CheckArchiveBounds(zipPath));

        var staging = Path_("staging");
        var result = BoundedZipExtractor.ExtractBounded(
            zipPath, staging,
            AppPayload.InflationCeiling(new FileInfo(zipPath).Length),
            AppPayload.MaxArchiveEntries);

        Assert.True(result.Succeeded, $"refused with {result.Refusal}");
        Assert.Equal(905, result.FilesWritten);
        Assert.True(File.Exists(Path.Combine(staging, AppPayload.ExeName)));
        Assert.True(File.Exists(Path.Combine(staging, "runtimes", "win-x64", "native", "lib0.dll")));
        Assert.Null(AppPayload.ValidateDirectory(staging, InstallLayout.SelfContained));
    }
}
