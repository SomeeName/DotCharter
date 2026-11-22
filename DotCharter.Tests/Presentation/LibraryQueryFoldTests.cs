using DotCharter.Presentation.Shared.Library;

namespace DotCharter.Tests.Presentation;

/// <summary>
///     The library search fold keeps kana voicing marks, folds width and still drops Latin accents.
/// </summary>
public sealed class LibraryQueryFoldTests
{
    [Fact]
    public void VoicedKanaStaysDistinctFromUnvoiced()
    {
        Assert.NotEqual(LibraryQuery.Fold("かぜ"), LibraryQuery.Fold("がぜ"));
        Assert.NotEqual(LibraryQuery.Fold("ばんど"), LibraryQuery.Fold("はんと"));
        Assert.DoesNotContain(LibraryQuery.Fold("かぜ"), LibraryQuery.Fold("がぜ"), StringComparison.Ordinal);
    }

    [Fact]
    public void HalfWidthKanaMatchFullWidth()
    {
        Assert.Equal(LibraryQuery.Fold("カセ"), LibraryQuery.Fold("ｶｾ"));
        Assert.Equal(LibraryQuery.Fold("ガゼ"), LibraryQuery.Fold("ｶﾞｾﾞ"));
        Assert.Equal(LibraryQuery.Fold("ＡＢＣ"), LibraryQuery.Fold("abc"));
    }

    [Fact]
    public void LatinAccentsStillFold()
    {
        Assert.Equal(LibraryQuery.Fold("motorhead"), LibraryQuery.Fold("Motörhead"));
        Assert.Equal(LibraryQuery.Fold("cafe"), LibraryQuery.Fold("café"));
    }
}
