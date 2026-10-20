using DotCharter.Tone.Audio.Dsp.Tones;

namespace DotCharter.Tests.Services.Audio.Tones;

/// <summary>
///     Which audio a generated tone is allowed to be built from.
///     <para>
///         This matters more than the gear mapping does. Every measurement the
///         tone rests on — squashed dynamics, a flat spectrum, where the energy
///         sits — is wrecked when drums and vocals share the signal. A cymbal IS
///         broadband noise, so a full mix reads as distortion and the generator
///         returns a confident high-gain amp for a clean song.
///     </para>
/// </summary>
public sealed class ToneAudioSourceTests
{
    private static Dictionary<string, string> Stems(params string[] names) =>
        names.ToDictionary(n => n, n => $@"C:\stems\{n}.wav", StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void ARealGuitarStemWinsOutright()
    {
        var pick = ToneAudioSource.Pick(Stems("drums", "bass", "other", "vocals", "guitar", "piano"), @"C:\song.ogg");

        Assert.Equal("guitar", pick!.Value.StemName);
        Assert.True(pick.Value.IsIsolatedGuitar);
    }

    /// <summary>
    ///     A 4-source separation has no guitar stem and folds everything that is
    ///     not drums/bass/vocals into <c>other</c> — mostly guitar in a rock mix,
    ///     and the best evidence available.
    /// </summary>
    [Fact]
    public void WithoutAGuitarStemTheOtherStemIsUsed()
    {
        var pick = ToneAudioSource.Pick(Stems("drums", "bass", "other", "vocals"), @"C:\song.ogg");

        Assert.Equal("other", pick!.Value.StemName);
        Assert.True(pick.Value.IsIsolatedGuitar);
    }

    /// <summary>The mix is a last resort and is FLAGGED, so the caller can present
    /// the result as weaker instead of treating it as equivalent evidence.</summary>
    [Fact]
    public void WithNoStemsAtAllTheMixIsUsedButMarkedNotIsolated()
    {
        var pick = ToneAudioSource.Pick(null, @"C:\song.ogg");

        Assert.Equal(@"C:\song.ogg", pick!.Value.Path);
        Assert.Equal("", pick.Value.StemName);
        Assert.False(pick.Value.IsIsolatedGuitar);
    }

    /// <summary>
    ///     The drums are never guitar evidence, whatever else is missing. This is
    ///     the one that would silently produce nonsense rather than an error.
    /// </summary>
    [Fact]
    public void DrumsBassAndVocalsAreNeverPicked()
    {
        var pick = ToneAudioSource.Pick(Stems("drums", "bass", "vocals"), songPath: null);

        Assert.Null(pick);
        Assert.All(ToneAudioSource.NeverStems, s => Assert.False(ToneAudioSource.IsGuitarish(s)));
    }

    [Fact]
    public void NothingToAnalyseReturnsNull() =>
        Assert.Null(ToneAudioSource.Pick(new Dictionary<string, string>(), null));

    [Fact]
    public void AnEmptyPathIsNotASource() =>
        Assert.Null(ToneAudioSource.Pick(new Dictionary<string, string> { ["guitar"] = "  " }, null));

    /// <summary><c>piano</c> is a 6-source sibling: if it exists then so does
    /// <c>guitar</c>, so it is deliberately not a fallback of its own.</summary>
    [Fact]
    public void PianoIsNotTreatedAsGuitar() => Assert.False(ToneAudioSource.IsGuitarish("piano"));

    [Theory]
    [InlineData("Guitar")]
    [InlineData(" guitar ")]
    [InlineData("OTHER")]
    public void StemNamesMatchLooselyEnoughToSurviveASeparator(string name) =>
        Assert.True(ToneAudioSource.IsGuitarish(name));
}
