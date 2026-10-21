using System.IO.Compression;
using System.Reflection;
using System.Text.Json.Nodes;
using DotCharter.Services.Infrastructure.Backup;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Infrastructure;

/// <summary>
///     A backup is a file the user RECEIVED. Restoring it used to take
///     <c>appconfig.json</c> verbatim (the guitar VST chain it names is loaded at
///     startup, the ffmpeg / yt-dlp paths and the Claude Code path are executed, the
///     AI address receives the stored key) and any file under <c>profiles/</c>
///     whatever its type, so a backup from someone else could pick the native code
///     that runs as the user. These tests pin the guards: every setting has a
///     category, only data files travel, and the restore says what it left alone.
/// </summary>
public sealed class ConfigBackupImportSecurityTests : IDisposable
{
    private readonly string _workDir = Path.Combine(
        Path.GetTempPath(), "dc-backup-sec-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => BestEffortDelete.Directory(_workDir);

    // ---- category: Never ----------------------------------------------------------

    private const string MaliciousConfig = """
        {
          "UiScale": 1.25,
          "LanguageCulture": "de-DE",
          "GuitarVstChain": [ { "Id": "5b1c2a3e-0000-0000-0000-000000000001", "PluginPath": "\\\\evil-host\\share\\payload.vst3", "Bypass": false, "Player": 0 } ],
          "LastGuitarVstPath": "\\\\evil-host\\share\\legacy.vst3",
          "LastInstrumentVstPath": "\\\\evil-host\\share\\synth.vst3",
          "FfmpegPath": "\\\\evil-host\\share\\ffmpeg.exe",
          "YtDlpPath": "\\\\evil-host\\share\\yt-dlp.exe",
          "LastProjectPath": "\\\\evil-host\\share\\song.rscp",
          "PsarcLibraryPath": "\\\\evil-host\\share\\library",
          "RecentProjects": [ { "Path": "\\\\evil-host\\share\\song.rscp", "DisplayName": "x" } ],
          "LogFilePath": "\\\\evil-host\\share\\log.txt",
          "CrashReportOptIn": true,
          "Ai": { "Kind": "ClaudeCli", "ModelId": "claude-x", "BaseUrl": "\\\\evil-host\\share\\claude.exe", "WebResearch": false },
          "LastExportDir": ""
        }
        """;

    [Fact]
    public void Import_TakesNothingThatExecutesLoadsOrAutoOpens_AndKeepsThisMachinesValue()
    {
        var target = NewRoot("target");
        File.WriteAllText(Path.Combine(target, "appconfig.json"), """
            {
              "UiScale": 1.0,
              "FfmpegPath": "C:\\tools\\ffmpeg.exe",
              "LastProjectPath": "C:\\work\\mine.rscp",
              "CrashReportOptIn": false,
              "Ai": { "Kind": "Claude", "ModelId": "mine", "BaseUrl": null }
            }
            """);

        var result = new ConfigBackupService(target).Import(ArchiveWith(("appconfig.json", MaliciousConfig)));

        var text = File.ReadAllText(Path.Combine(target, "appconfig.json"));
        var imported = JsonNode.Parse(text)!.AsObject();

        // Plain preferences came across…
        Assert.Equal(1.25, imported["UiScale"]!.GetValue<double>());
        Assert.Equal("de-DE", imported["LanguageCulture"]!.GetValue<string>());

        // …this machine's own values stayed…
        Assert.Equal("C:\\tools\\ffmpeg.exe", imported["FfmpegPath"]!.GetValue<string>());
        Assert.Equal("C:\\work\\mine.rscp", imported["LastProjectPath"]!.GetValue<string>());
        Assert.False(imported["CrashReportOptIn"]!.GetValue<bool>());

        // …nothing the sender chose survived anywhere in the file…
        Assert.DoesNotContain("evil-host", text, StringComparison.Ordinal);
        Assert.False(imported.ContainsKey("GuitarVstChain"));
        Assert.False(imported.ContainsKey("LastGuitarVstPath"));
        Assert.False(imported.ContainsKey("LastInstrumentVstPath"));
        Assert.False(imported.ContainsKey("YtDlpPath"));
        Assert.False(imported.ContainsKey("LogFilePath"));

        // …and the AI provider: the other members came across, the provider and its
        // address (where the stored key goes / which executable runs) did not.
        var ai = imported["Ai"]!.AsObject();
        Assert.Equal("claude-x", ai["ModelId"]!.GetValue<string>());
        Assert.False(ai["WebResearch"]!.GetValue<bool>());
        Assert.Equal("Claude", ai["Kind"]!.GetValue<string>());
        Assert.Null(ai["BaseUrl"]);

        Assert.Equal(
            new[]
            {
                "GuitarVstChain", "LastGuitarVstPath", "LastInstrumentVstPath", "FfmpegPath", "YtDlpPath",
                "LastProjectPath", "PsarcLibraryPath", "RecentProjects", "LogFilePath", "CrashReportOptIn",
                "Ai.Kind", "Ai.BaseUrl"
            }.Order(StringComparer.Ordinal),
            result.NotRestoredSettings.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Import_OnAMachineWithoutASettingsFile_StillStripsThem()
    {
        var target = NewRoot("fresh");

        var result = new ConfigBackupService(target).Import(ArchiveWith(("appconfig.json", MaliciousConfig)));

        var text = File.ReadAllText(Path.Combine(target, "appconfig.json"));
        Assert.DoesNotContain("evil-host", text, StringComparison.Ordinal);
        Assert.Contains("1.25", text, StringComparison.Ordinal);
        Assert.NotEmpty(result.NotRestoredSettings);
    }

    [Fact]
    public void Import_MatchesTheKeysInAnyCasing()
    {
        var target = NewRoot("casing");

        new ConfigBackupService(target).Import(ArchiveWith(("appconfig.json",
            """{ "guitarvstchain": [ { "PluginPath": "\\\\evil-host\\x.vst3" } ], "FFMPEGPATH": "\\\\evil-host\\f.exe", "ai": { "BASEURL": "\\\\evil-host\\c.exe" } }""")));

        Assert.DoesNotContain("evil-host", File.ReadAllText(Path.Combine(target, "appconfig.json")),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Import_DropsAKeyNoCategoryNamesAndKeepsThisMachinesCopyOfIt()
    {
        var target = NewRoot("unknown");
        File.WriteAllText(Path.Combine(target, "appconfig.json"), """{ "FromANewerBuild": "mine" }""");

        var result = new ConfigBackupService(target).Import(ArchiveWith(("appconfig.json",
            """{ "FromANewerBuild": "theirs", "BrandNewExecutablePath": "\\\\evil-host\\x.exe", "UiScale": 2.0 }""")));

        var imported = JsonNode.Parse(File.ReadAllText(Path.Combine(target, "appconfig.json")))!.AsObject();
        Assert.Equal("mine", imported["FromANewerBuild"]!.GetValue<string>());
        Assert.False(imported.ContainsKey("BrandNewExecutablePath"));
        Assert.Equal(2.0, imported["UiScale"]!.GetValue<double>());
        Assert.Contains("BrandNewExecutablePath", result.NotRestoredSettings);
    }

    [Fact]
    public void Import_ReportsOnlyTheSettingsTheBackupActuallyCarried()
    {
        var target = NewRoot("empty-keys");

        // The app writes EVERY setting, so an empty string / list is the default.
        var result = new ConfigBackupService(target).Import(ArchiveWith(("appconfig.json",
            """{ "FfmpegPath": "", "GuitarVstChain": [], "UiScale": 2.0, "Ai": { "Kind": "None", "BaseUrl": null } }""")));

        Assert.Empty(result.NotRestoredSettings);
    }

    // ---- category: Value ----------------------------------------------------------

    [Fact]
    public void Import_TakesPlainPreferencesAsTheyAre_AndLeavesAnUntouchedFileByteForByte()
    {
        var target = NewRoot("plain");
        const string body = """{ "UiScale": 2.0, "LanguageCulture": "ja-JP" }""";

        var result = new ConfigBackupService(target).Import(ArchiveWith(("appconfig.json", body)));

        Assert.Equal(body, File.ReadAllText(Path.Combine(target, "appconfig.json")));
        Assert.Empty(result.NotRestoredSettings);
    }

    // ---- category: DataFolder -----------------------------------------------------

    [Fact]
    public void Import_RestoresADataFolder_WhenItIsALocalDirectoryThatExistsHere()
    {
        var target = NewRoot("folders");
        var library = NewRoot("psarc-library");
        var exports = NewRoot("exports");

        var result = new ConfigBackupService(target).Import(ArchiveWith(("appconfig.json",
            Config(("PsarcLibraryPath", library), ("LastExportDir", exports), ("LastAudioDir", NewRoot("audio"))))));

        var imported = ReadConfig(target);
        Assert.Equal(library, imported["PsarcLibraryPath"]!.GetValue<string>());
        Assert.Equal(exports, imported["LastExportDir"]!.GetValue<string>());
        Assert.Empty(result.NotRestoredSettings);
    }

    [Fact]
    public void Import_KeepsThisMachinesFolder_AndReportsIt_WhenTheBackupsDoesNotExistHere()
    {
        var target = NewRoot("folders-missing");
        var mine = NewRoot("mine");
        File.WriteAllText(Path.Combine(target, "appconfig.json"), Config(("PsarcLibraryPath", mine)));
        var missing = Path.Combine(_workDir, "no-such-folder");

        var result = new ConfigBackupService(target).Import(ArchiveWith(("appconfig.json",
            Config(("PsarcLibraryPath", missing)))));

        Assert.Equal(mine, ReadConfig(target)["PsarcLibraryPath"]!.GetValue<string>());
        Assert.Equal(["PsarcLibraryPath"], result.NotRestoredSettings);
    }

    [Theory]
    [InlineData(@"\\evil-host\share\music")]       // UNC
    [InlineData("//evil-host/share/music")]        // UNC, forward slashes
    [InlineData(@"\\?\C:\Windows")]                // device path
    [InlineData(@"\\.\C:\Windows")]
    [InlineData(@"Music\relative")]                // not rooted
    [InlineData(@"\rooted-without-a-drive")]
    [InlineData(@"C:relative-to-the-drive-cwd")]
    public void Import_RefusesADataFolderThatIsNotALocalRootedPath(string value)
    {
        // Existence is not the guard here: a probe that says "exists" for everything
        // must still be refused by the shape of the path.
        var filtered = AppConfigBackupFilter.TryFilter(Config(("LastAudioDir", value)), null, _ => true, _ => true)!;

        Assert.DoesNotContain("LastAudioDir", filtered.Json, StringComparison.Ordinal);
        Assert.Equal(["LastAudioDir"], filtered.NotRestored);
    }

    [Fact]
    public void Import_RefusesADataFolderWithDotSegments()
    {
        var real = NewRoot("dotted");
        var filtered = AppConfigBackupFilter.TryFilter(
            Config(("LastAudioDir", real + @"\..\" + Path.GetFileName(real))), null, _ => true, _ => true)!;

        Assert.Equal(["LastAudioDir"], filtered.NotRestored);
    }

    // ---- category: ExistingProjectFiles --------------------------------------------

    [Fact]
    public void Import_KeepsOnlyRecentProjectsThatExistHereAsLocalRscpFiles()
    {
        var target = NewRoot("recents");
        var real = Path.Combine(NewRoot("projects"), "song.rscp");
        File.WriteAllText(real, "x");
        var notProject = Path.Combine(_workDir, "projects", "tool.exe");
        File.WriteAllText(notProject, "x");

        var recents = new JsonArray(
            Recent(real), Recent(notProject), Recent(@"\\evil-host\share\a.rscp"), Recent(Path.Combine(_workDir, "gone.rscp")));
        var result = new ConfigBackupService(target).Import(ArchiveWith(("appconfig.json",
            new JsonObject { ["RecentProjects"] = recents }.ToJsonString())));

        var kept = ReadConfig(target)["RecentProjects"]!.AsArray();
        Assert.Equal(real, Assert.Single(kept)!["Path"]!.GetValue<string>());
        Assert.Equal(["RecentProjects"], result.NotRestoredSettings);
    }

    // ---- every setting has exactly one category ------------------------------------

    /// <summary>
    ///     The allowlist is hand-maintained over a 250-key type. A NEW setting that is in
    ///     none of its lists would silently be treated as Never (safe, but a lost
    ///     preference); one a reviewer forgets to look at is how a path/executable key
    ///     would have slipped through under the old denylist. So every public
    ///     <c>AppConfig</c> property must be classified, exactly once, and every
    ///     classified name must be a real property.
    /// </summary>
    [Fact]
    public void EveryAppConfigSetting_HasExactlyOneCategory()
    {
        var properties = typeof(AppConfig).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

        var all = new[]
        {
            AppConfigBackupFilter.ValueKeys, AppConfigBackupFilter.DataFolderKeys,
            AppConfigBackupFilter.ExistingProjectFileKeys, AppConfigBackupFilter.NeverKeys
        }.SelectMany(x => x).ToList();

        Assert.Empty(all.GroupBy(n => n, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key));
        Assert.Equal(all.Count, AppConfigBackupFilter.ClassifiedNames.Count);

        var unclassified = properties.Where(p => !AppConfigBackupFilter.ClassifiedNames.Contains(p, StringComparer.Ordinal)).Order().ToList();
        Assert.True(unclassified.Count == 0,
            "these AppConfig settings are in no category of AppConfigBackupFilter, so a backup restore ignores them " +
            "(treated as Never) — review each: Value, DataFolder, ExistingProjectFiles or Never:\n  " +
            string.Join("\n  ", unclassified));

        var typos = all.Where(n => !properties.Contains(n)).ToList();
        Assert.True(typos.Count == 0, "classified names that are not AppConfig properties:\n  " + string.Join("\n  ", typos));
    }

    [Fact]
    public void EveryKindIsExercised_AndTheStartupAndExecutableKeysAreNever()
    {
        Assert.Equal(BackupSettingKind.Value, AppConfigBackupFilter.KindOf("UiScale"));
        Assert.Equal(BackupSettingKind.DataFolder, AppConfigBackupFilter.KindOf("PsarcLibraryPath"));
        Assert.Equal(BackupSettingKind.ExistingProjectFiles, AppConfigBackupFilter.KindOf("RecentProjects"));
        Assert.Equal(BackupSettingKind.Never, AppConfigBackupFilter.KindOf("NotARealSetting"));

        foreach (var key in new[]
                 {
                     "GuitarVstChain", "LastGuitarVstPath", "LastInstrumentVstPath", "FfmpegPath", "YtDlpPath",
                     "LastProjectPath", "LogFilePath"
                 })
        {
            Assert.Equal(BackupSettingKind.Never, AppConfigBackupFilter.KindOf(key));
        }

        Assert.Equal(["Kind", "BaseUrl"], AppConfigBackupFilter.NestedNever["Ai"]);
    }

    [Fact]
    public void Import_RefusesASettingsFileThatIsNotAJsonObject_AndKeepsTheLiveOne()
    {
        var target = NewRoot("garbage");
        const string live = """{ "UiScale": 1.0 }""";
        File.WriteAllText(Path.Combine(target, "appconfig.json"), live);

        var result = new ConfigBackupService(target).Import(ArchiveWith(
            ("appconfig.json", "this is not json"),
            ("keybinds.json", "{}")));

        Assert.Equal(live, File.ReadAllText(Path.Combine(target, "appconfig.json")));
        Assert.Contains("appconfig.json", result.SkippedEntries);
        Assert.Equal(1, result.FileCount); // keybinds only
        Assert.True(File.Exists(Path.Combine(target, "keybinds.json")));
    }

    // ---- files --------------------------------------------------------------------

    [Theory]
    [InlineData("profiles/x.vst3")]
    [InlineData("profiles/0123abcd/payload.dll")]
    [InlineData("profiles/0123abcd/payload.exe")]
    [InlineData("profiles/0123abcd/run.ps1")]
    [InlineData("profiles/0123abcd/run.bat")]
    [InlineData("profiles/0123abcd/run.vbs")]
    [InlineData("profiles/0123abcd/payload.DLL")]
    [InlineData("profiles/0123abcd/payload.dll.")]            // Windows drops the trailing dot
    [InlineData("profiles/0123abcd/payload.dll ")]            // …and the trailing space
    [InlineData("profiles/0123abcd/noextension")]
    [InlineData("profiles/0123abcd/phrase.rscp")]             // right type, wrong tree
    [InlineData("profiles/evil.vst3/Contents/x86_64-win/evil.vst3")]
    [InlineData("profiles/evil.vst3/Contents/Resources/readme.json")] // a bundle's contents never travel
    [InlineData("themes/neon/hook.js")]
    [InlineData("themes/neon/theme.wav")]
    [InlineData("languages/fr-FR.exe")]
    [InlineData("languages/fr-FR.png")]                       // a language pack is .json only
    [InlineData("languages/fr-FR.rscp")]
    [InlineData("recovery/snapshot.rscp")]                    // crash snapshots never travel
    [InlineData("recovery/snapshot.rscp.json")]
    [InlineData("recovery/snapshot.dll")]
    [InlineData("phrases/riff.scr")]
    [InlineData("phrases/riff.json")]
    [InlineData("song-tones/tone.vst3")]
    [InlineData("song-tones/tone.rscp")]
    public void Rules_RefuseAnythingThatIsNotDataForItsTree(string path)
        => Assert.False(ConfigBackupRules.ShouldTransfer(path), path);

    [Theory]
    [InlineData("appconfig.json")]
    [InlineData("keybinds.json")]
    [InlineData("profiles/0123abcd/profile.json")]
    [InlineData("profiles/0123abcd/avatar.png")]
    [InlineData("profiles/0123abcd/avatar.JPG")]
    [InlineData("profiles/0123abcd/avatar.webp")]
    [InlineData("profiles/0123abcd/replays/replay-20260101-000000-abc.json")]
    [InlineData("profiles/0123abcd/takes/take-20260101-000000-abc.wav")]
    [InlineData("profiles/0123abcd/tone-rig.json")]
    [InlineData("profiles/0123abcd/tone-rig.corrupt-20260101-000000000.json")]
    [InlineData("languages/fr-FR.json")]
    [InlineData("themes/neon/theme.json")]
    [InlineData("themes/neon/background.png")]
    [InlineData("phrases/main-riff.rscp")]
    [InlineData("song-tones/tone-1a2b.json")]
    public void Rules_StillTransferTheFilesTheAppWrites(string path)
        => Assert.True(ConfigBackupRules.ShouldTransfer(path), path);

    [Fact]
    public void Rules_RecoveryIsStillUserData_SoAnUpdateNeverOverwritesIt()
    {
        Assert.True(ConfigBackupRules.ShouldInclude("recovery/snapshot.rscp"));
        Assert.False(ConfigBackupRules.ShouldTransfer("recovery/snapshot.rscp"));
    }

    [Fact]
    public void Import_RefusesExecutablesPluginBundlesAndRecoveryFiles_AndRestoresTheDataBesideThem()
    {
        var target = NewRoot("files");

        var result = new ConfigBackupService(target).Import(ArchiveWith(
            ("profiles/0123abcd/profile.json", "{}"),
            ("profiles/0123abcd/avatar.png", "png-bytes"),
            ("profiles/x.vst3", "native-code"),
            ("profiles/0123abcd/payload.dll", "native-code"),
            ("profiles/evil.vst3/Contents/x86_64-win/evil.vst3", "native-code"),
            ("profiles/0123abcd/run.ps1", "script"),
            ("recovery/abc.rscp", "snapshot"),
            ("recovery/abc.rscp.json", """{ "OriginalProjectPath": "C:\\Users\\someone\\Documents\\thesis.rscp" }""")));

        Assert.Equal(2, result.FileCount);
        Assert.Equal(6, result.SkippedEntries.Count);
        Assert.True(File.Exists(Path.Combine(target, "profiles", "0123abcd", "profile.json")));
        Assert.True(File.Exists(Path.Combine(target, "profiles", "0123abcd", "avatar.png")));
        Assert.Empty(Directory.EnumerateFiles(target, "*.vst3", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(target, "*.dll", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(target, "*.ps1", SearchOption.AllDirectories));
        Assert.False(Directory.Exists(Path.Combine(target, "recovery")));
    }

    [Fact]
    public void Export_LeavesStrayBinariesAndCrashSnapshotsBehind()
    {
        var source = NewRoot("export-src");
        File.WriteAllText(Path.Combine(source, "appconfig.json"), "{}");
        Directory.CreateDirectory(Path.Combine(source, "profiles", "p1"));
        File.WriteAllText(Path.Combine(source, "profiles", "p1", "profile.json"), "{}");
        File.WriteAllText(Path.Combine(source, "profiles", "p1", "stray.dll"), "binary");
        Directory.CreateDirectory(Path.Combine(source, "recovery"));
        File.WriteAllText(Path.Combine(source, "recovery", "crash.rscp"), "snapshot");

        var zipPath = Path.Combine(_workDir, "export.zip");
        new ConfigBackupService(source).Export(zipPath);

        using var zip = ZipFile.OpenRead(zipPath);
        Assert.Contains(zip.Entries, e => e.FullName == "profiles/p1/profile.json");
        Assert.DoesNotContain(zip.Entries, e => e.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(zip.Entries, e => e.FullName.StartsWith("recovery/", StringComparison.Ordinal));
    }

    // ---- helpers ------------------------------------------------------------------

    private static string Config(params (string Key, string Value)[] settings)
    {
        var obj = new JsonObject();
        foreach (var (key, value) in settings) obj[key] = value;
        return obj.ToJsonString();
    }

    private static JsonNode Recent(string path) => new JsonObject { ["Path"] = path, ["DisplayName"] = "x" };

    private static JsonObject ReadConfig(string root)
        => JsonNode.Parse(File.ReadAllText(Path.Combine(root, "appconfig.json")))!.AsObject();

    private string NewRoot(string name)
    {
        var root = Path.Combine(_workDir, name);
        Directory.CreateDirectory(root);
        return root;
    }

    private string ArchiveWith(params (string Name, string Content)[] entries)
    {
        Directory.CreateDirectory(_workDir);
        var path = Path.Combine(_workDir, "backup-" + Guid.NewGuid().ToString("N") + ".zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);

        var manifest = new ConfigBackupManifest(
            ConfigBackupRules.SchemaMajor, ConfigBackupRules.SchemaMinor, null, DateTimeOffset.UtcNow, entries.Length);
        Write(zip, ConfigBackupRules.ManifestEntryName, ConfigBackupRules.SerializeManifest(manifest));
        foreach (var (name, content) in entries) Write(zip, name, content);
        return path;
    }

    private static void Write(ZipArchive zip, string name, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open());
        writer.Write(content);
    }
}
