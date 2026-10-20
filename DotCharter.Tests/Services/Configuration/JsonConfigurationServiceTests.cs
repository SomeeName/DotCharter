using System.Collections.Concurrent;
using Avalonia.Input;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Configuration;

/// <summary>
///     Covers the chicken-and-egg logger wiring in <see cref="JsonConfigurationService" />:
///     errors captured before the logger exists must be replayed when it is attached.
///     Tests use a temporary working directory to avoid clobbering the real config files.
/// </summary>
[Collection("CwdSerialized")]
public class JsonConfigurationServiceTests : IDisposable
{
    private readonly string _originalCwd;
    private readonly string _tempDir;

    public JsonConfigurationServiceTests()
    {
        _originalCwd = Directory.GetCurrentDirectory();
        _tempDir = Path.Combine(Path.GetTempPath(), "DotCharterTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        Directory.SetCurrentDirectory(_tempDir);
        WritablePaths.OverrideRootsForTesting(_tempDir, _tempDir);
    }

    /// <summary>
    ///     Config writes are COALESCED onto a background thread, so a service a
    ///     test left un-drained would try to write into <c>_tempDir</c> after the
    ///     dir is gone and the path overrides are back. Every service this class
    ///     builds is tracked and drained first, which keeps all of its file I/O
    ///     inside the test that asked for it.
    ///     <para>
    ///         They are also built with NO debounce window (the product default is
    ///         250 ms). This class holds the process-wide CWD and the
    ///         <c>WritablePaths</c> overrides for its whole lifetime — the
    ///         <c>CwdSerialized</c> collection exists for exactly that — so
    ///         leaving each service a quarter second to drain would stretch that
    ///         window by seconds of wall clock across the class for no test's
    ///         benefit. Every test that asserts the FILE calls <c>Flush()</c>
    ///         explicitly anyway.
    ///     </para>
    /// </summary>
    private readonly List<JsonConfigurationService> _services = [];

    private JsonConfigurationService NewService() =>
        Track(new JsonConfigurationService(TimeSpan.Zero, null));

    private JsonConfigurationService Track(JsonConfigurationService service)
    {
        _services.Add(service);
        return service;
    }

    public void Dispose()
    {
        foreach (var service in _services) service.Dispose();
        WritablePaths.OverrideRootsForTesting(null, null);
        Directory.SetCurrentDirectory(_originalCwd);
        BestEffortDelete.Directory(_tempDir);
    }

    [Fact]
    public void LoadAppConfig_NoFile_ReturnsDefaults_AndLogsTraceAfterLoggerAttached()
    {
        var svc = NewService();
        var config = svc.LoadAppConfig();

        Assert.NotNull(config);
        Assert.Equal("system", config.LanguageCulture);

        var logger = new FakeAppLogger();
        svc.Logger = logger;

        // No pending errors since loading defaults isn't an error; replay list is empty.
        Assert.DoesNotContain(logger.Entries, e => e.Level == "ERROR");
    }

    [Fact]
    public void LoadAppConfig_CorruptFile_ReportsErrorBufferedUntilLoggerAttached()
    {
        File.WriteAllText(Path.Combine(_tempDir, "appconfig.json"), "{ this is not json ");

        var svc = NewService();
        var config = svc.LoadAppConfig();
        Assert.NotNull(config); // falls back to defaults

        var logger = new FakeAppLogger();
        svc.Logger = logger;

        Assert.Contains(logger.Entries,
            e => e.Level == "ERROR" && e.Category == AppLogCategory.Settings &&
                 e.Message.Contains("Failed to load app config", StringComparison.Ordinal));
    }

    [Fact]
    public void LoadAppConfig_CorruptFile_WithLoggerAttachedUpFront_ReportsImmediately()
    {
        File.WriteAllText(Path.Combine(_tempDir, "appconfig.json"), "not json");

        var svc = NewService();
        var logger = new FakeAppLogger();
        svc.Logger = logger;

        svc.LoadAppConfig();

        Assert.Contains(logger.Entries,
            e => e.Level == "ERROR" && e.Message.Contains("Failed to load app config", StringComparison.Ordinal));
    }

    /// <summary>
    ///     A config file that exists but could not be READ right now (an antivirus
    ///     scan, a cloud sync, a backup tool) is presumed INTACT. The load used to
    ///     cache <c>new AppConfig()</c> for it, and the next <c>Update</c> — one
    ///     happens on nearly every open and save — atomically wrote those defaults
    ///     over every setting the user had.
    /// </summary>
    [Fact]
    public void LoadAppConfig_Unreadable_NeverWritesDefaultsOverIt_AndHealsWhenTheLockGoes()
    {
        var path = Path.Combine(_tempDir, "appconfig.json");
        File.WriteAllText(path, """{"LanguageCulture":"ja-JP"}""");
        var before = File.ReadAllBytes(path);

        var svc = NewService();

        // FileAccess.Read shared as Write|Delete: a reader is denied (our load
        // throws IOException) while a RENAME over the file is still permitted —
        // i.e. the writer really could have clobbered it. Sharing Delete too is
        // what makes this test non-vacuous.
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Write | FileShare.Delete))
        {
            Assert.Equal("system", svc.LoadAppConfig().LanguageCulture); // stand-in defaults
            svc.Update(c => c.LanguageCulture = "de-DE");
            svc.Flush();
        }

        Assert.Equal(before, File.ReadAllBytes(path));

        // Not cached while degraded, so the session goes back to the real file on
        // its own the moment the hold is released.
        Assert.Equal("ja-JP", svc.LoadAppConfig().LanguageCulture);
    }

    /// <summary>
    ///     CORRUPT is the other half: readable bytes that are not a config. Those
    ///     are the user's only copy of their settings, so they are moved ASIDE
    ///     rather than overwritten, and the session starts fresh from there.
    /// </summary>
    [Fact]
    public void LoadAppConfig_Corrupt_IsQuarantinedAside_AndTheSessionStartsFresh()
    {
        var path = Path.Combine(_tempDir, "appconfig.json");
        const string corrupt = "{ this is not json ";
        File.WriteAllText(path, corrupt);

        var svc = NewService();
        svc.Update(c => c.LanguageCulture = "de-DE");
        svc.Flush();

        var quarantined = Directory.GetFiles(_tempDir, "appconfig.corrupt-*.json");
        Assert.Single(quarantined);
        Assert.Equal(corrupt, File.ReadAllText(quarantined[0]));
        Assert.Contains("de-DE", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void SaveAppConfig_ThenLoad_RoundTripsAllFields()
    {
        var svc = NewService();
        var logger = new FakeAppLogger();
        svc.Logger = logger;

        var config = new AppConfig
        {
            LastProjectPath = "some/path.rscp",
            LanguageCulture = "en-US",
            MinNoteDistanceDivisor = 128,
            // Default is true; persist the non-default so the round-trip proves
            // the field actually serializes (a saved-but-inert setting bug).
            GpImportTruncateShortNotes = false,
            // Same rule: both lyric-span toggles flipped away from their defaults.
            LyricSpanWordGap = false,
            LyricSpanPhraseGap = true
        };

        svc.SaveAppConfig(config);
        // Config saves are COALESCED — SaveAppConfig arms a debounced background
        // write rather than paying fsync on the caller's thread. Anything that
        // reads the FILE (here, a second service instance) has to drain it first.
        svc.Flush();

        // Force a re-read using a fresh instance.
        var fresh = NewService();
        var loaded = fresh.LoadAppConfig();

        Assert.Equal("some/path.rscp", loaded.LastProjectPath);
        Assert.Equal("en-US", loaded.LanguageCulture);
        Assert.Equal(128, loaded.MinNoteDistanceDivisor);
        Assert.False(loaded.GpImportTruncateShortNotes);
        Assert.False(loaded.LyricSpanWordGap);
        Assert.True(loaded.LyricSpanPhraseGap);
        Assert.True(logger.HasEntryContaining("TRACE", "Saved app config"));
    }

    /// <summary>
    ///     A config written by a build that still had the removed features (the
    ///     LAN scoreboard's identity and download opt-in, the v0.60 online keys)
    ///     must still load: the serializer keeps unknown members, the known
    ///     ones keep their values, and the next save keeps the dead keys (#806).
    /// </summary>
    [Fact]
    public void LoadAppConfig_WithKeysOfRemovedFeatures_StillLoads()
    {
        var path = Path.Combine(_tempDir, "appconfig.json");
        File.WriteAllText(path, """
            {
              "LanguageCulture": "de-DE",
              "LanDisplayName": "Stage PC",
              "LanPeerId": "0f6d1c7e-2b43-4a0c-9d5e-1f2a3b4c5d6e",
              "AllowPeersDownloadSongs": true,
              "OnlineApiUrl": "https://example.invalid",
              "RoomBoardNoticeDismissed": true
            }
            """);

        var svc = NewService();
        var logger = new FakeAppLogger();
        svc.Logger = logger;
        var loaded = svc.LoadAppConfig();

        Assert.Equal("de-DE", loaded.LanguageCulture);
        Assert.Empty(Directory.GetFiles(_tempDir, "appconfig.corrupt-*.json"));

        svc.SaveAppConfig(loaded);
        svc.Flush();
        var written = File.ReadAllText(path);
        Assert.Contains("de-DE", written, StringComparison.Ordinal);
        Assert.Contains("LanPeerId", written, StringComparison.Ordinal);
        Assert.Contains("AllowPeersDownloadSongs", written, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The two stem-orchestrator keys (#662: deleted with the JuceSharp
    ///     orchestrator, which never separated anything) sit in every config
    ///     written before the removal. Such a config must still load with the
    ///     stem settings that DO drive separation intact, and the next save keeps
    ///     the dead keys (#806: unknown members are never deleted).
    /// </summary>
    [Fact]
    public void LoadAppConfig_WithTheRemovedStemOrchestratorKeys_StillLoads()
    {
        var path = Path.Combine(_tempDir, "appconfig.json");
        File.WriteAllText(path, """
            {
              "StemSeparationExecutionProvider": "Cpu",
              "StemSeparationGpuAdapter": "1:NVIDIA GeForce RTX 3080",
              "StemSeparationMaxConcurrent": 3,
              "StemSeparationEnableCpuFallback": false
            }
            """);

        var svc = NewService();
        var loaded = svc.LoadAppConfig();

        Assert.Equal("Cpu", loaded.StemSeparationExecutionProvider);
        Assert.Equal("1:NVIDIA GeForce RTX 3080", loaded.StemSeparationGpuAdapter);
        Assert.Empty(Directory.GetFiles(_tempDir, "appconfig.corrupt-*.json"));

        svc.SaveAppConfig(loaded);
        svc.Flush();
        var written = File.ReadAllText(path);
        Assert.Contains("\"StemSeparationExecutionProvider\": \"Cpu\"", written, StringComparison.Ordinal);
        Assert.Contains("StemSeparationMaxConcurrent", written, StringComparison.Ordinal);
        Assert.Contains("StemSeparationEnableCpuFallback", written, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadAppConfig_CachesResult()
    {
        var svc = NewService();
        var first = svc.LoadAppConfig();
        var second = svc.LoadAppConfig();
        Assert.Same(first, second);
    }

    /// <summary>
    ///     A backup import rewrites appconfig.json behind the service. Without an
    ///     invalidation the cached (pre-import) instance keeps being served — and is
    ///     eventually written back over the imported file, losing the import.
    /// </summary>
    [Fact]
    public void InvalidateCache_MakesTheNextLoadSeeAFileWrittenBehindTheService()
    {
        var svc = NewService();
        svc.SaveAppConfig(new AppConfig { LastProjectPath = "before.rscp", MinNoteDistanceDivisor = 64 });
        svc.Flush();

        var cached = svc.LoadAppConfig();
        Assert.Equal("before.rscp", cached.LastProjectPath);

        // Stand in for the import: replace the file without going through the service.
        var imported = NewService();
        imported.SaveAppConfig(new AppConfig { LastProjectPath = "imported.rscp", MinNoteDistanceDivisor = 128 });
        imported.Flush();

        // Still the stale instance...
        Assert.Same(cached, svc.LoadAppConfig());

        svc.InvalidateCache();

        var reloaded = svc.LoadAppConfig();
        Assert.NotSame(cached, reloaded);
        Assert.Equal("imported.rscp", reloaded.LastProjectPath);
        Assert.Equal(128, reloaded.MinNoteDistanceDivisor);
    }

    /// <summary>
    ///     Finding #2: the AppConfig read-modify-write must be atomic. Hammer
    ///     <see cref="JsonConfigurationService.Update" /> from many threads
    ///     incrementing a scalar field — under the lock every increment lands
    ///     (final == total calls) and nothing throws. Without the lock,
    ///     interleaved read-modify-writes would lose increments.
    /// </summary>
    [Fact]
    public void Update_ConcurrentIncrement_NeverThrowsAndNeverLosesAWrite()
    {
        var svc = NewService();
        svc.Update(c => c.AsioOutputDevice = 0); // use this int field as a counter

        const int threads = 8;
        const int perThread = 64;
        var exceptions = new ConcurrentBag<Exception>();

        Parallel.For(0, threads, _ =>
        {
            try
            {
                for (var i = 0; i < perThread; i++)
                    svc.Update(c => c.AsioOutputDevice++);
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        });

        Assert.Empty(exceptions);
        Assert.Equal(threads * perThread, svc.LoadAppConfig().AsioOutputDevice);
    }

    /// <summary>
    ///     The exact shape of finding #2: a collection member mutated while the
    ///     config serializes. Routed through <see cref="JsonConfigurationService.Update" />
    ///     the dictionary write and the serialize can never overlap, so no
    ///     "Collection was modified" throw and every entry is persisted.
    /// </summary>
    [Fact]
    public void Update_ConcurrentCollectionMutation_NeverThrowsAndPersistsEveryEntry()
    {
        var svc = NewService();
        svc.Update(c => c.ThemeColorOverrides.Clear());

        const int threads = 8;
        const int perThread = 40;
        var exceptions = new ConcurrentBag<Exception>();

        Parallel.For(0, threads, t =>
        {
            try
            {
                for (var i = 0; i < perThread; i++)
                {
                    var key = $"{t}-{i}";
                    svc.Update(c => c.ThemeColorOverrides[key] = "#FF00FF");
                }
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        });

        Assert.Empty(exceptions);
        Assert.Equal(threads * perThread, svc.LoadAppConfig().ThemeColorOverrides.Count);
    }

    [Fact]
    public void Logger_ReplayedOnlyOnce()
    {
        File.WriteAllText(Path.Combine(_tempDir, "appconfig.json"), "broken json");
        var svc = NewService();
        svc.LoadAppConfig();

        var logger = new FakeAppLogger();
        svc.Logger = logger;

        var countAfterFirstAttach = logger.Entries.Count(e => e.Level == "ERROR");

        // Reassigning the logger (same or different) must not duplicate the buffered entries.
        var secondLogger = new FakeAppLogger();
        svc.Logger = secondLogger;
        Assert.DoesNotContain(secondLogger.Entries, e => e.Level == "ERROR");
        Assert.Equal(countAfterFirstAttach, logger.Entries.Count(e => e.Level == "ERROR"));
    }

    // === NormalizeKeybinds migrations =========================================
    // v0.35 gave the string-edit mask the whole Ctrl+0..9 row, which collided
    // with the shipped TogglePreview3D default of Ctrl+3. A collision resolves
    // last-registration-wins with only a log line, so an unmigrated config
    // would silently lose one of the two.

    /// <summary>
    ///     The keybinds store had the shape the app config was fixed for in
    ///     v0.48.5: a bare <c>catch (Exception)</c> that returned DEFAULTS for a
    ///     read that merely failed right now, and a save that wrote whatever it
    ///     was handed. The settings page reads the binds back and saves them, so
    ///     one AV/cloud-sync hold turned into every factory chord written over
    ///     the user's binds.
    /// </summary>
    [Fact]
    public void LoadKeybinds_Unreadable_NeverWritesDefaultsOverIt_AndHealsWhenTheLockGoes()
    {
        var path = Path.Combine(_tempDir, "keybinds.json");
        WriteKeybinds("""{"TogglePreview3D":"F9"}""");
        var before = File.ReadAllBytes(path);

        var svc = NewService();

        // Same hold as the app-config case: a reader is denied while a RENAME
        // over the file is still permitted, so the writer really could clobber it.
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Write | FileShare.Delete))
        {
            var degraded = svc.LoadKeybinds();
            Assert.NotEqual(new KeyChord(Key.F9), degraded.TogglePreview3D); // the stand-in defaults
            svc.SaveKeybinds(degraded); // what the settings page would do next
        }

        Assert.Equal(before, File.ReadAllBytes(path));

        // Not cached while degraded: the next read goes back to the real file,
        // and the store is writable again.
        Assert.Equal(new KeyChord(Key.F9), svc.LoadKeybinds().TogglePreview3D);
        var edited = svc.LoadKeybinds();
        edited.ToggleClaps = new KeyChord(Key.K);
        svc.SaveKeybinds(edited);
        Assert.Equal(new KeyChord(Key.K), svc.LoadKeybinds().ToggleClaps);
    }

    [Fact]
    public void LoadKeybinds_Corrupt_IsQuarantinedAside_AndTheSessionStartsFresh()
    {
        var path = Path.Combine(_tempDir, "keybinds.json");
        const string corrupt = "{ this is not json ";
        File.WriteAllText(path, corrupt);

        var svc = NewService();
        var fresh = svc.LoadKeybinds();
        fresh.ToggleClaps = new KeyChord(Key.K);
        svc.SaveKeybinds(fresh);

        var quarantined = Directory.GetFiles(_tempDir, "keybinds.corrupt-*.json");
        Assert.Single(quarantined);
        Assert.Equal(corrupt, File.ReadAllText(quarantined[0]));
        Assert.Equal(new KeyChord(Key.K), svc.LoadKeybinds().ToggleClaps);
    }

    [Fact]
    public void LoadKeybinds_MovesTheOldCtrl3PreviewDefaultOffTheStringMaskRow()
    {
        WriteKeybinds("""{"TogglePreview3D":"Ctrl+D3"}""");

        var loaded = NewService().LoadKeybinds();

        Assert.Equal(new KeyChord(Key.P, KeyModifiers.Control), loaded.TogglePreview3D);
    }

    [Fact]
    public void LoadKeybinds_LeavesADeliberatelyReboundPreviewChordAlone()
    {
        WriteKeybinds("""{"TogglePreview3D":"F9"}""");

        var loaded = NewService().LoadKeybinds();

        Assert.Equal(new KeyChord(Key.F9), loaded.TogglePreview3D);
    }

    [Fact]
    public void LoadKeybinds_IgnoresTheRemovedToggleMidiKey()
    {
        // System.Text.Json skips unmapped members, so a config written before
        // the dead ToggleMidi binding was deleted still loads.
        WriteKeybinds("""{"ToggleMidi":"I","ToggleClaps":"K"}""");

        var loaded = NewService().LoadKeybinds();

        Assert.Equal(new KeyChord(Key.K), loaded.ToggleClaps);
    }

    // v0.47 EOF alignment pass. A saved keybinds.json PINS every chord, so
    // changing a default alone reaches nobody who has ever opened the app —
    // which is the whole point of these migrations and the reason this is
    // tested rather than assumed.

    [Theory]
    [InlineData("SetLoopStart", "Alt+OemOpenBrackets")]
    [InlineData("SetLoopEnd", "Alt+OemCloseBrackets")]
    [InlineData("SelectLike", "Ctrl+Shift+A")]
    [InlineData("StampSection", "Shift+M")]
    [InlineData("SnapAll", "Ctrl+Alt+R")]
    [InlineData("Toggle3DFingering", "Ctrl+F")]
    [InlineData("ToggleInspector", "Ctrl+I")]
    // v0.60 pass 3 — see NormalizeKeybinds for the EOF literal behind each.
    [InlineData("ZoomIn", "Ctrl+OemCloseBrackets")]
    [InlineData("ZoomOut", "Ctrl+OemOpenBrackets")]
    [InlineData("InsertBeatAtPlayhead", "Ctrl+B")]
    [InlineData("RemoveBeatAtPlayhead", "Ctrl+Shift+B")]
    [InlineData("HalfTime", "Ctrl+Shift+H")]
    [InlineData("ToggleChordPassOtherNotes", "Ctrl+Shift+X")]
    [InlineData("ToggleIgnore", "G")]
    [InlineData("ToggleIgnoreGroup", "Alt+G")]
    [InlineData("InsertVocalAtPlayhead", "Ctrl+Shift+I")]
    public void LoadKeybinds_MovesAStillDefaultChordOntoItsEofHome(string property, string oldChord)
    {
        WriteKeybinds($$"""{"{{property}}":"{{oldChord}}"}""");

        var loaded = NewService().LoadKeybinds();
        var moved = (KeyChord)typeof(KeybindConfig).GetProperty(property)!.GetValue(loaded)!;

        Assert.Equal((KeyChord)typeof(KeybindConfig).GetProperty(property)!.GetValue(new KeybindConfig())!, moved);
        Assert.NotEqual(KeyChordJsonConverter.Parse(oldChord), moved);
    }

    /// <summary>
    ///     The hole in "it is an equality test against the OLD DEFAULT": a user
    ///     who deliberately CHOOSES the old default is indistinguishable from
    ///     one who never moved off it — and the migrations re-ran on every
    ///     single load, so Ctrl+I for the Inspector (its own pre-v0.47 default,
    ///     and the obvious chord for the verb) was silently reset to F9 at every
    ///     launch, for ever. The stamp is the only thing that can tell the two
    ///     apart.
    /// </summary>
    [Theory]
    [InlineData("ToggleInspector", "Ctrl+I")]
    [InlineData("Toggle3DFingering", "Ctrl+F")]
    [InlineData("InsertBeatAtPlayhead", "Ctrl+B")]
    [InlineData("ToggleIgnore", "G")]
    public void LoadKeybinds_ARebindOntoARetiredDefault_SurvivesTheNextLaunch(string property, string chord)
    {
        var wanted = KeyChordJsonConverter.Parse(chord);

        // Launch 1: an up-to-date config (the migrations have run and stamped).
        var first = NewService().LoadKeybinds();
        Assert.Equal(KeybindConfig.CurrentMigrationVersion, first.MigrationVersion);

        // The user picks the retired chord for that verb, on purpose.
        typeof(KeybindConfig).GetProperty(property)!.SetValue(first, wanted);
        NewService().SaveKeybinds(first);

        // Launch 2, 3, 4: it is still theirs.
        for (var launch = 0; launch < 3; launch++)
        {
            var reloaded = NewService().LoadKeybinds();
            Assert.Equal(wanted, (KeyChord)typeof(KeybindConfig).GetProperty(property)!.GetValue(reloaded)!);
        }
    }

    /// <summary>The stamp must be written, or every launch re-migrates from
    /// scratch and the guarantee above lasts exactly one session.</summary>
    [Fact]
    public void LoadKeybinds_StampsAnUnmigratedConfigOnDisk()
    {
        WriteKeybinds("""{"ToggleInspector":"Ctrl+I"}""");

        var migrated = NewService().LoadKeybinds();
        Assert.Equal(new KeyChord(Key.F9), migrated.ToggleInspector); // wave 3 ran, once

        var onDisk = File.ReadAllText(Path.Combine(_tempDir, "keybinds.json"));
        Assert.Contains("\"MigrationVersion\": " + KeybindConfig.CurrentMigrationVersion, onDisk);
    }

    [Fact]
    public void LoadKeybinds_LeavesAChordTheUserChoseThemselvesAlone()
    {
        // The rule every one of these migrations follows: it is an equality
        // test against the OLD DEFAULT, never an unconditional write.
        WriteKeybinds("""{"ToggleInspector":"Ctrl+Shift+F4"}""");

        var loaded = NewService().LoadKeybinds();

        Assert.Equal(new KeyChord(Key.F4, KeyModifiers.Control | KeyModifiers.Shift), loaded.ToggleInspector);
    }

    /// <summary>The MIDI binds (#310) live in <c>keybinds.json</c> beside the chords:
    /// one Save writes both, one Load reads both, as readable strings.</summary>
    [Fact]
    public void MidiBindings_RoundTripThroughKeybindsJson_AsReadableStrings()
    {
        var svc = NewService();
        var config = new KeybindConfig();
        config.SetMidiTrigger(nameof(KeybindConfig.PlayPause), MidiTrigger.Note(9, 36));
        config.SetMidiTrigger(nameof(KeybindConfig.Undo), MidiTrigger.ControlChange(0, 20));
        svc.SaveKeybinds(config);

        var json = File.ReadAllText(Path.Combine(_tempDir, "keybinds.json"));
        Assert.Contains("\"note:10:36\"", json, StringComparison.Ordinal);
        Assert.Contains("\"cc:1:20\"", json, StringComparison.Ordinal);

        var loaded = NewService().LoadKeybinds();
        Assert.Equal(MidiTrigger.Note(9, 36), loaded.GetMidiTrigger(nameof(KeybindConfig.PlayPause)));
        Assert.Equal(MidiTrigger.ControlChange(0, 20), loaded.GetMidiTrigger(nameof(KeybindConfig.Undo)));
        Assert.Equal(nameof(KeybindConfig.Undo), loaded.FindMidiVerb(MidiTrigger.ControlChange(0, 20)));
    }

    /// <summary>A hand-edited file cannot smuggle in a bind no row shows or no press
    /// can run: an unknown verb, a digit family, garbage, a trigger taken twice.</summary>
    [Fact]
    public void LoadKeybinds_DropsMidiBindsNoRowCanShow_AndKeepsTheFirstOwnerOfATrigger()
    {
        WriteKeybinds("""
            {"MidiBindings":{
              "PlayPause":"note:10:36",
              "NoSuchVerb":"note:1:40",
              "BookmarkBase":"note:1:41",
              "Undo":"banana",
              "Redo":"cc:17:1",
              "SeekLeft":"note:10:36"
            }}
            """);

        var loaded = NewService().LoadKeybinds();

        Assert.Equal([nameof(KeybindConfig.PlayPause)], loaded.MidiBindings.Keys.ToArray());
        Assert.Equal(MidiTrigger.Note(9, 36), loaded.GetMidiTrigger(nameof(KeybindConfig.PlayPause)));
    }

    private void WriteKeybinds(string json) =>
        File.WriteAllText(Path.Combine(_tempDir, "keybinds.json"), json);
    // ---- coalesced, off-thread config writes (C.2) ------------------------------

    /// <summary>Holds the debounce window open until something flushes, so
    /// "has it written yet?" is a deterministic question rather than a race.
    /// The service's own default is 250 ms of wall clock, which nothing here
    /// waits on.</summary>
    private JsonConfigurationService HeldOpenService()
        => Track(new JsonConfigurationService(TimeSpan.Zero, (_, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct)));

    private string AppConfigFile => Path.Combine(_tempDir, "appconfig.json");

    /// <summary>
    ///     The C.2 contract in one test. The cache MUST be published
    ///     synchronously — several callers <c>LoadAppConfig()</c> straight after a
    ///     save and must see the values they just wrote — while the atomic write
    ///     + fsync (4.40 ms median, 13.48 ms max on the owner's config drive)
    ///     must NOT happen on the caller's thread. Twelve
    ///     <c>On&lt;X&gt;Changed</c> setters called this, one of them per zoom
    ///     step.
    /// </summary>
    [Fact]
    public void SaveAppConfig_PublishesTheCacheSynchronously_ButDefersTheFileWrite()
    {
        using var svc = HeldOpenService();

        svc.SaveAppConfig(new AppConfig { LastProjectPath = "typed-just-now.rscp" });

        // Visible in-process immediately …
        Assert.Equal("typed-just-now.rscp", svc.LoadAppConfig().LastProjectPath);
        // … and nothing has touched the disk on the caller's thread.
        Assert.False(File.Exists(AppConfigFile),
            "SaveAppConfig wrote the file inline; the write must be deferred to the background writer.");

        svc.Flush();

        Assert.True(File.Exists(AppConfigFile));
        Assert.Contains("typed-just-now.rscp", File.ReadAllText(AppConfigFile), StringComparison.Ordinal);
    }

    /// <summary>
    ///     A zoom spin is 10–20 <c>SaveAppConfig</c> calls a second. They must
    ///     collapse into ONE write, and it must carry the LAST value — a
    ///     coalescer that kept the first payload would silently revert the user's
    ///     setting on the next launch. Counted through the save TRACE, which the
    ///     writer emits exactly once per file write.
    /// </summary>
    [Fact]
    public void ABurstOfSaves_CostsOneWrite_CarryingTheLastValue()
    {
        using var svc = HeldOpenService();
        var logger = new FakeAppLogger();
        svc.Logger = logger;

        for (var i = 1; i <= 20; i++)
            svc.SaveAppConfig(new AppConfig { LastProjectPath = $"step-{i}.rscp" });

        svc.Flush();

        Assert.Equal(1, logger.Entries.Count(e =>
            e.Level == "TRACE" && e.Message.Contains("Saved app config", StringComparison.Ordinal)));
        Assert.Contains("step-20.rscp", File.ReadAllText(AppConfigFile), StringComparison.Ordinal);
    }

    /// <summary>Disposing the service (the clean exit path reaches this through
    /// the DI container) must not drop a pending write.</summary>
    [Fact]
    public void Dispose_DrainsAPendingSave()
    {
        var svc = HeldOpenService();
        svc.SaveAppConfig(new AppConfig { LastProjectPath = "changed-then-quit.rscp" });
        Assert.False(File.Exists(AppConfigFile));

        svc.Dispose();

        Assert.Contains("changed-then-quit.rscp", File.ReadAllText(AppConfigFile), StringComparison.Ordinal);
    }

    /// <summary>
    ///     A backup import rewrites appconfig.json BEHIND the service, and
    ///     <see cref="JsonConfigurationService.InvalidateCache" /> is what the
    ///     import calls. A payload armed before the import predates it, so it
    ///     must be abandoned — landing it later would write the pre-import
    ///     settings straight back over the imported file, which is the exact
    ///     failure the invalidation exists to prevent.
    /// </summary>
    [Fact]
    public void InvalidateCache_AbandonsAPendingSave_SoAnImportIsNotClobbered()
    {
        using var svc = HeldOpenService();
        svc.SaveAppConfig(new AppConfig { LastProjectPath = "pre-import.rscp" });

        // Stand in for the import: the file is replaced behind the service.
        File.WriteAllText(AppConfigFile, "{ \"LastProjectPath\": \"imported.rscp\" }");

        svc.InvalidateCache();
        svc.Flush();

        Assert.Contains("imported.rscp", File.ReadAllText(AppConfigFile), StringComparison.Ordinal);
        Assert.DoesNotContain("pre-import.rscp", File.ReadAllText(AppConfigFile), StringComparison.Ordinal);
        Assert.Equal("imported.rscp", svc.LoadAppConfig().LastProjectPath);
    }
    /// <summary>
    ///     0.57.4 made the CQT verifier the Live Play default. A config saved while it was
    ///     opt-in carries <c>false</c> as a DEFAULT, not a choice, and is switched on once;
    ///     turning it off afterwards sticks. Measured reason in <c>AppConfig.LivePlayUseCqtVerifier</c>.
    /// </summary>
    [Fact]
    public void LoadAppConfig_ACqtOptOutFromBeforeItWasTheDefault_IsSwitchedOnOnce()
    {
        File.WriteAllText(Path.Combine(_tempDir, "appconfig.json"), """{"LivePlayUseCqtVerifier":false}""");

        var loaded = NewService().LoadAppConfig();

        Assert.True(loaded.LivePlayUseCqtVerifier);
        Assert.True(loaded.LivePlayCqtDefaultMigrated);
    }

    [Fact]
    public void LoadAppConfig_ACqtOptOutAfterTheMigration_Sticks()
    {
        File.WriteAllText(Path.Combine(_tempDir, "appconfig.json"),
            """{"LivePlayUseCqtVerifier":false,"LivePlayCqtDefaultMigrated":true}""");

        Assert.False(NewService().LoadAppConfig().LivePlayUseCqtVerifier);
    }

    [Fact]
    public void AFreshConfig_JudgesWithTheCqtVerifier()
    {
        Assert.True(new AppConfig().LivePlayUseCqtVerifier);
        Assert.True(NewService().LoadAppConfig().LivePlayUseCqtVerifier);
    }
}
