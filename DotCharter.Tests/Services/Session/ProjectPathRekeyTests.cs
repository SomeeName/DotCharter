using DotCharter.Services.Audio.LivePlay;
using DotCharter.Services.Gameplay.Profiles;
using DotCharter.Services.Gameplay.Setlists;
using DotCharter.Services.Session;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Session;

/// <summary>
///     What the app keeps under an own chart's PROJECT PATH follows the project when its file is
///     renamed by the <c>.rscp</c> to <c>.dcproj</c> migration: Play Mode stats and best scores in
///     every player profile, setlist songs and the per-song settings in appconfig. Each move is
///     idempotent, merges instead of replacing, and leaves a store it cannot read as it found it.
/// </summary>
public sealed class ProjectPathRekeyTests : IDisposable
{
    private const string Lead = "Lead:Lead";

    private readonly string _dir = Directory.CreateTempSubdirectory("dotcharter-rekey-").FullName;
    private readonly PlayerProfileService _profiles;
    private readonly PracticeProfileService _practice;
    private readonly PlaySetlistStore _setlists;
    private readonly FakeConfigurationService _config = new();
    private readonly ProjectPathRekey _rekey;

    private readonly string _old = Path.Combine(Path.GetTempPath(), "rekey-songs", "Band - Song", "project.rscp");
    private readonly string _new = Path.Combine(Path.GetTempPath(), "rekey-songs", "Band - Song", "project.dcproj");

    public ProjectPathRekeyTests()
    {
        _profiles = new PlayerProfileService(FakeLocalization.English(), null,
            Path.Combine(_dir, "player-profiles.json"), "Tester");
        _practice = new PracticeProfileService(_profiles, null, Path.Combine(_dir, "legacy-practice.json"));
        _setlists = new PlaySetlistStore(_profiles);
        _rekey = new ProjectPathRekey(_practice, _setlists, _profiles, _config, new FakeAppLogger());
    }

    public void Dispose() => BestEffortDelete.Directory(_dir);

    private static LivePlaySessionResult Run(long score, double accuracy = 0.9) =>
        new(score, accuracy, 90, 10, 100, 40, 180, 100, 1.0);

    private string StoreFileOf(PlayerProfile profile) =>
        Path.Combine(_profiles.GetDataDirectory(profile.Id), PracticeProfileService.StoreFileName);

    [Fact]
    public void TheStatsAndBestScores_FollowTheProjectToItsNewPath()
    {
        _practice.Record(_old, Lead, Run(100_000), "Band - Song");
        _practice.Record(_old, Lead, Run(120_000, 0.95));

        var result = _rekey.Rekey(_old, _new);

        Assert.Equal(1, result.PracticeEntries);
        var moved = _practice.GetProfile(_new, Lead);
        Assert.Equal(120_000, moved.BestScore);
        Assert.Equal(0.95, moved.BestAccuracy, 3);
        Assert.Equal(2, moved.Sessions.Count);
        Assert.Equal("Band - Song", moved.DisplayName);
        Assert.Equal(0, _practice.GetProfile(_old, Lead).BestScore);
        Assert.DoesNotContain(_practice.GetAll().Keys, k => k.StartsWith(_old, StringComparison.Ordinal));
    }

    [Fact]
    public void EveryPlayerProfile_IsMoved_NotJustTheActiveOne()
    {
        var guest = _profiles.Create("Guest");
        _practice.Record(_old, Lead, Run(100_000));
        _profiles.SwitchTo(guest.Id);
        _practice.Record(_old, Lead, Run(70_000));
        _profiles.SwitchTo(_profiles.Profiles[0].Id);

        _rekey.Rekey(_old, _new);

        Assert.Equal(100_000, _practice.GetProfile(_new, Lead).BestScore);
        // The guest's store is on disk, not cached: read the file itself.
        var guestStore = PracticeProfileService.ReadStoreFile(StoreFileOf(guest));
        Assert.Equal(70_000, guestStore[$"{_new}|{Lead}"].BestScore);
        Assert.DoesNotContain(guestStore.Keys, k => k.StartsWith(_old, StringComparison.Ordinal));
    }

    [Fact]
    public void ASecondRekey_MovesNothing_AndWritesNothing()
    {
        _practice.Record(_old, Lead, Run(100_000));
        _rekey.Rekey(_old, _new);
        var file = StoreFileOf(_profiles.ActiveProfile);
        var before = File.ReadAllBytes(file);
        var stamp = File.GetLastWriteTimeUtc(file);

        var again = _rekey.Rekey(_old, _new);

        Assert.Equal(0, again.Total);
        Assert.Equal(before, File.ReadAllBytes(file));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(file));
        Assert.Equal(100_000, _practice.GetProfile(_new, Lead).BestScore);
    }

    [Fact]
    public void WhenBothKeysExist_TheyMerge_AndNothingIsDropped()
    {
        _practice.Record(_old, Lead, Run(100_000, 0.80), "Old name");
        _practice.Record(_old, Lead, Run(90_000));
        _practice.Record(_new, Lead, Run(150_000, 0.70));

        _rekey.Rekey(_old, _new);

        var merged = _practice.GetProfile(_new, Lead);
        Assert.Equal(150_000, merged.BestScore); // the larger of the two
        Assert.Equal(0.90, merged.BestAccuracy, 3); // 0.90 from the runs recorded under the old path
        Assert.Equal(3, merged.Sessions.Count); // all three runs
        Assert.True(merged.Sessions.Zip(merged.Sessions.Skip(1)).All(p => p.First.EndedUtc <= p.Second.EndedUtc));
        Assert.Equal("Old name", merged.DisplayName); // filled from the entry that had one
        Assert.DoesNotContain(_practice.GetAll().Keys, k => k.StartsWith(_old, StringComparison.Ordinal));
    }

    [Fact]
    public void EveryArrangementOfTheSong_Moves_AndOtherSongsStayPut()
    {
        var other = Path.Combine(Path.GetTempPath(), "rekey-songs", "Other", "project.rscp");
        _practice.Record(_old, Lead, Run(100_000));
        _practice.Record(_old, "Bass:Bass", Run(50_000));
        _practice.Record(other, Lead, Run(10_000));

        Assert.Equal(2, _rekey.Rekey(_old, _new).PracticeEntries);

        Assert.Equal(100_000, _practice.GetProfile(_new, Lead).BestScore);
        Assert.Equal(50_000, _practice.GetProfile(_new, "Bass:Bass").BestScore);
        Assert.Equal(10_000, _practice.GetProfile(other, Lead).BestScore);
    }

    [Fact]
    public void ALibrarySongKey_IsNeverTouched()
    {
        _practice.Record("lib:CallMe", Lead, Run(100_000));

        _rekey.Rekey(_old, _new);

        Assert.Equal(100_000, _practice.GetProfile("lib:CallMe", Lead).BestScore);
    }

    [Fact]
    public void AnotherProfilesStoreThatCannotBeParsed_IsLeftExactlyAsItWas()
    {
        var guest = _profiles.Create("Guest");
        var corrupt = StoreFileOf(guest);
        File.WriteAllText(corrupt, "{ this is not a practice store");
        var before = File.ReadAllBytes(corrupt);
        _practice.Record(_old, Lead, Run(100_000));

        _rekey.Rekey(_old, _new);

        Assert.Equal(before, File.ReadAllBytes(corrupt));
        Assert.Equal(100_000, _practice.GetProfile(_new, Lead).BestScore); // the readable one still moved
    }

    [Fact]
    public void TheSetlistSongs_AreRetargeted_InEveryProfile()
    {
        var guest = _profiles.Create("Guest");
        foreach (var profile in new[] { _profiles.ActiveProfile, guest })
        {
            _setlists.Load(profile.Id);
            Assert.True(_setlists.Save(profile.Id,
            [
                new PlaySetlist
                {
                    Name = "Practice",
                    Entries =
                    [
                        new PlaySetlistEntry { SongKey = _old, Arrangement = "Lead", SpeedPercent = 80, Title = "Song" },
                        new PlaySetlistEntry { SongKey = "lib:CallMe", Arrangement = "Lead" }
                    ]
                }
            ]));
        }

        var result = _rekey.Rekey(_old, _new);

        Assert.Equal(2, result.SetlistEntries);
        foreach (var profile in new[] { _profiles.ActiveProfile, guest })
        {
            var entries = Assert.Single(_setlists.Load(profile.Id).Setlists).Entries;
            Assert.Equal(_new, entries[0].SongKey);
            Assert.Equal(80, entries[0].SpeedPercent); // the rest of the entry is untouched
            Assert.Equal("lib:CallMe", entries[1].SongKey);
        }
    }

    [Fact]
    public void ThePerSongSettings_MoveInAppConfig_AndTheNewPathsOwnSettingWins()
    {
        _config.AppConfig.SongTonePreferences[_old] = new SongTonePreference { Mode = SongTonePreference.MyToneMode };
        _config.AppConfig.DropTunerSongs.Add(_old.ToUpperInvariant()); // the same file spelled another way
        var other = Path.Combine(Path.GetTempPath(), "rekey-songs", "Other", "project.rscp");
        _config.AppConfig.SongTonePreferences[other] = new SongTonePreference { Mode = SongTonePreference.InGameMode };

        var result = _rekey.Rekey(_old, _new);

        Assert.Equal(2, result.ConfigEntries);
        Assert.Equal(SongTonePreference.MyToneMode, _config.AppConfig.SongTonePreferences[_new].Mode);
        Assert.False(_config.AppConfig.SongTonePreferences.ContainsKey(_old));
        Assert.Equal(SongTonePreference.InGameMode, _config.AppConfig.SongTonePreferences[other].Mode);
        Assert.Equal([_new], _config.AppConfig.DropTunerSongs);

        // A setting already filed under the new path is not replaced by a stale one.
        _config.AppConfig.SongTonePreferences[_old] = new SongTonePreference { Mode = SongTonePreference.InGameMode };
        _config.AppConfig.SongTonePreferences[_new] = new SongTonePreference { Mode = SongTonePreference.MyToneMode };
        _rekey.Rekey(_old, _new);
        Assert.Equal(SongTonePreference.MyToneMode, _config.AppConfig.SongTonePreferences[_new].Mode);
        Assert.False(_config.AppConfig.SongTonePreferences.ContainsKey(_old));
    }

    [Fact]
    public void AProjectWithNoPerSongSettings_DoesNotRewriteAppConfig()
    {
        _rekey.Rekey(_old, _new);

        Assert.Empty(_config.Writes);
    }

    [Fact]
    public void ThePathsAreComparedTheWayTheFileSystemDoes()
    {
        Assert.True(ProjectStoreKeys.SamePath(_old, _old));
        Assert.True(ProjectStoreKeys.SamePath(_old, _old.Replace('\\', '/')));
        Assert.False(ProjectStoreKeys.SamePath(_old, _new));
        Assert.False(ProjectStoreKeys.SamePath("", ""));
        Assert.False(ProjectStoreKeys.SamePath(null, _old));
        if (OperatingSystem.IsWindows()) Assert.True(ProjectStoreKeys.SamePath(_old, _old.ToUpperInvariant()));
    }
}
