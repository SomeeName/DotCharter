using System.Text;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Services.Audio.Dsp.Tones;
using DotCharter.Services.Audio.LivePlay;
using DotCharter.Services.Audio.StemSeparation.Cache;
using DotCharter.Services.Gameplay.Setlists;
using DotCharter.Services.Library;
using DotCharter.Services.State.Checkpoints;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Library;

/// <summary>
///     #884: a library song's identity moved from <c>psarc:&lt;key&gt;</c> to
///     <c>lib:&lt;key&gt;</c>. Every store keyed on it is fed the data an EARLIER build
///     left on disk and must (1) still find it under the new identity, (2) lose nothing
///     — an own chart's entries and every field stay — (3) write the new key and drop
///     the old one only in, or after, the write that stores the new one, (4) let an
///     existing <c>lib:</c> entry win over its <c>psarc:</c> twin and (5) do nothing on
///     a second run.
/// </summary>
public sealed class LibrarySongIdentityMigrationTests : IDisposable
{
    private const string OwnChart = @"C:\Charts\My Song\project.rscp";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "dc-lib-identity-" + Guid.NewGuid().ToString("N"));

    public LibrarySongIdentityMigrationTests() => Directory.CreateDirectory(_root);

    public void Dispose() => BestEffortDelete.Directory(_root);

    // ---- the identity itself ------------------------------------------------

    [Fact]
    public void TheIdentity_IsWrittenAsLib_AndReadInBothSpellings()
    {
        Assert.Equal("lib:CallMe", LibrarySongIdentity.For("CallMe"));
        Assert.Equal("lib:CallMe", LibrarySongIdentity.Normalize("psarc:CallMe"));
        Assert.Equal("lib:CallMe|Lead:Lead", LibrarySongIdentity.Normalize("psarc:CallMe|Lead:Lead"));
        Assert.Equal(OwnChart, LibrarySongIdentity.Normalize(OwnChart));
        Assert.True(LibrarySongIdentity.IsLibrary("lib:CallMe"));
        Assert.True(LibrarySongIdentity.IsLibrary("psarc:CallMe"));
        Assert.False(LibrarySongIdentity.IsLibrary(OwnChart));
        Assert.True(LibrarySongIdentity.TryGetSongKey("psarc:CallMe", out var old));
        Assert.True(LibrarySongIdentity.TryGetSongKey("lib:CallMe", out var current));
        Assert.Equal(("CallMe", "CallMe"), (old, current));
    }

    [Fact]
    public void MigrateKeys_LibWins_AndASecondRunIsANoOp()
    {
        var entries = new Dictionary<string, string>
        {
            ["psarc:A|Lead:Lead"] = "old A",
            ["psarc:B|Lead:Lead"] = "old B",
            ["lib:B|Lead:Lead"] = "new B",
            [OwnChart + "|Lead:Lead"] = "own"
        };

        Assert.Equal(2, LibrarySongIdentity.MigrateKeys(entries));
        Assert.Equal(0, LibrarySongIdentity.MigrateKeys(entries));

        Assert.Equal(3, entries.Count);
        Assert.Equal("old A", entries["lib:A|Lead:Lead"]);
        Assert.Equal("new B", entries["lib:B|Lead:Lead"]);
        Assert.Equal("own", entries[OwnChart + "|Lead:Lead"]);
    }

    // ---- practice profiles (stats, bests, leaderboards) ---------------------

    /// <summary>A store as a pre-#884 build wrote it: two library songs, one own chart.</summary>
    private const string OldPracticeStore = """
        {
          "psarc:CallMe|Lead:Lead": {
            "DisplayName": "Blondie - Call Me",
            "BestScore": 184200,
            "BestAccuracy": 0.92,
            "BestStreak": 41,
            "FullComboAchieved": true,
            "Sessions": [
              { "EndedUtc": "2026-09-01T20:00:00Z", "Score": 150000, "Accuracy": 0.81 },
              { "EndedUtc": "2026-09-02T20:00:00Z", "Score": 184200, "Accuracy": 0.92 }
            ]
          },
          "psarc:Heart|Bass:Bass": {
            "DisplayName": "Heart - Barracuda",
            "BestScore": 9000,
            "Sessions": [ { "EndedUtc": "2026-09-03T20:00:00Z", "Score": 9000, "Accuracy": 0.5 } ]
          },
          "C:\\Charts\\My Song\\project.rscp|Lead:Lead": {
            "DisplayName": "My Song",
            "BestScore": 777,
            "Sessions": [ { "EndedUtc": "2026-09-04T20:00:00Z", "Score": 777, "Accuracy": 0.7 } ]
          }
        }
        """;

    [Fact]
    public void PracticeStore_OldKeysResolve_NothingIsLost_AndTheNextWriteRekeysTheFile()
    {
        var path = Path.Combine(_root, PracticeProfileService.StoreFileName);
        File.WriteAllText(path, OldPracticeStore);

        var store = new PracticeProfileService(filePath: path);
        var callMe = store.GetProfile("lib:CallMe", "Lead:Lead");
        Assert.Equal(184200, callMe.BestScore);
        Assert.True(callMe.FullComboAchieved);
        Assert.Equal(2, callMe.Sessions.Count);
        // A caller still holding the old spelling reaches the same entry.
        Assert.Same(callMe, store.GetProfile("psarc:CallMe", "Lead:Lead"));
        Assert.Equal(777, store.GetProfile(OwnChart, "Lead:Lead").BestScore);
        Assert.DoesNotContain(store.GetAll().Keys, k => k.StartsWith("psarc:", StringComparison.Ordinal));
        Assert.Equal(3, store.GetAll().Count);

        // Reading alone never rewrote the file: the old entries are still on disk.
        Assert.Equal(OldPracticeStore, File.ReadAllText(path));

        store.Record("lib:Heart", "Bass:Bass", new LivePlaySessionResult(9500, 0.6, 6, 4, 20, 5, 30, 100, 0.9));

        var onDisk = PracticeProfileService.ReadStoreFile(path);
        var json = File.ReadAllText(path);
        Assert.DoesNotContain("psarc:", json, StringComparison.Ordinal);
        Assert.Equal(["C:\\Charts\\My Song\\project.rscp|Lead:Lead", "lib:CallMe|Lead:Lead", "lib:Heart|Bass:Bass"],
            onDisk.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(2, onDisk["lib:CallMe|Lead:Lead"].Sessions.Count);
        Assert.Equal(2, onDisk["lib:Heart|Bass:Bass"].Sessions.Count);

        // Second run: a fresh service over the migrated file changes nothing.
        var again = new PracticeProfileService(filePath: path);
        Assert.Equal(3, again.GetAll().Count);
        Assert.Equal(json, File.ReadAllText(path));
    }

    [Fact]
    public void PracticeStore_ALibEntry_WinsOverItsOldTwin()
    {
        var path = Path.Combine(_root, PracticeProfileService.StoreFileName);
        File.WriteAllText(path, """
            {
              "psarc:CallMe|Lead:Lead": { "BestScore": 1, "Sessions": [ { "EndedUtc": "2026-09-01T20:00:00Z", "Score": 1 } ] },
              "lib:CallMe|Lead:Lead": { "BestScore": 2, "Sessions": [ { "EndedUtc": "2026-09-02T20:00:00Z", "Score": 2 } ] }
            }
            """);

        var store = new PracticeProfileService(filePath: path);

        Assert.Equal(2, store.GetProfile("lib:CallMe", "Lead:Lead").BestScore);
        Assert.Single(store.GetAll());
        // The cross-profile read (leaderboards) migrates the same way.
        Assert.Equal(2, PracticeProfileService.ReadStoreFile(path)["lib:CallMe|Lead:Lead"].BestScore);
    }

    // ---- song tone edits + chart-synth instrument overrides ------------------

    private static DcRigPreset Rig(float gainDb) => new([
        new DcRigModule("gain", new Dictionary<string, float> { ["gainDb"] = gainDb })
    ]);

    private static float GainOf(DcRigPreset rig) => rig.Modules.Single(m => m.ModuleTypeId == "gain").Params["gainDb"];

    /// <summary>Writes the file an earlier build wrote for <paramref name="legacyIdentity" />.</summary>
    private static string WriteOldToneFile(SongToneEditStore store, string dir, string legacyIdentity, float gainDb)
    {
        var path = store.PathFor(legacyIdentity);
        Directory.CreateDirectory(dir);
        File.WriteAllText(path, $$$"""
            {"version":1,"song":"{{{legacyIdentity}}}","tones":{
              "CallMe_dist":{"Modules":[{"ModuleTypeId":"gain","Params":{"gainDb":{{{gainDb}}} } }]} },
             "instruments":{"CallMe_dist":{"guitar":{"model":"StratSss","pickup":"Neck"} } } }
            """);
        return path;
    }

    [Fact]
    public void ToneEdits_AnOldFile_IsFoundUnderLib_ThenMovedToTheNewName()
    {
        var dir = Path.Combine(_root, "song-tones");
        var store = new SongToneEditStore(dir);
        var oldPath = WriteOldToneFile(store, dir, "psarc:CallMe", -6f);

        var edits = store.Read("lib:CallMe");

        Assert.Equal(-6f, GainOf(edits["CallMe_dist"]));
        Assert.False(File.Exists(oldPath));
        var newPath = store.PathFor("lib:CallMe");
        Assert.True(File.Exists(newPath));
        Assert.Contains("\"song\": \"lib:CallMe\"", File.ReadAllText(newPath), StringComparison.Ordinal);
        Assert.Equal(["CallMe_dist"], store.ReadInstruments("lib:CallMe").Keys);

        // Second run: nothing left to move, the same edits come back.
        var bytes = File.ReadAllBytes(newPath);
        Assert.Equal(-6f, GainOf(store.Read("lib:CallMe")["CallMe_dist"]));
        Assert.Equal(bytes, File.ReadAllBytes(newPath));
    }

    [Fact]
    public void ToneEdits_ASaveUnderLib_KeepsTheOldFilesEdits()
    {
        var dir = Path.Combine(_root, "song-tones");
        var store = new SongToneEditStore(dir);
        WriteOldToneFile(store, dir, "psarc:CallMe", -6f);

        Assert.True(store.Save("lib:CallMe", "CallMe_clean", "clean", Rig(-1f)).Ok);

        var edits = store.Read("lib:CallMe");
        Assert.Equal(-6f, GainOf(edits["CallMe_dist"]));
        Assert.Equal(-1f, GainOf(edits["CallMe_clean"]));
    }

    [Fact]
    public void ToneEdits_ALibFile_WinsOverItsOldTwin()
    {
        var dir = Path.Combine(_root, "song-tones");
        var store = new SongToneEditStore(dir);
        Assert.True(store.Save("lib:CallMe", "CallMe_dist", "dist", Rig(-2f)).Ok);
        var oldPath = WriteOldToneFile(store, dir, "psarc:CallMe", -6f);

        Assert.Equal(-2f, GainOf(store.Read("lib:CallMe")["CallMe_dist"]));
        Assert.False(File.Exists(oldPath));
    }

    [Fact]
    public void ToneEdits_AnUnreadableOldFile_IsNeverWrittenOver()
    {
        var dir = Path.Combine(_root, "song-tones");
        var store = new SongToneEditStore(dir);
        var oldPath = WriteOldToneFile(store, dir, "psarc:CallMe", -6f);

        using (new FileStream(oldPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(SongToneEditWriteOutcome.Failed, store.Save("lib:CallMe", "CallMe_clean", "clean", Rig(-1f)).Outcome);
            Assert.False(File.Exists(store.PathFor("lib:CallMe")));
        }

        Assert.Equal(-6f, GainOf(store.Read("lib:CallMe")["CallMe_dist"]));
    }

    // ---- setlists -------------------------------------------------------------

    [Fact]
    public void Setlists_AnOldEntry_IsReadAsLib_AndKeepsItsSettings()
    {
        var profile = Guid.NewGuid();
        File.WriteAllText(Path.Combine(_root, PlaySetlistStore.StoreFileName), """
            { "Setlists": [ { "Id": "6a1f0c3e-4b39-4f53-9df0-0b6f7b1f2a10", "Name": "Warm-up", "Entries": [
                { "SongKey": "psarc:CallMe", "Arrangement": "Lead:Lead", "SpeedPercent": 80, "Title": "Call Me", "Artist": "Blondie" },
                { "SongKey": "C:\\Charts\\My Song\\project.rscp", "Arrangement": "Lead:Lead" } ] } ] }
            """);
        var store = new PlaySetlistStore(_ => _root);

        var read = store.Load(profile);

        var entries = Assert.Single(read.Setlists).Entries;
        Assert.Equal(["lib:CallMe", OwnChart], entries.Select(e => e.SongKey));
        Assert.Equal(80, entries[0].SpeedPercent);
        Assert.Equal("Call Me", entries[0].Title);

        Assert.True(store.Save(profile, read.Setlists));
        Assert.DoesNotContain("psarc:", File.ReadAllText(Path.Combine(_root, PlaySetlistStore.StoreFileName)),
            StringComparison.Ordinal);
        Assert.Equal(["lib:CallMe", OwnChart], Assert.Single(store.Load(profile).Setlists).Entries.Select(e => e.SongKey));
    }

    // ---- checkpoints ----------------------------------------------------------

    private static ChartProject Project(string songName)
    {
        var project = new ChartProject { Bpm = 120, Chart = new ChartData { SongName = songName, Artist = "Blondie" } };
        project.Arrangements.Add(new Arrangement { Name = "Lead" });
        for (var i = 0; i < 4; i++)
            project.Beats.Add(new BeatMarker { Time = i * 500.0, Bpm = 120, IsMeasure = i == 0, BeatsInMeasure = 4, NoteDenominator = 4 });
        return project;
    }

    [Fact]
    public void Checkpoints_AnOldFolder_IsFoundUnderLib_AndRestores()
    {
        var root = Path.Combine(_root, "checkpoints");
        // What an earlier build did: the Checkpoints dialog keyed a library song on "psarc:".
        var created = new CheckpointService(root).Create("psarc:CallMe", "before the bridge", Project("Call Me"));
        var oldDir = Path.Combine(root, CheckpointService.ComputeKey("psarc:CallMe"));
        Assert.True(Directory.Exists(oldDir));

        var service = new CheckpointService(root);
        var identity = CheckpointIdentity.For(null, "psarc:CallMe");
        Assert.Equal("lib:CallMe", identity);

        var listed = Assert.Single(service.List(identity));
        Assert.Equal(created.Id, listed.Id);
        Assert.Equal("Call Me", service.Restore(identity, listed).Chart.SongName);
        Assert.False(Directory.Exists(oldDir));

        // Second run: still exactly one, nothing re-created under the old key.
        Assert.Single(new CheckpointService(root).List(identity));
        Assert.False(Directory.Exists(oldDir));
    }

    [Fact]
    public void Checkpoints_BothFolders_AreMerged_LibEntriesKept_ThenTheOldOneGoes()
    {
        var root = Path.Combine(_root, "checkpoints");
        var old = new CheckpointService(root).Create("psarc:CallMe", "old one", Project("Old"));
        var oldDir = Path.Combine(root, CheckpointService.ComputeKey("psarc:CallMe"));

        // A lib folder already exists (made by this build) — create it without touching the old one.
        var newDir = Path.Combine(root, CheckpointService.ComputeKey("lib:CallMe"));
        Directory.Move(oldDir, newDir + ".tmp");
        var fresh = new CheckpointService(root).Create("lib:CallMe", "new one", Project("New"));
        Directory.Move(newDir + ".tmp", oldDir);

        var listed = new CheckpointService(root).List("lib:CallMe");

        Assert.Equal(new[] { fresh.Id, old.Id }.Order(StringComparer.Ordinal),
            listed.Select(e => e.Id).Order(StringComparer.Ordinal));
        Assert.False(Directory.Exists(oldDir));
    }

    // ---- stem cache -------------------------------------------------------------

    [Fact]
    public void StemCache_BothSpellings_HashTheLibIdentity_AndTheLegacyFormIsTheOldName()
    {
        var signature = StemAudioSignature.Compute(new MemoryStream(Encoding.ASCII.GetBytes(new string('x', 4096))));

        var current = StemCacheKey.TryDerive(StemCacheIdentity.ForLibrary("lib:CallMe|Blondie|1.0"), signature)!;
        var fromOld = StemCacheKey.TryDerive(StemCacheIdentity.ForLibrary("psarc:CallMe|Blondie|1.0"), signature)!;
        var currentWem = StemCacheKey.TryDeriveForWem(StemCacheIdentity.ForLibrary("lib:CallMe"), signature)!;
        var fromOldWem = StemCacheKey.TryDeriveForWem(StemCacheIdentity.ForLibrary("psarc:CallMe"), signature)!;

        // #898: either spelling in, the lib: key out.
        Assert.Equal(current, fromOld);
        Assert.Equal(currentWem, fromOldWem);
        Assert.StartsWith("lib:CallMe|Blondie|1.0|b=", current.Canonical, StringComparison.Ordinal);

        // The legacy form is EXACTLY the key an earlier build hashed (psarc: basis), so
        // its archive is found to be re-keyed.
        var earlierBuild = new StemCacheIdentity { LogicalId = "psarc:CallMe|Blondie|1.0" };
        Assert.Equal(StemCacheKey.TryDerive(earlierBuild, signature), current.LegacyLibraryForm);
        Assert.Equal(StemCacheKey.TryDeriveForWem(earlierBuild with { LogicalId = "psarc:CallMe" }, signature),
            currentWem.LegacyLibraryForm);
        Assert.Null(StemCacheKey.TryDerive(StemCacheIdentity.ForAudioFile(OwnChart), signature)!.LegacyLibraryForm);
    }
}
