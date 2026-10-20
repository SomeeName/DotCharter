using DotCharter.Services.Audio.ChartSynth;
using DotCharter.Tone.Audio.ChartSynth;
using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Audio.ChartSynth;

/// <summary>
///     A player's chart-synth instrument file (#66) follows the JSON-store rule
///     (config-and-json-stores.md): ABSENT is the defaults, a CORRUPT file reads as
///     the defaults and is moved aside before a save replaces it, an UNREADABLE one
///     is never written over, a bad family costs that family only, and nothing is
///     ever written for the registry's degraded stand-in profile.
/// </summary>
public sealed class ProfileChartSynthInstrumentStoreTests : IDisposable
{
    private static readonly ChartSynthInstrument TeleNeck = new(ChartSynthModel.Telecaster, ChartSynthPickup.Neck, false);
    private static readonly ChartSynthInstrument PBass = new(ChartSynthModel.PrecisionBass, ChartSynthPickup.Middle, false);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "dotcharter-synth-store-" + Guid.NewGuid().ToString("N"));
    private readonly Guid _id = Guid.NewGuid();

    public void Dispose() => BestEffortDelete.Directory(_root);

    private sealed class Scope(string root, Guid degraded) : IToneRigProfileScope
    {
        public Guid ActiveProfileId => Guid.Empty;
        public string GetDataDirectory(Guid id) => Directory.CreateDirectory(Path.Combine(root, id.ToString("N"))).FullName;
        public bool IsDegradedProfile(Guid id) => id == degraded;
    }

    private ProfileChartSynthInstrumentStore Store(Guid? degraded = null) => new(new Scope(_root, degraded ?? Guid.Empty));

    private string FileOf(Guid id) => Path.Combine(_root, id.ToString("N"), ProfileChartSynthInstrumentStore.FileName);

    [Fact]
    public void NoFile_IsTheDefaults()
    {
        var read = Store().Read(_id);
        Assert.Equal(ProfileInstrumentReadState.Absent, read.State);
        Assert.Equal(ChartSynthInstrumentSelection.DefaultGuitar, read.Guitar);
        Assert.Equal(ChartSynthInstrumentSelection.DefaultBass, read.Bass);
    }

    [Fact]
    public void Save_ThenRead_RoundTrips_ByName()
    {
        Assert.True(Store().Save(_id, TeleNeck, PBass, out var error), error);
        var read = Store().Read(_id);   // a new instance: the next start

        Assert.Equal(ProfileInstrumentReadState.Loaded, read.State);
        Assert.Equal(TeleNeck, read.Guitar);
        Assert.Equal(PBass, read.Bass);
        var json = File.ReadAllText(FileOf(_id));
        Assert.Contains("\"Telecaster\"", json, StringComparison.Ordinal);   // names, never ordinals
        Assert.Contains("\"PrecisionBass\"", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"guitar":{"model":"NoSuchGuitar","pickup":"Neck"},"bass":{"model":"PrecisionBass","pickup":"Middle"}}""")]
    [InlineData("""{"guitar":{"model":"JazzBass","pickup":"Neck"},"bass":{"model":"PrecisionBass","pickup":"Middle"}}""")]
    [InlineData("""{"guitar":{"model":"3","pickup":"0"},"bass":{"model":"PrecisionBass","pickup":"Middle"}}""")]
    [InlineData("""{"bass":{"model":"PrecisionBass","pickup":"Middle"}}""")]
    public void ABadFamily_CostsThatFamily_NeverTheFile(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FileOf(_id))!);
        File.WriteAllText(FileOf(_id), json);

        var read = Store().Read(_id);

        Assert.Equal(ProfileInstrumentReadState.Loaded, read.State);
        Assert.Equal(ChartSynthInstrumentSelection.DefaultGuitar, read.Guitar);
        Assert.Equal(PBass, read.Bass);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("null")]
    [InlineData("[1,2]")]
    public void ACorruptFile_ReadsAsTheDefaults_AndIsMovedAsideBeforeASave(string bytes)
    {
        var path = FileOf(_id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, bytes);

        var read = Store().Read(_id);
        Assert.Equal(ProfileInstrumentReadState.Corrupt, read.State);
        Assert.Equal(ChartSynthInstrumentSelection.DefaultGuitar, read.Guitar);

        Assert.True(Store().Save(_id, TeleNeck, PBass, out _));
        Assert.Equal(TeleNeck, Store().Read(_id).Guitar);
        var aside = Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.corrupt-*.json"));
        Assert.Equal(bytes, File.ReadAllText(aside));   // the player still has the bytes
    }

    [Fact]
    public void AnUnreadableFile_ReadsAsUnreadable_AndIsNeverWrittenOver()
    {
        Assert.True(Store().Save(_id, TeleNeck, PBass, out _));
        var path = FileOf(_id);
        var before = File.ReadAllBytes(path);

        ProfileInstrumentRead read;
        bool saved;
        string? error;
        // An AV / sync-client hold that denies READERS but still permits a rename
        // over the file — so only the store's own refusal keeps it from writing.
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Write | FileShare.Delete))
        {
            read = Store().Read(_id);
            saved = Store().Save(_id, ChartSynthInstrumentSelection.DefaultGuitar, ChartSynthInstrumentSelection.DefaultBass, out error);
        }

        Assert.Equal(ProfileInstrumentReadState.Unreadable, read.State);
        Assert.Equal(ChartSynthInstrumentSelection.DefaultGuitar, read.Guitar);   // a stand-in, not the file
        Assert.False(saved);
        Assert.NotNull(error);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(TeleNeck, Store().Read(_id).Guitar);
    }

    [Fact]
    public void TheDegradedStandInProfile_IsNeverWritten()
    {
        var store = Store(degraded: _id);
        Assert.False(store.Save(_id, TeleNeck, PBass, out var error));
        Assert.NotNull(error);
        Assert.False(File.Exists(FileOf(_id)));
        Assert.Equal(ProfileInstrumentReadState.Unreadable, store.Read(_id).State);
    }
}
