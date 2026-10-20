using System.IO.Compression;
using System.Text;
using DotCharter.Services.Infrastructure.Backup;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Infrastructure;

/// <summary>
///     Coverage for the whole-install backup &amp; migrate archive: the pure
///     include/credential/sanitization rules, a full export → import round-trip
///     against temp config roots, the schema-version gate, and the hardening
///     against crafted archives (traversal entries, planted credential stores).
/// </summary>
public sealed class ConfigBackupServiceTests : IDisposable
{
    private readonly string _workDir = Path.Combine(
        Path.GetTempPath(), "dc-backup-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => BestEffortDelete.Directory(_workDir);

    // ---- pure rules --------------------------------------------------------------

    [Theory]
    [InlineData("appconfig.json")]
    [InlineData("keybinds.json")]
    [InlineData("player-profiles.json")]
    [InlineData("practice-profiles.json")]
    [InlineData("profiles/default/stats.json")]
    [InlineData("languages/fr-FR.json")]
    [InlineData("recovery/abc123.rscp")]
    [InlineData("themes/midnight.json")]
    [InlineData("phrases/main-riff.rscp")]        // named phrase catalog
    [InlineData("profiles\\default\\stats.json")] // separator-agnostic
    [InlineData("APPCONFIG.JSON")]                // case-insensitive
    public void Rules_IncludeUserData(string path) => Assert.True(ConfigBackupRules.ShouldInclude(path));

    [Theory]
    [InlineData("github-token.dat")]            // DPAPI GitHub PAT
    [InlineData("online-session.bin")]          // DPAPI online session
    [InlineData("online-server-session.bin")]   // DPAPI server session
    [InlineData("tone3000-token.dat")]          // DPAPI TONE3000 session
    [InlineData("library-index.db")]            // psarc index cache
    [InlineData("library-index.db-wal")]
    [InlineData("logs/dotcharter.log")]
    [InlineData("updates/DotCharter-v9-win-x64.zip")]
    [InlineData("update-staging/DotCharter.exe")]
    [InlineData("multiplayer-received/song.psarc")]
    [InlineData("DotCharter.exe")]              // portable install: app payload
    [InlineData("hostfxr.dll")]
    [InlineData("juce_sharp.dll")]
    public void Rules_ExcludeSecretsCachesAndBinaries(string path) => Assert.False(ConfigBackupRules.ShouldInclude(path));

    [Theory]
    [InlineData("GITHUB-TOKEN.DAT")]
    [InlineData("github-token.dat")]
    [InlineData("\\github-token.dat")]
    public void Rules_CredentialsAreFlaggedCaseInsensitively(string path)
    {
        Assert.True(ConfigBackupRules.IsCredential(path));
        Assert.False(ConfigBackupRules.ShouldInclude(path));
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("profiles/../../evil.txt")]
    [InlineData("profiles/./x.json")]
    [InlineData("C:/Windows/evil.txt")]
    [InlineData("C:\\Windows\\evil.txt")]
    [InlineData("")]
    [InlineData("   ")]
    public void Rules_SanitizeRejectsTraversalAndRootedPaths(string entryName) => Assert.Null(ConfigBackupRules.SanitizeEntryPath(entryName));

    [Fact]
    public void Rules_SanitizeNormalizesSeparators()
    {
        Assert.Equal("profiles/default/stats.json",
            ConfigBackupRules.SanitizeEntryPath("profiles\\default\\stats.json"));
    }

    // ---- export ------------------------------------------------------------------

    [Fact]
    public void Export_PicksUpUserDataAndLeavesSecretsCachesBehind()
    {
        var root = CreatePopulatedConfigRoot();
        var zipPath = Path.Combine(_workDir, "backup.zip");

        var result = new ConfigBackupService(root).Export(zipPath);

        using var zip = ZipFile.OpenRead(zipPath);
        var names = zip.Entries.Select(e => e.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains(ConfigBackupRules.ManifestEntryName, names);
        Assert.Contains("appconfig.json", names);
        Assert.Contains("keybinds.json", names);
        Assert.Contains("player-profiles.json", names);
        Assert.Contains("practice-profiles.json", names);
        Assert.Contains("profiles/default/stats.json", names);
        Assert.Contains("languages/fr-FR.json", names);
        Assert.Contains("recovery/snapshot.rscp", names);

        Assert.DoesNotContain("github-token.dat", names);
        Assert.DoesNotContain("online-session.bin", names);
        Assert.DoesNotContain("online-server-session.bin", names);
        Assert.DoesNotContain("library-index.db", names);
        Assert.DoesNotContain("logs/dotcharter.log", names);
        Assert.DoesNotContain("DotCharter.exe", names);

        Assert.Equal(names.Count - 1, result.FileCount); // minus the manifest
        Assert.Empty(result.SkippedEntries);
    }

    [Fact]
    public void Export_WritesAParseableManifestStampedWithTheCurrentSchema()
    {
        var root = CreatePopulatedConfigRoot();
        var zipPath = Path.Combine(_workDir, "backup.zip");

        new ConfigBackupService(root).Export(zipPath);

        using var zip = ZipFile.OpenRead(zipPath);
        using var reader = new StreamReader(zip.GetEntry(ConfigBackupRules.ManifestEntryName)!.Open());
        var manifest = ConfigBackupRules.TryParseManifest(reader.ReadToEnd());

        Assert.NotNull(manifest);
        Assert.Equal(ConfigBackupRules.SchemaMajor, manifest!.SchemaMajor);
        Assert.Equal(ConfigBackupRules.SchemaMinor, manifest.SchemaMinor);
        Assert.True(manifest.EntryCount > 0);
    }

    [Fact]
    public void Export_DoesNotPackItselfWhenSavedInsideTheConfigRoot()
    {
        var root = CreatePopulatedConfigRoot();
        // "recovery/" is an included tree, so an unguarded export would try to
        // zip the half-written archive into itself.
        var zipPath = Path.Combine(root, "recovery", "backup.zip");

        var result = new ConfigBackupService(root).Export(zipPath);

        using var zip = ZipFile.OpenRead(zipPath);
        Assert.DoesNotContain(zip.Entries, e => e.FullName.EndsWith("backup.zip", StringComparison.OrdinalIgnoreCase));
        Assert.True(result.FileCount > 0);
    }

    // ---- round-trip --------------------------------------------------------------

    [Fact]
    public void ExportImport_RoundTripsAllUserDataByteForByte()
    {
        var source = CreatePopulatedConfigRoot();
        var zipPath = Path.Combine(_workDir, "backup.zip");
        new ConfigBackupService(source).Export(zipPath);

        var target = Path.Combine(_workDir, "target-root");
        Directory.CreateDirectory(target);
        var result = new ConfigBackupService(target).Import(zipPath);

        foreach (var relative in new[]
                 {
                     "appconfig.json", "keybinds.json", "player-profiles.json",
                     "practice-profiles.json",
                     Path.Combine("profiles", "default", "stats.json"),
                     Path.Combine("languages", "fr-FR.json"),
                     Path.Combine("recovery", "snapshot.rscp")
                 })
        {
            var expected = File.ReadAllBytes(Path.Combine(source, relative));
            var actual = File.ReadAllBytes(Path.Combine(target, relative));
            Assert.Equal(expected, actual);
        }

        // Nothing that must stay behind materialized on the target machine.
        Assert.False(File.Exists(Path.Combine(target, "github-token.dat")));
        Assert.False(File.Exists(Path.Combine(target, "online-session.bin")));
        Assert.False(File.Exists(Path.Combine(target, "library-index.db")));
        Assert.False(File.Exists(Path.Combine(target, "DotCharter.exe")));

        Assert.Equal(7, result.FileCount);
        Assert.Empty(result.SkippedEntries);
    }

    [Fact]
    public void Import_MergeOverwritesExistingFilesAndKeepsUnrelatedOnes()
    {
        var source = CreatePopulatedConfigRoot();
        var zipPath = Path.Combine(_workDir, "backup.zip");
        new ConfigBackupService(source).Export(zipPath);

        var target = Path.Combine(_workDir, "target-root");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "appconfig.json"), "{ \"old\": true }");
        File.WriteAllText(Path.Combine(target, "unrelated.txt"), "kept");

        new ConfigBackupService(target).Import(zipPath);

        Assert.Equal(File.ReadAllText(Path.Combine(source, "appconfig.json")),
            File.ReadAllText(Path.Combine(target, "appconfig.json")));
        Assert.Equal("kept", File.ReadAllText(Path.Combine(target, "unrelated.txt")));
    }

    /// <summary>
    ///     Every file lands atomically, but until 2026-08-29 the SET did not: a
    ///     failure part-way through the promote left some files imported and the
    ///     rest the user's own — a config root that never existed on either side of
    ///     the restore — and the `finally` then deleted the staging tree, so the
    ///     rest of the archive was gone with it.
    /// </summary>
    [Fact]
    public void Import_ThatFailsPartWayThroughThePromote_LeavesTheConfigRootExactlyAsItWas()
    {
        string[] names =
        [
            "appconfig.json", "keybinds.json", "player-profiles.json",
            "practice-profiles.json"
        ];

        var source = Path.Combine(_workDir, "src-root");
        var target = Path.Combine(_workDir, "dst-root");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        foreach (var name in names)
        {
            File.WriteAllText(Path.Combine(source, name), $"{{ \"imported\": \"{name}\" }}", Encoding.UTF8);
            File.WriteAllText(Path.Combine(target, name), $"{{ \"original\": \"{name}\" }}", Encoding.UTF8);
        }

        var zipPath = Path.Combine(_workDir, "backup.zip");
        new ConfigBackupService(source).Export(zipPath);

        // FileShare.Read shares neither write nor delete, and a rename OVER a
        // destination needs delete on it — so the promote of this one file is
        // denied while the two that sort before it have already gone through.
        var blocked = Path.Combine(target, "player-profiles.json");
        using (new FileStream(blocked, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.ThrowsAny<Exception>(() => new ConfigBackupService(target).Import(zipPath));
        }

        foreach (var name in names)
            Assert.Equal($"{{ \"original\": \"{name}\" }}", File.ReadAllText(Path.Combine(target, name)));

        // No scratch left behind either — the rollback copies are not a new
        // directory the user has to clean up.
        Assert.Empty(Directory.EnumerateDirectories(target));
    }

    // ---- import hardening --------------------------------------------------------

    [Fact]
    public void Import_RefusesAnArchiveWithoutAManifest()
    {
        var zipPath = Path.Combine(_workDir, "not-a-backup.zip");
        Directory.CreateDirectory(_workDir);
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            WriteEntry(zip, "appconfig.json", "{}");
        }

        var target = Path.Combine(_workDir, "target-root");
        Assert.Throws<InvalidDataException>(() => new ConfigBackupService(target).Import(zipPath));
        Assert.False(File.Exists(Path.Combine(target, "appconfig.json")));
    }

    [Fact]
    public void Import_RefusesAnArchiveFromANewerSchemaMajor()
    {
        var zipPath = Path.Combine(_workDir, "future.zip");
        Directory.CreateDirectory(_workDir);
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var manifest = new ConfigBackupManifest(
                ConfigBackupRules.SchemaMajor + 1, 0, "99.0.0", DateTimeOffset.UtcNow, 1);
            WriteEntry(zip, ConfigBackupRules.ManifestEntryName, ConfigBackupRules.SerializeManifest(manifest));
            WriteEntry(zip, "appconfig.json", "{ \"fromTheFuture\": true }");
        }

        var target = Path.Combine(_workDir, "target-root");
        var ex = Assert.Throws<InvalidDataException>(() => new ConfigBackupService(target).Import(zipPath));
        Assert.Contains("newer", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(target, "appconfig.json")));
    }

    [Fact]
    public void Import_AcceptsAnOlderMinorOfTheSameMajor()
    {
        var zipPath = Path.Combine(_workDir, "older-minor.zip");
        Directory.CreateDirectory(_workDir);
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var manifest = new ConfigBackupManifest(
                ConfigBackupRules.SchemaMajor, ConfigBackupRules.SchemaMinor, null, DateTimeOffset.UtcNow, 1);
            WriteEntry(zip, ConfigBackupRules.ManifestEntryName, ConfigBackupRules.SerializeManifest(manifest));
            WriteEntry(zip, "keybinds.json", "{}");
        }

        var target = Path.Combine(_workDir, "target-root");
        var result = new ConfigBackupService(target).Import(zipPath);

        Assert.Equal(1, result.FileCount);
        Assert.True(File.Exists(Path.Combine(target, "keybinds.json")));
    }

    [Fact]
    public void Import_SkipsTraversalCredentialAndUnknownEntriesWithoutWritingThem()
    {
        var zipPath = Path.Combine(_workDir, "crafted.zip");
        Directory.CreateDirectory(_workDir);
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var manifest = new ConfigBackupManifest(
                ConfigBackupRules.SchemaMajor, 0, null, DateTimeOffset.UtcNow, 5);
            WriteEntry(zip, ConfigBackupRules.ManifestEntryName, ConfigBackupRules.SerializeManifest(manifest));
            WriteEntry(zip, "appconfig.json", "{ \"legit\": true }");
            WriteEntry(zip, "../escape.txt", "escaped");                    // traversal
            WriteEntry(zip, "profiles/../../escape2.txt", "escaped");       // nested traversal
            WriteEntry(zip, "github-token.dat", "planted-credential");      // credential plant
            WriteEntry(zip, "DotCharter.exe", "planted-binary");            // payload plant
        }

        var target = Path.Combine(_workDir, "sandbox", "target-root");
        var result = new ConfigBackupService(target).Import(zipPath);

        Assert.Equal(1, result.FileCount);
        Assert.Equal(4, result.SkippedEntries.Count);
        Assert.True(File.Exists(Path.Combine(target, "appconfig.json")));
        Assert.False(File.Exists(Path.Combine(target, "github-token.dat")));
        Assert.False(File.Exists(Path.Combine(target, "DotCharter.exe")));
        Assert.False(File.Exists(Path.Combine(_workDir, "sandbox", "escape.txt")));
        Assert.False(File.Exists(Path.Combine(_workDir, "sandbox", "escape2.txt")));
        Assert.False(File.Exists(Path.Combine(_workDir, "escape.txt")));
        Assert.False(File.Exists(Path.Combine(_workDir, "escape2.txt")));
    }

    // ---- untrusted-archive bounds -------------------------------------------------

    /// <summary>
    ///     A backup archive is a file the user RECEIVED from somewhere — a second
    ///     machine, a cloud drive, a friend — so it is untrusted like the LAN
    ///     project transfer and the profile bundle, and it had NEITHER of the two
    ///     bounds those got: <c>Import</c> enumerated <c>zip.Entries</c> with no
    ///     count pre-check and extracted with no decompressed-byte budget.
    ///     <para>
    ///         The zip-slip and allowlist guards were already correct and are
    ///         unchanged — the tests above still pin them.
    ///     </para>
    /// </summary>
    [Fact]
    public void Import_RefusesAnArchiveDeclaringMoreMembersThanTheBoundAllows()
    {
        var zipPath = Path.Combine(_workDir, "many.zip");
        Directory.CreateDirectory(_workDir);
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var manifest = new ConfigBackupManifest(
                ConfigBackupRules.SchemaMajor, 0, null, DateTimeOffset.UtcNow, 1);
            WriteEntry(zip, ConfigBackupRules.ManifestEntryName, ConfigBackupRules.SerializeManifest(manifest));
            for (var i = 0; i < ConfigBackupService.MaxImportEntries + 8; i++)
                zip.CreateEntry($"recovery/{i}.rscp");
        }

        // The point of the fixture: a small file declaring tens of thousands of
        // members, each of which used to become a live object on `zip.Entries`.
        Assert.True(new FileInfo(zipPath).Length < 4 * 1024 * 1024);

        var target = Path.Combine(_workDir, "sandbox-many");
        Assert.Throws<InvalidDataException>(() => new ConfigBackupService(target).Import(zipPath));

        // The assertion that matters: the refusal came from the declared count,
        // BEFORE the archive was opened. Falling through to the extractor's own
        // per-entry ceiling would also throw — after writing 16,384 files.
        Assert.False(Directory.Exists(target), "the pre-check must refuse before anything is written");
    }

    /// <summary>
    ///     The decompressed-byte budget. The ceiling is passed low here through
    ///     the same formula the service uses, so the gate does not have to write
    ///     half a gigabyte to watch it bite; the shipped FLOOR is asserted below.
    /// </summary>
    [Fact]
    public void Import_RefusesAnArchiveThatInflatesPastItsBudget()
    {
        var zipPath = Path.Combine(_workDir, "bomb.zip");
        Directory.CreateDirectory(_workDir);
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var manifest = new ConfigBackupManifest(
                ConfigBackupRules.SchemaMajor, 0, null, DateTimeOffset.UtcNow, 1);
            WriteEntry(zip, ConfigBackupRules.ManifestEntryName, ConfigBackupRules.SerializeManifest(manifest));

            // 8 MiB of zeros in a ~8 KB archive, under an ALLOWLISTED path so it
            // is the BUDGET that refuses it and not the allowlist.
            using var entry = zip.CreateEntry("recovery/bomb.rscp").Open();
            var chunk = new byte[1024 * 1024];
            for (var i = 0; i < 8; i++) entry.Write(chunk);
        }

        Assert.True(new FileInfo(zipPath).Length < 128 * 1024);

        var target = Path.Combine(_workDir, "sandbox-bomb");
        var service = new ConfigBackupService(target) { ImportBudgetOverrideForTests = 1024 * 1024 };

        var error = Assert.Throws<InvalidDataException>(() => service.Import(zipPath));
        Assert.Contains("BudgetExceeded", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A REFUSED archive must leave the live config root byte-for-byte as it
    ///     was.
    ///     <para>
    ///         This is the data-loss case. <c>ExtractBounded</c> refuses
    ///         MID-ARCHIVE, and because <c>Export</c> enumerates with
    ///         <c>AllDirectories</c> the config-root FILES — settings, keybinds,
    ///         profiles, the practice store — are written into the archive BEFORE
    ///         the large subtrees, so they are precisely the members already
    ///         overwritten when a truncated or oversized archive fails later.
    ///         Restoring a backup that an interrupted cloud sync had truncated
    ///         answered "the archive was refused" while the user's settings and
    ///         every stored personal best had already been replaced, with no
    ///         undo.
    ///     </para>
    /// </summary>
    [Fact]
    public void Import_RefusingAnArchive_LeavesEveryLiveFileUntouched()
    {
        Directory.CreateDirectory(_workDir);
        var target = Path.Combine(_workDir, "sandbox-refusal");
        Directory.CreateDirectory(target);

        // The live state a restore is being run against.
        var live = new Dictionary<string, string>
        {
            ["appconfig.json"] = """{"live":"settings"}""",
            ["keybinds.json"] = """{"live":"keybinds"}""",
            ["player-profiles.json"] = """{"live":"profiles"}""",
            ["practice-profiles.json"] = """{"live":"bests"}"""
        };
        foreach (var (name, body) in live)
            File.WriteAllText(Path.Combine(target, name), body);

        var zipPath = Path.Combine(_workDir, "truncated.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var manifest = new ConfigBackupManifest(
                ConfigBackupRules.SchemaMajor, 0, null, DateTimeOffset.UtcNow, 5);
            WriteEntry(zip, ConfigBackupRules.ManifestEntryName, ConfigBackupRules.SerializeManifest(manifest));

            // Export order: the small root files first, the big subtree last —
            // so the root files are the ones already written when the budget
            // bites on the tail.
            foreach (var name in live.Keys)
                WriteEntry(zip, name, $$"""{"from":"the archive: {{name}}"}""");

            using var entry = zip.CreateEntry("recovery/huge.rscp").Open();
            var chunk = new byte[1024 * 1024];
            for (var i = 0; i < 8; i++) entry.Write(chunk);
        }

        var service = new ConfigBackupService(target) { ImportBudgetOverrideForTests = 1024 * 1024 };

        var error = Assert.Throws<InvalidDataException>(() => service.Import(zipPath));
        Assert.Contains("BudgetExceeded", error.Message, StringComparison.Ordinal);

        foreach (var (name, body) in live)
            Assert.Equal(body, File.ReadAllText(Path.Combine(target, name)));

        // …and no scratch is left where a portable install would broadcast it.
        Assert.False(Directory.Exists(Path.Combine(target, ConfigBackupService.ImportStagingFolderName)));
        Assert.False(Directory.Exists(Path.Combine(target, "recovery")));
    }

    /// <summary>
    ///     The same for a member this build cannot inflate, which throws STRAIGHT
    ///     OUT of the inflater rather than being refused by a budget. It is the
    ///     other way out of <c>Import</c>, and only the <c>finally</c> around the
    ///     staging directory covers it.
    /// </summary>
    [Fact]
    public void Import_AMemberItCannotInflate_AlsoLeavesEveryLiveFileUntouched()
    {
        Directory.CreateDirectory(_workDir);
        var target = Path.Combine(_workDir, "sandbox-unreadable");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "appconfig.json"), """{"live":"settings"}""");
        File.WriteAllText(Path.Combine(target, "keybinds.json"), """{"live":"keybinds"}""");

        var zipPath = Path.Combine(_workDir, "unreadable.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var manifest = new ConfigBackupManifest(
                ConfigBackupRules.SchemaMajor, 0, null, DateTimeOffset.UtcNow, 3);
            WriteEntry(zip, ConfigBackupRules.ManifestEntryName, ConfigBackupRules.SerializeManifest(manifest));
            WriteEntry(zip, "appconfig.json", """{"from":"the archive"}""");
            WriteEntry(zip, "keybinds.json", """{"from":"the archive"}""");
            WriteEntry(zip, "recovery/late.rscp", new string('x', 4096));
        }

        // Re-stamp the LAST member's compression method (central directory copy,
        // which is the one ZipArchive reads) to 6 = Implode, which .NET has no
        // decompressor for. Every header stays structurally valid, so the archive
        // opens and the two earlier members inflate cleanly — this one throws.
        // Mangling the DATA would not do: .NET's ZipArchive verifies no CRC on
        // read, so a corrupt member is silently returned as different bytes.
        var bytes = File.ReadAllBytes(zipPath);
        var central = CentralDirectoryHeaderOffset(bytes, "recovery/late.rscp");
        bytes[central + 10] = 6;
        bytes[central + 11] = 0;
        File.WriteAllBytes(zipPath, bytes);

        var service = new ConfigBackupService(target);
        Assert.ThrowsAny<InvalidDataException>(() => service.Import(zipPath));

        Assert.Equal("""{"live":"settings"}""", File.ReadAllText(Path.Combine(target, "appconfig.json")));
        Assert.Equal("""{"live":"keybinds"}""", File.ReadAllText(Path.Combine(target, "keybinds.json")));
        Assert.False(Directory.Exists(Path.Combine(target, ConfigBackupService.ImportStagingFolderName)));
        Assert.False(Directory.Exists(Path.Combine(target, "recovery")));
    }

    /// <summary>An interrupted earlier import must not be able to leak into the
    /// next one: staging is scratch, and is cleared before it is used.</summary>
    [Fact]
    public void Import_ClearsStagingLeftBehindByAnInterruptedRun()
    {
        Directory.CreateDirectory(_workDir);
        var target = Path.Combine(_workDir, "sandbox-leftover");
        var staging = Path.Combine(target, ConfigBackupService.ImportStagingFolderName);
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "appconfig.json"), """{"stale":"from a killed run"}""");

        var zipPath = Path.Combine(_workDir, "good.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var manifest = new ConfigBackupManifest(
                ConfigBackupRules.SchemaMajor, 0, null, DateTimeOffset.UtcNow, 1);
            WriteEntry(zip, ConfigBackupRules.ManifestEntryName, ConfigBackupRules.SerializeManifest(manifest));
            WriteEntry(zip, "keybinds.json", """{"from":"the archive"}""");
        }

        var result = new ConfigBackupService(target).Import(zipPath);

        Assert.Equal(1, result.FileCount);
        Assert.Equal("""{"from":"the archive"}""", File.ReadAllText(Path.Combine(target, "keybinds.json")));
        Assert.False(File.Exists(Path.Combine(target, "appconfig.json")));
        Assert.False(Directory.Exists(staging));
    }

    [Fact]
    public void TheImportCeilingFloorsAndScales()
    {
        Assert.Equal(512L * 1024 * 1024, ConfigBackupService.ImportInflationCeiling(0));
        Assert.Equal(512L * 1024 * 1024, ConfigBackupService.ImportInflationCeiling(-1));
        Assert.Equal(8L * 1024 * 1024 * 1024, ConfigBackupService.ImportInflationCeiling(1024L * 1024 * 1024));
    }

    [Fact]
    public void Import_RefusesSomethingThatIsNotAZipAtAll()
    {
        Directory.CreateDirectory(_workDir);
        var path = Path.Combine(_workDir, "not-a-zip.zip");
        File.WriteAllBytes(path, [1, 2, 3, 4, 5, 6, 7, 8]);

        Assert.Throws<InvalidDataException>(
            () => new ConfigBackupService(Path.Combine(_workDir, "sandbox-nz")).Import(path));
    }

    // ---- fixture -----------------------------------------------------------------

    /// <summary>A config root with one of everything: every included user-data
    /// file/dir, every credential store, and cache/payload files that must stay
    /// behind (the payload files simulate a portable install, where the config
    /// root IS the install directory).</summary>
    private string CreatePopulatedConfigRoot()
    {
        var root = Path.Combine(_workDir, "source-root");
        Directory.CreateDirectory(root);

        void Write(string relative, string content)
        {
            var path = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content, Encoding.UTF8);
        }

        Write("appconfig.json", "{ \"themePresetId\": \"midnight\", \"uiScale\": 1.25 }");
        Write("keybinds.json", "{ \"togglePlayback\": \"Space\" }");
        Write("player-profiles.json", "{ \"profiles\": [ { \"id\": \"p1\" } ] }");
        Write("practice-profiles.json", "{ \"songs\": {} }");
        Write(Path.Combine("profiles", "default", "stats.json"), "{ \"totalNotesHit\": 12345 }");
        Write(Path.Combine("languages", "fr-FR.json"), "{ \"Language_DisplayName\": \"Français\" }");
        Write(Path.Combine("recovery", "snapshot.rscp"), "snapshot-bytes");

        // Must never travel:
        Write("github-token.dat", "dpapi-sealed-github-token");
        Write("online-session.bin", "dpapi-sealed-session");
        Write("online-server-session.bin", "dpapi-sealed-server-session");
        Write("library-index.db", "sqlite-cache");
        Write(Path.Combine("logs", "dotcharter.log"), "log line");
        Write(Path.Combine("updates", "DotCharter-v9-win-x64.zip"), "downloaded update");
        Write("DotCharter.exe", "app-binary");        // portable install simulation
        Write("hostfxr.dll", "host-binary");

        return root;
    }

    /// <summary>Byte offset of an entry's CENTRAL DIRECTORY header — the copy
    /// ZipArchive actually reads. The name appears twice: first in the local file
    /// header, then here, 46 bytes into the central header.</summary>
    private static int CentralDirectoryHeaderOffset(byte[] zip, string entryName)
    {
        var name = Encoding.UTF8.GetBytes(entryName);
        var local = IndexOf(zip, name, 0);
        Assert.True(local > 0, $"no local file header found for '{entryName}'.");

        var central = IndexOf(zip, name, local + 1);
        Assert.True(central >= 46, $"no central directory header found for '{entryName}'.");
        return central - 46;
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int from)
    {
        for (var i = from; i + needle.Length <= haystack.Length; i++)
        {
            var hit = true;
            for (var j = 0; j < needle.Length && hit; j++)
            {
                if (haystack[i + j] != needle[j])
                    hit = false;
            }

            if (hit) return i;
        }

        return -1;
    }

    private static void WriteEntry(ZipArchive zip, string entryName, string content)
    {
        var entry = zip.CreateEntry(entryName);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }
}
