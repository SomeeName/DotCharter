using DotCharter.Services.Audio.Lyrics;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Audio.Lyrics;

/// <summary>
///     Pins the rules that decide which audio lyric auto-sync listens to.
///     <para>
///         Worth testing precisely because every failure here is SILENT: pick the
///         wrong file and alignment still succeeds, still returns every syllable,
///         and simply places them badly — there is no exception and no log line
///         that says "these timings are nonsense".
///     </para>
/// </summary>
public sealed class VocalStemLocatorTests
{
    [Theory]
    [InlineData("vocals")]
    [InlineData("vocal")]
    [InlineData("VOCALS")]
    [InlineData("Vocals")]
    [InlineData("  vocals  ")]
    [InlineData("song_vocals")]
    [InlineData("htdemucs-vocals")]
    public void TheVoiceStem_IsRecognised(string bareName) =>
        Assert.True(VocalStemLocator.IsVocalStemName(bareName));

    /// <summary>
    ///     The complement stem is conventionally called <c>no_vocals</c>, so it
    ///     CONTAINS the substring "vocal" — a loose match would hand the aligner
    ///     the one stem with the singing removed.
    /// </summary>
    [Theory]
    [InlineData("no_vocals")]
    [InlineData("no-vocals")]
    [InlineData("novocals")]
    [InlineData("NO_VOCALS")]
    [InlineData("instrumental")]
    [InlineData("song_instrumental")]
    public void TheInstrumentalComplement_IsNeverMistakenForTheVoice(string bareName) =>
        Assert.False(VocalStemLocator.IsVocalStemName(bareName));

    [Theory]
    [InlineData("drums")]
    [InlineData("bass")]
    [InlineData("other")]
    [InlineData("guitar")]
    [InlineData("piano")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void TheOtherStems_AreNotTheVoice(string? bareName) =>
        Assert.False(VocalStemLocator.IsVocalStemName(bareName));

    [Fact]
    public void AmongAFullStemSet_OnlyTheVoiceIsPicked()
    {
        var picked = VocalStemLocator.PickVocalStem(
        [
            @"C:\song\stems\drums.wav",
            @"C:\song\stems\bass.wav",
            @"C:\song\stems\other.wav",
            @"C:\song\stems\vocals.wav",
            @"C:\song\stems\guitar.wav",
            @"C:\song\stems\piano.wav"
        ]);

        Assert.Equal(@"C:\song\stems\vocals.wav", picked);
    }

    /// <summary>
    ///     The A/B that justified this feature was measured on lossless stems, so
    ///     when both are present the lossless one is what gets used.
    /// </summary>
    [Fact]
    public void WhenBothAreCached_LosslessBeatsCompressed()
    {
        var picked = VocalStemLocator.PickVocalStem(
            [@"C:\song\stems\vocals.opus", @"C:\song\stems\vocals.wav"]);

        Assert.Equal(@"C:\song\stems\vocals.wav", picked);
    }

    /// <summary>
    ///     The durable stem cache stores Opus, so refusing it would mean the stem
    ///     path only ever worked in the session that ran the separation.
    /// </summary>
    [Fact]
    public void AnOpusOnlyCache_IsStillUsable() =>
        Assert.Equal(@"C:\song\stems\vocals.opus",
            VocalStemLocator.PickVocalStem([@"C:\song\stems\vocals.opus"]));

    [Fact]
    public void ANonAudioFile_IsIgnored() =>
        Assert.Null(VocalStemLocator.PickVocalStem(
            [@"C:\song\stems\vocals.json", @"C:\song\stems\vocals.txt"]));

    [Fact]
    public void NoStemsAtAll_MeansTheMix() =>
        Assert.Null(VocalStemLocator.PickVocalStem([]));

    [Fact]
    public void AMixWithNoStemsFolder_ResolvesToNothing()
    {
        var dir = NewTempDir();
        try
        {
            var mix = Path.Combine(dir, "song.ogg");
            File.WriteAllText(mix, "not really audio");

            Assert.Null(VocalStemLocator.TryLocate(mix));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public void AMixBesideASeparatedStemsFolder_ResolvesToTheVoice()
    {
        var dir = NewTempDir();
        try
        {
            var mix = Path.Combine(dir, "song.ogg");
            File.WriteAllText(mix, "not really audio");

            var stems = Path.Combine(dir, VocalStemLocator.StemsFolderName);
            Directory.CreateDirectory(stems);
            foreach (var name in new[] { "drums.wav", "bass.wav", "other.wav", "vocals.wav" })
                File.WriteAllText(Path.Combine(stems, name), "not really audio");

            Assert.Equal(Path.Combine(stems, "vocals.wav"), VocalStemLocator.TryLocate(mix));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>
    ///     A two-stem split leaves <c>no_vocals</c> beside <c>vocals</c>. Getting
    ///     this wrong is the silent failure this whole type exists to prevent, so
    ///     it is asserted end to end and not only on the name predicate.
    /// </summary>
    [Fact]
    public void ATwoStemSplit_DoesNotResolveToTheInstrumental()
    {
        var dir = NewTempDir();
        try
        {
            var mix = Path.Combine(dir, "song.wav");
            File.WriteAllText(mix, "not really audio");

            var stems = Path.Combine(dir, VocalStemLocator.StemsFolderName);
            Directory.CreateDirectory(stems);
            File.WriteAllText(Path.Combine(stems, "no_vocals.wav"), "not really audio");
            File.WriteAllText(Path.Combine(stems, "vocals.wav"), "not really audio");

            Assert.Equal(Path.Combine(stems, "vocals.wav"), VocalStemLocator.TryLocate(mix));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public void AnInstrumentalOnlyFolder_FallsBackToTheMix()
    {
        var dir = NewTempDir();
        try
        {
            var mix = Path.Combine(dir, "song.wav");
            File.WriteAllText(mix, "not really audio");

            var stems = Path.Combine(dir, VocalStemLocator.StemsFolderName);
            Directory.CreateDirectory(stems);
            File.WriteAllText(Path.Combine(stems, "no_vocals.wav"), "not really audio");

            Assert.Null(VocalStemLocator.TryLocate(mix));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ANonPath_IsNotAnError(string? path) => Assert.Null(VocalStemLocator.TryLocate(path));

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dc-vocal-stem-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Cleanup(string dir) => BestEffortDelete.Directory(dir);
}
