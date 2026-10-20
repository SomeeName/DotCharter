using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     The mixer's strips named themselves by the separator's file keys — "drums", "bass",
///     "other", "vocals" in lower case beside a capitalised "Original", in English whatever
///     the language. The key still drives the engine; the label is a word.
/// </summary>
public sealed class StemDisplayNameTests
{
    [Theory]
    [InlineData("drums", "Stem_Drums")]
    [InlineData("bass", "Stem_Bass")]
    [InlineData("vocals", "Stem_Vocals")]
    [InlineData("other", "Stem_Other")]
    [InlineData("guitar", "Stem_Guitar")]
    [InlineData("Piano", "Stem_Piano")]
    public void ASeparatorStem_IsNamedInTheUsersLanguage(string key, string resource)
    {
        var strip = new StemTrackViewModel(new FakeAudioPlaybackService(), new Stem { Name = key }, FakeLocalization.KeysOnly());

        Assert.Equal(resource, strip.DisplayName);
        Assert.Equal(key, strip.Name);
    }

    [Theory]
    [InlineData("rhythm guitar", "Rhythm guitar")]
    [InlineData("Click Track", "Click Track")]
    public void AnyOtherStem_KeepsItsName_WithACapital(string key, string expected)
    {
        var strip = new StemTrackViewModel(new FakeAudioPlaybackService(), new Stem { Name = key }, FakeLocalization.KeysOnly());

        Assert.Equal(expected, strip.DisplayName);
    }

    [Fact]
    public void TheMasterStrip_IsTheOriginal_InTheUsersLanguage()
    {
        var strip = new StemTrackViewModel(new FakeAudioPlaybackService(), "Original", muted: false, FakeLocalization.KeysOnly());

        Assert.Equal("Stem_Original", strip.DisplayName);
    }
}
