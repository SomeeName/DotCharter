using DotCharter.Tone.UI.Platform;

namespace DotCharter.Tests.UI;

/// <summary>
///     The per-glyph font FALLBACK chain. Every face in the theme is Latin-only
///     (Chakra Petch / Space Grotesk / IBM Plex Mono), so without a chain a CJK
///     song title renders as tofu boxes — which reads as "mangled text" and
///     sends you hunting an encoding bug that does not exist. Owner report
///     2026-08-21 on a Japanese import whose tags the app had in fact read
///     perfectly (<c>artist='ヨルシカ'</c> in the log).
/// </summary>
public sealed class TextFallbackFontTests
{
    private static readonly string[] CjkFamilies =
    [
        "Yu Gothic UI", "Meiryo UI", "Microsoft YaHei UI",
        "Microsoft JhengHei UI", "Malgun Gothic",
        "Hiragino Sans", "PingFang SC", "Apple SD Gothic Neo",
        "Noto Sans CJK JP", "Noto Sans CJK SC", "Noto Sans CJK KR"
    ];

    [Fact]
    public void TheChain_IsNotEmpty()
    {
        Assert.NotEmpty(TextFallbackFonts.FamilyNames);
        Assert.NotEmpty(TextFallbackFonts.Options().FontFallbacks!);
    }

    [Fact]
    public void EveryFamilyName_IsRealAndListedOnce()
    {
        Assert.All(TextFallbackFonts.FamilyNames, n => Assert.False(string.IsNullOrWhiteSpace(n)));
        Assert.Equal(TextFallbackFonts.FamilyNames.Length,
            TextFallbackFonts.FamilyNames.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData("Yu Gothic UI")]          // Japanese, the reported gap
    [InlineData("Microsoft YaHei UI")]    // Simplified Chinese
    [InlineData("Malgun Gothic")]         // Korean
    public void TheChain_CoversTheScriptTheAppActuallyFailedOn(string family)
        => Assert.Contains(family, TextFallbackFonts.FamilyNames);

    [Fact]
    public void BroadLatinFacesComeBeforeTheCjkOnes()
    {
        // Order is priority and the matcher takes the FIRST family that HAS the
        // glyph. Segoe UI carries no CJK, so it cannot steal one; a CJK face put
        // first WOULD steal every accented Latin and Cyrillic character, because
        // it has those glyphs too. Measured 2026-08-21: with the chain ordered
        // CJK-first, 'e-acute' and Cyrillic Zhe both resolved to Yu Gothic UI.
        var names = TextFallbackFonts.FamilyNames;
        var firstCjk = Array.FindIndex(names, n => CjkFamilies.Contains(n, StringComparer.Ordinal));
        var segoe = Array.IndexOf(names, "Segoe UI");

        Assert.True(firstCjk >= 0, "no CJK family in the chain at all");
        Assert.True(segoe >= 0 && segoe < firstCjk,
            $"'Segoe UI' is at {segoe} but the first CJK face is at {firstCjk}");
    }

    /// <summary>
    ///     A chain nothing APPLIES is dead code that reads as shipped — the
    ///     failure shape this repo has hit four times. The only place it can take
    ///     effect is the AppBuilder, so that is what gets asserted.
    /// </summary>
    [Fact]
    public void Program_ActuallyAppliesTheChainToTheAppBuilder()
    {
        var source = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Program.cs")));
        var code = string.Join('\n', source.Split('\n')
            .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        Assert.Contains("TextFallbackFonts.Options()", code, StringComparison.Ordinal);
    }
}
