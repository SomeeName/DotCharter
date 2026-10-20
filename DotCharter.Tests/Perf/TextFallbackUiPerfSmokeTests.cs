using Avalonia.Media;
using DotCharter.Tone.UI.Platform;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The runtime half of the fallback-chain guard.
///     <para>
///         <b>What this does NOT claim.</b> Measured 2026-08-21 on Windows 11 /
///         Avalonia 12.1.1: the Skia font manager already matches CJK with no
///         chain configured at all (<c>八</c> and <c>ヨ</c> to Yu Gothic UI,
///         <c>한</c> to Malgun Gothic). So the CJK cases below pass either way
///         and are a floor, not the point.
///     </para>
///     <para>
///         <b>What it does claim</b> is the part that actually changed: without
///         the chain the matcher's pick for everything ELSE is arbitrary —
///         <c>é</c> and <c>Ж</c> both resolved to <b>Malgun Gothic</b>, a Korean
///         face. <see cref="LatinPlusCharacters_ResolveToABroadLatinFace_EvenAfterACjkMatch" /> is
///         the discriminating assertion; it fails with the chain removed from
///         <see cref="HeadlessSessionFixture" />, which is how it was verified.
///     </para>
///     <para>
///         Needs <c>DOTCHARTER_UI_SWEEP_DUMP=1</c>: with headless DRAWING the
///         font manager is a stub with no system faces and every match fails.
///         Named <c>*UiPerfSmokeTests</c> because <c>FontManager.Current</c>
///         needs a real Avalonia platform;
///         <see cref="DotCharter.Tests.UI.TextFallbackFontTests" /> is the pure
///         half that runs in the default gate.
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class TextFallbackUiPerfSmokeTests
{
    private static readonly string[] LatinOnlyThemeFaces =
        ["Atkinson Hyperlegible", "JetBrains Mono"];

    private static void Match(int codepoint, string what, Action<string> assertFamily)
    {
        Assert.SkipUnless(UiSweepRunner.CaptureEnabled,
            "needs DOTCHARTER_UI_SWEEP_DUMP=1 — headless drawing has no system fonts");

        var matched = FontManager.Current.TryMatchCharacter(
            codepoint, FontStyle.Normal, FontWeight.Normal, FontStretch.Normal,
            null, null, out var typeface);

        Assert.True(matched, $"no face at all for {what}");
        Assert.True(FontManager.Current.TryGetGlyphTypeface(typeface, out var glyphs));
        Assert.DoesNotContain(glyphs.FamilyName, LatinOnlyThemeFaces);
        assertFamily(glyphs.FamilyName);
    }

    public static TheoryData<int, string> CjkCodepoints => new()
    {
        { '八', "Japanese kanji (the owner's imported title, 八月、某、月明かり)" },
        { 'ヨ', "katakana (the artist, ヨルシカ)" },
        { '한', "Hangul" }
    };

    [Theory]
    [MemberData(nameof(CjkCodepoints))]
    public async Task ACjkCodepoint_FindsAFace(int codepoint, string what)
        => await HeadlessSessionFixture.Dispatch(
            () => Match(codepoint, what, _ => { }), CancellationToken.None);

    public static TheoryData<int, string> LatinPlusCodepoints => new()
    {
        { 'é', "accented Latin" },
        { 'Ж', "Cyrillic" },
        { '♭', "the flat in a tuning (E♭ Standard)" }
    };

    /// <summary>
    ///     The assertion the chain exists for: a character a broad UI face
    ///     handles must not land on a CJK face.
    ///     <para>
    ///         <b>The CJK match first is load-bearing, not decoration.</b> With no
    ///         chain the matcher's answer depends on its own CACHE: asked cold,
    ///         it returns Segoe UI for <c>é</c> and the test would pass while
    ///         proving nothing. Asked right after a Hangul match — the measured
    ///         2026-08-21 sequence, and the ordinary case in an app that has just
    ///         rendered a Japanese song title — it returns <b>Malgun Gothic</b>.
    ///         Priming is what makes this fail with the chain removed from
    ///         <see cref="HeadlessSessionFixture" />, which is how it was verified.
    ///     </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(LatinPlusCodepoints))]
    public async Task LatinPlusCharacters_ResolveToABroadLatinFace_EvenAfterACjkMatch(
        int codepoint, string what)
        => await HeadlessSessionFixture.Dispatch(() =>
        {
            Match('한', "Hangul (priming the matcher's cache)", _ => { });
            Match(codepoint, what, family => Assert.Contains(family,
                TextFallbackFonts.FamilyNames.TakeWhile(n => n != "Yu Gothic UI")));
        }, CancellationToken.None);
}
