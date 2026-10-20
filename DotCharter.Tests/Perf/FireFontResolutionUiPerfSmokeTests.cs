using Avalonia.Media;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The runtime half of the bundled-font guard: does Avalonia's real
///     <see cref="FontManager" /> actually hand back a glyph typeface for each
///     `avares://…#Family` token, and does asking for Bold get a DIFFERENT face
///     than asking for Regular?
///     <para>
///         <see cref="DotCharter.Tests.UI.FireFontAssetTests" /> proves the files
///         are there and that their declared family names match the tokens. Only
///         this one proves the font manager agrees — the two can disagree,
///         because Avalonia groups embedded faces by whatever name its platform
///         backend reports, which is not guaranteed to be the `name` table's
///         nameID 1.
///     </para>
///     <para>
///         Named <c>*UiPerfSmokeTests</c> because <c>FontManager.Current</c>
///         needs a real Avalonia platform, which is what the excluded-from-the-
///         gate app session provides. Run it by hand after touching
///         <c>Assets/Fonts</c> or the `FireFont*` tokens.
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class FireFontResolutionUiPerfSmokeTests
{
    private const string Root = "avares://DotCharter.Tone.UI/Assets/Fonts";

    public static TheoryData<string> Families =>
        ["Atkinson Hyperlegible", "JetBrains Mono"];

    [Theory]
    [MemberData(nameof(Families))]
    public async Task EveryBundledFamily_ResolvesToARealGlyphTypeface(string family)
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var typeface = new Typeface(new FontFamily($"{Root}#{family}"));

            Assert.True(FontManager.Current.TryGetGlyphTypeface(typeface, out var glyphs),
                $"'{family}' did not resolve — every TextBlock asking for it renders in the fallback face, silently");

            // `TryGetGlyphTypeface` also succeeds when nothing matched: it hands
            // back the platform's DEFAULT face. So the family name it reports —
            // not the bool — is what proves the bundled file was the one loaded.
            Assert.Equal(family, glyphs.FamilyName);
        }, CancellationToken.None);
    }

    /// <summary>
    ///     Both cuts must be reachable THROUGH THE FAMILY. This is the assertion
    ///     that would have failed on the first bundle attempt: Google's static
    ///     SemiBold instances declare their own family, so a four-file family
    ///     looked complete while `FontWeight="SemiBold"` still landed on the same
    ///     face as Regular.
    /// </summary>
    [Theory]
    [MemberData(nameof(Families))]
    public async Task AskingForBold_GetsADifferentFaceThanRegular(string family)
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var name = new FontFamily($"{Root}#{family}");

            Assert.True(FontManager.Current.TryGetGlyphTypeface(
                new Typeface(name, FontStyle.Normal, FontWeight.Normal), out var regular));
            Assert.True(FontManager.Current.TryGetGlyphTypeface(
                new Typeface(name, FontStyle.Normal, FontWeight.Bold), out var bold));

            Assert.Equal(FontWeight.Normal, regular.Weight);
            Assert.Equal(FontWeight.Bold, bold.Weight);
            Assert.NotSame(regular, bold);
        }, CancellationToken.None);
    }
}
