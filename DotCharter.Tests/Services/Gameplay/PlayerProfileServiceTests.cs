using System.IO.Compression;
using DotCharter.Services.Gameplay.Profiles;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Gameplay;

public sealed class PlayerProfileServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DotCharterTests", Guid.NewGuid().ToString("N"));

    private string StorePath => Path.Combine(_dir, "player-profiles.json");

    private PlayerProfileService NewService(string defaultName = "Player 1")
        => new(FakeLocalization.English(), logger: null, filePath: StorePath, defaultProfileName: defaultName);

    [Fact]
    public void FirstRun_CreatesActiveDefaultProfile()
    {
        var service = NewService(defaultName: "Standard");

        // No custom name: the default profile renders the localized "Player 1" at display time.
        var active = service.ActiveProfile;
        Assert.Equal("", active.Name);
        Assert.False(active.HasCustomName);
        Assert.True(active.IsDefault);
        Assert.Single(service.Profiles);
        Assert.True(File.Exists(StorePath));
    }

    /// <summary>
    ///     A backup import rewrites player-profiles.json behind the service; the
    ///     permanent in-memory registry would otherwise keep serving (and re-saving)
    ///     the pre-import profiles.
    /// </summary>
    [Fact]
    public void InvalidateCache_MakesTheNextReadSeeAFileWrittenBehindTheService()
    {
        var service = NewService();
        Assert.Single(service.Profiles); // warms + persists the registry

        // Stand in for the import: another writer replaces the file on disk.
        NewService().Create("Imported");

        Assert.Single(service.Profiles); // still the stale in-memory registry

        service.InvalidateCache();

        Assert.Equal(2, service.Profiles.Count);
        Assert.Contains(service.Profiles, p => p.Name == "Imported");
    }

    [Fact]
    public void Create_AddsProfile_AndPersistsAcrossInstances()
    {
        var created = NewService().Create("Alex");

        var reloaded = NewService();
        Assert.Equal(2, reloaded.Profiles.Count);
        var loaded = Assert.Single(reloaded.Profiles, p => p.Id == created.Id);
        Assert.Equal("Alex", loaded.Name);
        Assert.False(loaded.IsDefault);
        Assert.False(string.IsNullOrWhiteSpace(loaded.AccentColor));
    }

    [Fact]
    public void Create_BlankName_Throws() => Assert.Throws<ArgumentException>(() => NewService().Create("   "));

    [Fact]
    public void SwitchTo_ChangesActive_RaisesEvent_AndPersists()
    {
        var service = NewService();
        var second = service.Create("Zwei");

        PlayerProfile? switched = null;
        service.ActiveProfileChanged += p => switched = p;

        Assert.True(service.SwitchTo(second.Id));
        Assert.Equal(second.Id, service.ActiveProfile.Id);
        Assert.Equal(second.Id, switched?.Id);

        Assert.Equal(second.Id, NewService().ActiveProfile.Id);
    }

    [Fact]
    public void SwitchTo_SameProfile_DoesNotRaiseEvent()
    {
        var service = NewService();
        var raised = false;
        service.ActiveProfileChanged += _ => raised = true;

        Assert.True(service.SwitchTo(service.ActiveProfile.Id));
        Assert.False(raised);
    }

    [Fact]
    public void SwitchTo_UnknownId_ReturnsFalse() => Assert.False(NewService().SwitchTo(Guid.NewGuid()));

    [Fact]
    public void Delete_LastProfile_IsRefused()
    {
        var service = NewService();
        Assert.False(service.Delete(service.ActiveProfile.Id));
        Assert.Single(service.Profiles);
    }

    [Fact]
    public void Delete_ActiveProfile_SwitchesToRemaining()
    {
        var service = NewService();
        var second = service.Create("Zwei");
        service.SwitchTo(second.Id);

        PlayerProfile? switched = null;
        service.ActiveProfileChanged += p => switched = p;

        Assert.True(service.Delete(second.Id));
        Assert.Single(service.Profiles);
        Assert.NotEqual(second.Id, service.ActiveProfile.Id);
        Assert.Equal(service.ActiveProfile.Id, switched?.Id);
    }

    [Fact]
    public void Delete_RemovesDataDirectory()
    {
        var service = NewService();
        var second = service.Create("Zwei");
        var dataDir = service.GetDataDirectory(second.Id);
        File.WriteAllText(Path.Combine(dataDir, "scores.json"), "{}");

        Assert.True(service.Delete(second.Id));
        Assert.False(Directory.Exists(dataDir));
    }

    [Fact]
    public void Rename_UpdatesAndPersists()
    {
        var service = NewService();
        service.Rename(service.ActiveProfile.Id, "  Neu  ");
        Assert.Equal("Neu", NewService().ActiveProfile.Name);
    }

    [Fact]
    public void GetDataDirectory_IsPerProfile_UnderProfilesFolder()
    {
        var service = NewService();
        var dir = service.GetDataDirectory(service.ActiveProfile.Id);

        Assert.True(Directory.Exists(dir));
        Assert.StartsWith(Path.Combine(_dir, "profiles"), dir);
    }

    [Fact]
    public void CorruptStore_StartsFreshWithDefault()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(StorePath, "{ not json !!!");

        var service = NewService();
        Assert.Single(service.Profiles);
        Assert.True(service.ActiveProfile.IsDefault);
    }

    // --- The registry is the only map from profiles/<id>/ back to a person ----

    /// <summary>
    ///     A registry whose CONTENT is not a registry is moved aside, not
    ///     overwritten. The ids inside it are the only thing that can match the
    ///     <c>profiles/&lt;id&gt;/</c> folders still on disk back to their owners,
    ///     so destroying the bytes turns every profile's scores, goals and history
    ///     into unidentifiable folders.
    /// </summary>
    [Fact]
    public void CorruptStore_IsQuarantinedInsteadOfOverwritten()
    {
        Directory.CreateDirectory(_dir);
        const string original = "{ \"Profiles\": [ not json !!!";
        File.WriteAllText(StorePath, original);

        var service = NewService();
        Assert.Single(service.Profiles); // fresh registry, and it IS written

        var quarantined = Directory.GetFiles(_dir, "player-profiles.corrupt-*.json");
        Assert.Single(quarantined);
        Assert.Equal(original, File.ReadAllText(quarantined[0]));
        Assert.False(service.IsDegraded); // the bytes are safe, so writing is allowed again
    }

    /// <summary>
    ///     The bug this pins: a registry that exists and is presumed INTACT but
    ///     cannot be opened right now (antivirus scan, cloud sync, backup tool)
    ///     used to be replaced — on the first property read — by one fresh
    ///     "Player 1" with a brand-new id. Nothing was deleted and everything was
    ///     orphaned: per-profile data lives under <c>profiles/&lt;id&gt;/</c> and
    ///     the file that names those ids was gone.
    /// </summary>
    [Fact]
    public void UnreadableStore_IsNeverWrittenOver_AndHealsWhenTheLockLifts()
    {
        var real = NewService().Create("Echt");

        var service = NewService();
        using (new FileStream(StorePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            // Every read path that used to mint (and persist) a fresh registry.
            Assert.True(service.IsDegraded);
            Assert.Equal(PlayerProfileService.DegradedProfileId, service.ActiveProfile.Id);
            Assert.Single(service.Profiles);
            // Stable across calls: a fresh id each time would scatter the degraded
            // session's practice/goal writes over a new folder per call.
            Assert.Equal(PlayerProfileService.DegradedProfileId, service.ActiveProfile.Id);
            Assert.StartsWith(Path.Combine(_dir, "profiles",
                PlayerProfileService.DegradedProfileId.ToString("N")),
                service.GetDataDirectory(service.ActiveProfile.Id));

            // Mutations are accepted-and-dropped, never persisted.
            service.Create("Verloren");
            service.Rename(real.Id, "Umbenannt");
            Assert.False(service.SwitchTo(real.Id));
            Assert.False(service.Delete(real.Id));
        }

        // The lock lifted: the SAME instance recovers, because the failed read was
        // deliberately NOT cached. Without that, the stand-in stayed authoritative
        // for the rest of the session…
        Assert.False(service.IsDegraded);
        Assert.Equal(2, service.Profiles.Count);
        Assert.Contains(service.Profiles, p => p.Id == real.Id && p.Name == "Echt");
        Assert.DoesNotContain(service.Profiles, p => p.Name == "Verloren");

        // …and the next ordinary write flushed it over the real registry, which is
        // how every profile's data folder became unnameable.
        service.Create("Danach");
        var onDisk = NewService().Profiles;
        Assert.Equal(3, onDisk.Count);
        Assert.Contains(onDisk, p => p.Id == real.Id && p.Name == "Echt");
    }

    /// <summary>An import while degraded would extract a whole data folder that
    /// no registry can ever name — refused before anything is written.</summary>
    [Fact]
    public void UnreadableStore_RefusesAnImportInsteadOfOrphaningItsDataFolder()
    {
        var service = NewService();
        var original = service.Create("Tour");
        var zipPath = Path.Combine(_dir, "tour.dcprofile");
        Assert.True(service.ExportProfile(original.Id, zipPath));

        var before = ProfileDataFolders();
        service.InvalidateCache();

        using (new FileStream(StorePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Null(service.ImportProfile(zipPath));
        }

        Assert.Equal(before, ProfileDataFolders());
    }

    private int ProfileDataFolders()
    {
        var root = Path.Combine(_dir, "profiles");
        return Directory.Exists(root) ? Directory.GetDirectories(root).Length : 0;
    }

    /// <summary>
    ///     The degraded stand-in must not be <c>IsDefault</c>: the practice store's
    ///     one-shot 0.10 migration MOVES the pre-profile score file into the
    ///     DEFAULT profile's folder the first time it resolves, and a session that
    ///     cannot even read the registry must not be what consumes it.
    /// </summary>
    [Fact]
    public void TheDegradedStandInIsNotTheDefaultProfile()
    {
        var service = NewService();
        Assert.Single(service.Profiles); // seeds the file
        service.InvalidateCache();

        using var _ = new FileStream(StorePath, FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.False(service.ActiveProfile.IsDefault);
    }

    [Fact]
    public void UnknownActiveId_FallsBackToFirstProfile()
    {
        var service = NewService();
        var defaultId = service.ActiveProfile.Id;

        var json = File.ReadAllText(StorePath);
        File.WriteAllText(StorePath, json.Replace(
            $"\"ActiveProfileId\": \"{defaultId}\"",
            $"\"ActiveProfileId\": \"{Guid.NewGuid()}\""));

        Assert.Equal(defaultId, NewService().ActiveProfile.Id);
    }

    // --- Avatar ------------------------------------------------------------

    /// <summary>Tiny fake image OUTSIDE the profile data dirs (like a user pick from Downloads).</summary>
    private string MakeSourceImage(string name = "source.png")
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, [0x89, 0x50, 0x4E, 0x47, 0x01, 0x02, 0x03]);
        return path;
    }

    [Fact]
    public void SetAvatar_CopiesIntoDataDir_AndPersists()
    {
        var service = NewService();
        var id = service.ActiveProfile.Id;

        Assert.True(service.SetAvatar(id, MakeSourceImage()));

        var avatarPath = Path.Combine(service.GetDataDirectory(id), "avatar.png");
        Assert.True(File.Exists(avatarPath));
        Assert.Equal(avatarPath, service.GetAvatarPath(id));

        var reloaded = NewService();
        Assert.Equal("avatar.png", reloaded.ActiveProfile.AvatarFileName);
        Assert.Equal(avatarPath, reloaded.GetAvatarPath(id));
    }

    [Fact]
    public void SetAvatar_NewExtension_ReplacesOldAvatarFile()
    {
        var service = NewService();
        var id = service.ActiveProfile.Id;
        service.SetAvatar(id, MakeSourceImage("a.png"));
        service.SetAvatar(id, MakeSourceImage("b.jpg"));

        var dataDir = service.GetDataDirectory(id);
        Assert.False(File.Exists(Path.Combine(dataDir, "avatar.png")));
        Assert.True(File.Exists(Path.Combine(dataDir, "avatar.jpg")));
        Assert.Equal("avatar.jpg", NewService().ActiveProfile.AvatarFileName);
    }

    [Fact]
    public void SetAvatar_UnknownProfileOrMissingFile_ReturnsFalse()
    {
        var service = NewService();
        Assert.False(service.SetAvatar(Guid.NewGuid(), MakeSourceImage()));
        Assert.False(service.SetAvatar(service.ActiveProfile.Id, Path.Combine(_dir, "nope.png")));
        Assert.Null(service.GetAvatarPath(service.ActiveProfile.Id));
    }

    // --- Export / import ---------------------------------------------------

    [Fact]
    public void ExportImport_RoundTrip_RestoresDataAndAvatar_WithFreshId()
    {
        var service = NewService();
        var original = service.Create("Tour");
        service.SetAvatar(original.Id, MakeSourceImage());
        File.WriteAllText(Path.Combine(service.GetDataDirectory(original.Id), "scores.json"), "{\"best\":97}");

        var zipPath = Path.Combine(_dir, "tour.dcprofile");
        Assert.True(service.ExportProfile(original.Id, zipPath));
        Assert.True(File.Exists(zipPath));

        var imported = service.ImportProfile(zipPath);
        Assert.NotNull(imported);
        Assert.NotEqual(original.Id, imported.Id);
        Assert.Equal("Tour (2)", imported.Name); // original name is taken
        Assert.Equal("avatar.png", imported.AvatarFileName);
        Assert.False(imported.IsDefault);
        Assert.Equal(3, service.Profiles.Count);

        var importedDir = service.GetDataDirectory(imported.Id);
        Assert.Equal("{\"best\":97}", File.ReadAllText(Path.Combine(importedDir, "scores.json")));
        Assert.Equal(Path.Combine(importedDir, "avatar.png"), service.GetAvatarPath(imported.Id));
    }

    [Fact]
    public void ImportProfile_NameNotTaken_KeepsOriginalName()
    {
        var service = NewService();
        var original = service.Create("Tour");
        var zipPath = Path.Combine(_dir, "tour.dcprofile");
        Assert.True(service.ExportProfile(original.Id, zipPath));

        service.Rename(original.Id, "Renamed");
        var imported = service.ImportProfile(zipPath);

        Assert.NotNull(imported);
        Assert.Equal("Tour", imported.Name);
    }

    [Fact]
    public void ImportProfile_RepeatedCollisions_CountUpSuffix()
    {
        var service = NewService();
        var original = service.Create("Tour");
        var zipPath = Path.Combine(_dir, "tour.dcprofile");
        Assert.True(service.ExportProfile(original.Id, zipPath));

        Assert.Equal("Tour (2)", service.ImportProfile(zipPath)!.Name);
        Assert.Equal("Tour (3)", service.ImportProfile(zipPath)!.Name);
    }

    /// <summary>
    ///     A roamed bundle arrives from an unauthenticated LAN peer and
    ///     <c>MpProfileRoaming.MaxBundleBytes</c> bounds only the COMPRESSED
    ///     2 MB — which is exactly the ratio a zip bomb exploits. The import
    ///     loop had a correct zip-slip guard and no expansion ceiling at all.
    /// </summary>
    [Fact]
    public void ImportProfile_RefusesABundleThatExpandsPastTheCap()
    {
        var service = NewService();
        var original = service.Create("Bomb");
        var exported = Path.Combine(_dir, "source.dcprofile");
        Assert.True(service.ExportProfile(original.Id, exported));

        byte[] profileJson;
        using (var source = ZipFile.OpenRead(exported))
        using (var entry = source.GetEntry("profile.json")!.Open())
        using (var buffer = new MemoryStream())
        {
            entry.CopyTo(buffer);
            profileJson = buffer.ToArray();
        }

        var bombPath = Path.Combine(_dir, "bomb.dcprofile");
        using (var file = new FileStream(bombPath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
        {
            using (var manifest = archive.CreateEntry("profile.json").Open())
                manifest.Write(profileJson);

            using var bomb = archive.CreateEntry("data/bomb.bin", CompressionLevel.Optimal).Open();
            var chunk = new byte[1024 * 1024]; // zeros — deflate collapses them
            for (long written = 0;
                 written < PlayerProfileService.MaxImportExpansionBytes + chunk.Length;
                 written += chunk.Length)
            {
                bomb.Write(chunk, 0, chunk.Length);
            }
        }

        // Small enough to travel over the LAN roaming path.
        Assert.True(new FileInfo(bombPath).Length < 2_000_000);

        Assert.Null(service.ImportProfile(bombPath));
        Assert.Equal(2, service.Profiles.Count); // the default plus "Bomb" — nothing was registered
        Assert.DoesNotContain(Directory.EnumerateFiles(Path.Combine(_dir, "profiles"), "bomb.bin",
            SearchOption.AllDirectories), _ => true); // the half-written data folder is gone
    }

    [Fact]
    public void ImportProfile_NotAProfileArchive_ReturnsNull()
    {
        var service = NewService();
        Directory.CreateDirectory(_dir);

        var garbage = Path.Combine(_dir, "garbage.dcprofile");
        File.WriteAllText(garbage, "not a zip at all");
        Assert.Null(service.ImportProfile(garbage));
        Assert.Null(service.ImportProfile(Path.Combine(_dir, "missing.dcprofile")));
        Assert.Single(service.Profiles);
    }

    [Fact]
    public void ExportProfile_UnknownProfile_ReturnsFalse() => Assert.False(NewService().ExportProfile(Guid.NewGuid(), Path.Combine(_dir, "x.dcprofile")));

    public void Dispose() => BestEffortDelete.Directory(_dir);
}
