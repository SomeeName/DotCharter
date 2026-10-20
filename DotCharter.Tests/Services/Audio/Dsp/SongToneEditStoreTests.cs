using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Services.Audio.Dsp.Tones;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Audio.Dsp;

/// <summary>
///     A song-tone edit is DotCharter's data, one file per song: saved and read
///     back by song identity, kept apart per song, deleted with its last edit, and
///     handled by the three read outcomes — a corrupt file is moved aside, an
///     unreadable one is never written over.
/// </summary>
public sealed class SongToneEditStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dotcharter-song-tones-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => BestEffortDelete.Directory(_dir);

    private SongToneEditStore Store() => new(_dir);

    private static DcRigPreset Rig(float gainDb) => new([
        new DcRigModule("overdrive", new Dictionary<string, float> { ["drive"] = 0.25f }),
        new DcRigModule("gain", new Dictionary<string, float> { ["gainDb"] = gainDb })
    ]);

    [Fact]
    public void Save_ThenRead_ReturnsTheRig_UnderItsToneKey()
    {
        var store = Store();

        var result = store.Save("lib:CallMe", "CallMe_dist", "CallMe_dist", Rig(-6f));
        var edits = store.Read("lib:CallMe");

        Assert.Equal(SongToneEditWriteOutcome.Written, result.Outcome);
        var rig = Assert.Contains("callme_DIST", edits); // keys are case-insensitive
        Assert.Equal(["overdrive", "gain"], rig.Modules.Select(m => m.ModuleTypeId));
        Assert.Equal(-6f, rig.Modules[1].Params["gainDb"]);
    }

    [Fact]
    public void EditsAreKeptPerSong_AndSurviveANewStoreInstance()
    {
        Store().Save("lib:CallMe", "CallMe_dist", "dist", Rig(-6f));
        Store().Save("lib:CallMe", "CallMe_synth", "synth", Rig(-3f));
        Store().Save("lib:Heart", "Heart_lead", "lead", Rig(-1f));

        var callMe = Store().Read("lib:CallMe");

        Assert.Equal(["CallMe_dist", "CallMe_synth"], callMe.Keys.Order());
        Assert.Equal(["Heart_lead"], Store().Read("lib:Heart").Keys);
        Assert.Empty(Store().Read("lib:NotEdited"));
    }

    [Fact]
    public void Remove_DropsOneEdit_AndTheFileGoesWithTheLast()
    {
        var store = Store();
        store.Save("lib:CallMe", "CallMe_dist", "dist", Rig(-6f));
        store.Save("lib:CallMe", "CallMe_synth", "synth", Rig(-3f));

        Assert.Equal(SongToneEditWriteOutcome.Written, store.Remove("lib:CallMe", "CallMe_dist").Outcome);
        Assert.Equal(["CallMe_synth"], store.Read("lib:CallMe").Keys);

        store.Remove("lib:CallMe", "CallMe_synth");
        Assert.False(File.Exists(store.PathFor("lib:CallMe")));
        Assert.Equal(SongToneEditWriteOutcome.NothingToRemove, store.Remove("lib:CallMe", "CallMe_synth").Outcome);
    }

    [Fact]
    public void ACorruptFile_ReadsEmpty_AndIsMovedAsideBeforeASave()
    {
        var store = Store();
        var path = store.PathFor("lib:CallMe");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(path, "{ not json");

        Assert.Empty(store.Read("lib:CallMe"));
        var result = store.Save("lib:CallMe", "CallMe_dist", "dist", Rig(-6f));

        Assert.Equal(SongToneEditWriteOutcome.Written, result.Outcome);
        Assert.Single(store.Read("lib:CallMe"));
        var aside = Assert.Single(Directory.GetFiles(_dir, "*.corrupt-*.json"));
        Assert.Equal("{ not json", File.ReadAllText(aside)); // the player still has the bytes
    }

    [Fact]
    public void AnUnreadableFile_IsNeverWrittenOver()
    {
        var store = Store();
        store.Save("lib:CallMe", "CallMe_dist", "dist", Rig(-6f));
        var path = store.PathFor("lib:CallMe");
        var before = File.ReadAllBytes(path);

        SongToneEditWriteResult result;
        // An AV / sync-client hold that denies READERS but still permits a rename
        // over the file — so only the store's own refusal keeps it from writing.
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Write | FileShare.Delete))
            result = store.Save("lib:CallMe", "CallMe_synth", "synth", Rig(-3f));

        Assert.Equal(SongToneEditWriteOutcome.Failed, result.Outcome);
        Assert.NotNull(result.Detail);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void ATone_WithAnUnknownModule_IsSkipped_AndTheRestStay()
    {
        var store = Store();
        var path = store.PathFor("lib:CallMe");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(path, """
            {"version":1,"song":"lib:CallMe","tones":{
              "good":{"Modules":[{"ModuleTypeId":"gain","Params":{"gainDb":-2}}]},
              "bad":{"Modules":[{"ModuleTypeId":"no-such-module","Params":{}}]}}}
            """);

        var edits = store.Read("lib:CallMe");

        Assert.True(edits.ContainsKey("good"));
        Assert.False(edits.ContainsKey("bad"));
    }

    [Fact]
    public void IdentitiesThatSanitizeAlike_GetTheirOwnFiles()
    {
        var store = Store();

        Assert.NotEqual(store.PathFor("lib:A/B"), store.PathFor("lib:A?B"));
        Assert.StartsWith(Path.GetFullPath(_dir), Path.GetFullPath(store.PathFor("lib:../../escape")));
    }

    [Fact]
    public void NoSong_ReadsEmpty_AndCannotBeSaved()
    {
        var store = Store();

        Assert.Empty(store.Read(""));
        Assert.Equal(SongToneEditWriteOutcome.Failed, store.Save(" ", "k", "k", Rig(0f)).Outcome);
    }

    // ---- The chart synth's instrument override (#66) -------------------------------

    private const string Song = "lib:CallMe";

    private static readonly DotCharter.Services.Audio.ChartSynth.ChartSynthToneOverride NeckOverride =
        new(new DotCharter.Tone.Audio.ChartSynth.ChartSynthInstrument(
            DotCharter.Tone.Audio.ChartSynth.ChartSynthModel.StratSss,
            DotCharter.Tone.Audio.ChartSynth.ChartSynthPickup.Neck, false), null);

    [Fact]
    public void AnInstrumentOverride_RoundTrips_AndLivesBesideTheEdits_NotInsideThem()
    {
        var store = Store();
        store.Save(Song, "CallMe_dist", "dist", Rig(-6f));

        Assert.Equal(SongToneEditWriteOutcome.Written, store.SaveInstrument(Song, "CallMe_clean", NeckOverride).Outcome);

        var instruments = Store().ReadInstruments(Song);
        Assert.Equal(NeckOverride, Assert.Contains("callme_CLEAN", instruments));
        Assert.Null(instruments["CallMe_clean"].Bass);                  // a family left alone stays null
        Assert.Equal(["CallMe_dist"], Store().Read(Song).Keys);          // the edits are untouched…

        store.Save(Song, "CallMe_dist", "dist", Rig(-3f));
        Assert.Equal(NeckOverride, Store().ReadInstruments(Song)["CallMe_clean"]);   // …and an edit keeps the override

        // Resetting the song's last edit keeps the file while an override needs it.
        store.Remove(Song, "CallMe_dist");
        Assert.Empty(store.Read(Song));
        Assert.True(File.Exists(store.PathFor(Song)));
        Assert.Single(store.ReadInstruments(Song));

        // Dropping the override (the last thing in it) takes the file.
        Assert.Equal(SongToneEditWriteOutcome.Written, store.SaveInstrument(Song, "CallMe_clean", null).Outcome);
        Assert.False(File.Exists(store.PathFor(Song)));
        Assert.Equal(SongToneEditWriteOutcome.NothingToRemove, store.SaveInstrument(Song, "CallMe_clean", null).Outcome);
    }

    [Fact]
    public void AnOverride_ThatNamesNoInstrument_CostsThatFamily()
    {
        var store = Store();
        Directory.CreateDirectory(_dir);
        File.WriteAllText(store.PathFor(Song), """
            {"version":1,"song":"lib:CallMe","tones":{},"instruments":{
              "clean":{"guitar":{"model":"StratSss","pickup":"Neck"},"bass":{"model":"StratHss","pickup":"Bridge"}},
              "lead":{}}}
            """);

        var instruments = store.ReadInstruments(Song);

        Assert.Equal(NeckOverride, instruments["clean"]);   // a guitar model as the BASS is dropped
        Assert.Equal(DotCharter.Services.Audio.ChartSynth.ChartSynthToneOverride.Empty, instruments["lead"]);
    }

    [Fact]
    public void AnUnreadableFile_IsNeverWrittenOver_ByAnOverride()
    {
        var store = Store();
        store.Save(Song, "CallMe_dist", "dist", Rig(-6f));
        var path = store.PathFor(Song);
        var before = File.ReadAllBytes(path);

        SongToneEditWriteResult result;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Write | FileShare.Delete))
            result = store.SaveInstrument(Song, "CallMe_clean", NeckOverride);

        Assert.Equal(SongToneEditWriteOutcome.Failed, result.Outcome);
        Assert.Equal(before, File.ReadAllBytes(path));
    }
}
