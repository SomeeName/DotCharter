using DotCharter.Analysis.Lyrics.Syllables;

namespace DotCharter.Tests.Analysis.Lyrics;

/// <summary>#869: the mora rules for kana.</summary>
public sealed class KanaMoraSplitterTests
{
    [Theory]
    [InlineData("さくら", "さ|く|ら")]
    [InlineData("きょう", "きょ|う")]
    [InlineData("しゃしん", "しゃ|し|ん")]
    [InlineData("がっこう", "が|っ|こ|う")]
    [InlineData("にほん", "に|ほ|ん")]
    [InlineData("コーヒー", "コ|ー|ヒ|ー")]
    [InlineData("ファイル", "ファ|イ|ル")]
    [InlineData("ティー", "ティ|ー")]
    [InlineData("ウィスキー", "ウィ|ス|キ|ー")]
    [InlineData("チェック", "チェ|ッ|ク")]
    [InlineData("ジュース", "ジュ|ー|ス")]
    [InlineData("あぁ", "あ|ぁ")]
    [InlineData("かぁ", "か|ぁ")]
    public void Morae(string kana, string expected) => Assert.Equal(expected, string.Join('|', KanaMoraSplitter.Split(kana)));

    [Fact]
    public void Small_ya_never_starts_a_mora_and_small_tsu_and_n_always_count()
    {
        Assert.Equal(3, KanaMoraSplitter.Split("っっん").Count);
        Assert.Equal(["ゃ"], KanaMoraSplitter.Split("ゃ"));
    }

    [Fact]
    public void Kana_text_is_recognised()
    {
        Assert.True(KanaMoraSplitter.IsKana("コーヒー"));
        Assert.False(KanaMoraSplitter.IsKana("kohi"));
        Assert.False(KanaMoraSplitter.IsKana(""));
    }
}
