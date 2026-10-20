using DotCharter.Services.Gameplay.Setlists;

namespace DotCharter.Tests.Services.Gameplay;

/// <summary>
///     The Play Mode setlist store (#646): a round trip, and the three degraded
///     reads the services guide requires to stay apart — ABSENT starts fresh,
///     CORRUPT is moved aside, UNREADABLE is presumed intact and never written over.
/// </summary>
public sealed class PlaySetlistStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DotCharterTests", "setlists-" + Guid.NewGuid().ToString("N"));
    private readonly Guid _profile = Guid.NewGuid();

    private string ProfileDir => Path.Combine(_dir, _profile.ToString("N"));
    private string StorePath => Path.Combine(ProfileDir, PlaySetlistStore.StoreFileName);

    private PlaySetlistStore NewStore(Func<Guid, bool>? degraded = null) =>
        new(id => Path.Combine(_dir, id.ToString("N")), degraded);

    private static PlaySetlist Sample() => new()
    {
        Name = "Rock night",
        Entries =
        [
            new PlaySetlistEntry { SongKey = "lib:one", Arrangement = "Lead", SpeedPercent = 80, Title = "One", Artist = "Band" },
            new PlaySetlistEntry { SongKey = @"C:\charts\two\project.rscp", Arrangement = "Bass", Title = "Two" }
        ]
    };

    [Fact]
    public void ARoundTrip_KeepsEveryListAndEveryEntryInOrder()
    {
        var store = NewStore();
        Assert.Equal(PlaySetlistReadOutcome.Absent, store.Load(_profile).Outcome);
        var original = Sample();
        Assert.True(store.Save(_profile, [original, new PlaySetlist { Name = "Empty" }]));

        var read = NewStore().Load(_profile);

        Assert.Equal(PlaySetlistReadOutcome.Loaded, read.Outcome);
        Assert.Equal(2, read.Setlists.Count);
        var list = read.Setlists[0];
        Assert.Equal(original.Id, list.Id);
        Assert.Equal("Rock night", list.Name);
        Assert.Equal(["lib:one", @"C:\charts\two\project.rscp"], list.Entries.Select(e => e.SongKey));
        Assert.Equal(["Lead", "Bass"], list.Entries.Select(e => e.Arrangement));
        Assert.Equal([80, 100], list.Entries.Select(e => e.SpeedPercent));
        Assert.Equal(["One", "Two"], list.Entries.Select(e => e.Title));
        Assert.Equal("Band", list.Entries[0].Artist);
        Assert.Equal("Empty", read.Setlists[1].Name);
    }

    [Fact]
    public void Absent_StartsFreshAndMayBeSaved()
    {
        var store = NewStore();

        var read = store.Load(_profile);

        Assert.Equal(PlaySetlistReadOutcome.Absent, read.Outcome);
        Assert.Empty(read.Setlists);
        Assert.True(read.IsAuthoritative);
        Assert.True(store.Save(_profile, [Sample()]));
        Assert.True(File.Exists(StorePath));
    }

    [Fact]
    public void Corrupt_IsMovedAsideAndAFreshStoreMayBeSaved()
    {
        Directory.CreateDirectory(ProfileDir);
        File.WriteAllText(StorePath, "{ this is not json");
        var store = NewStore();

        var read = store.Load(_profile);

        Assert.Equal(PlaySetlistReadOutcome.Corrupt, read.Outcome);
        Assert.Empty(read.Setlists);
        var aside = Assert.Single(Directory.GetFiles(ProfileDir, "setlists.corrupt-*.json"));
        Assert.Equal("{ this is not json", File.ReadAllText(aside));
        Assert.False(File.Exists(StorePath));
        Assert.True(store.Save(_profile, [Sample()]));
        Assert.Equal(PlaySetlistReadOutcome.Loaded, NewStore().Load(_profile).Outcome);
    }

    [Fact]
    public void Unreadable_IsPresumedIntactAndEverySaveIsRefused()
    {
        var store = NewStore();
        store.Load(_profile);
        Assert.True(store.Save(_profile, [Sample()]));
        var before = File.ReadAllBytes(StorePath);

        PlaySetlistRead read;
        using (new FileStream(StorePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            read = store.Load(_profile);
            Assert.False(store.Save(_profile, []));
        }

        Assert.Equal(PlaySetlistReadOutcome.Unreadable, read.Outcome);
        Assert.False(read.IsAuthoritative);
        Assert.Empty(read.Setlists);
        Assert.False(store.Save(_profile, []), "a save after an unreadable read must wait for a good read");
        Assert.Equal(before, File.ReadAllBytes(StorePath));
        Assert.Empty(Directory.GetFiles(ProfileDir, "*.corrupt-*"));

        // The hold is gone: the next read heals the store and saving resumes.
        Assert.Equal(PlaySetlistReadOutcome.Loaded, store.Load(_profile).Outcome);
        Assert.True(store.Save(_profile, []));
    }

    [Fact]
    public void ASaveBeforeAnyRead_IsRefused()
    {
        Directory.CreateDirectory(ProfileDir);
        File.WriteAllText(StorePath, "{\"Setlists\":[]}");

        Assert.False(NewStore().Save(_profile, [Sample()]));
        Assert.Equal("{\"Setlists\":[]}", File.ReadAllText(StorePath));
    }

    [Fact]
    public void TheDegradedStandInProfile_IsNeverWritten()
    {
        var store = NewStore(degraded: id => id == _profile);
        store.Load(_profile);

        Assert.False(store.Save(_profile, [Sample()]));
        Assert.False(File.Exists(StorePath));
    }

    [Fact]
    public void AHandEditedFile_DropsNullListsAndKeylessEntries()
    {
        Directory.CreateDirectory(ProfileDir);
        File.WriteAllText(StorePath,
            """{"Setlists":[null,{"Name":"A","Entries":[null,{"SongKey":""},{"SongKey":"lib:x","SpeedPercent":5}]}]}""");

        var read = NewStore().Load(_profile);

        var list = Assert.Single(read.Setlists);
        var entry = Assert.Single(list.Entries);
        Assert.Equal("lib:x", entry.SongKey);
        Assert.Equal(PlaySetlistEntry.MinSpeedPercent, entry.EffectiveSpeedPercent);
        Assert.NotEqual(Guid.Empty, list.Id);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }
}
