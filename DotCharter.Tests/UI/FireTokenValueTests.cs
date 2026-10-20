using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia.Media;

namespace DotCharter.Tests.UI;

/// <summary>
///     The Fire design layer's §4 token VALUES, pinned against the owner's
///     `Implementation Spec.dc.html`.
///     <para>
///         <b>Why values and not just names.</b> A wrong colour is the one theme
///         fault with no symptom at all: it builds, it binds, it renders, and the
///         only way to notice is to hold the screen next to the mockup. The Fire
///         layer had already drifted this way once — the border weight, the
///         spacing ladder, the card shadow and the primary glow all moved a step
///         or two from the spec while every test stayed green.
///     </para>
///     <para>
///         <b>The string and gear entries are IDENTITIES.</b> The spec labels
///         them "FIXED, DO NOT REASSIGN": a string is its colour on the highway,
///         in the gutter, in the tuner and on a chord chart, and a gear category
///         is its colour on the board, in the gear list and on a tone chip.
///         Re-ordering them or nudging one toward the accent hue is a product
///         defect, not a palette tweak — so they get their own case and their own
///         message.
///     </para>
///     <para>
///         Source-level and platform-free, so it runs in the DEFAULT gate. It
///         parses the shipped <c>FireTokens.axaml</c> and compares through
///         <c>Color.Parse</c>, so <c>#E05A5A</c> and <c>#FFE05A5A</c>
///         compare equal and a token cannot pass by being spelled differently.
///         The runtime counterpart — do these keys actually resolve off a booted
///         app — is <c>Perf/FireComponentUiPerfSmokeTests</c>.
///     </para>
/// </summary>
public sealed class FireTokenValueTests
{
    /// <summary>
    ///     STRING IDENTITY · FIXED, DO NOT REASSIGN.
    ///     Verbatim from the spec's <c>STRINGS</c> table, in its order.
    /// </summary>
    private static readonly (string Key, string Colour, string What)[] Strings =
    [
        ("FireString6E", "#E05A5A", "E (6, low)"),
        ("FireString5A", "#E0C04A", "A (5)"),
        ("FireString4D", "#4A90E0", "D (4)"),
        ("FireString3G", "#E08A3C", "G (3)"),
        ("FireString2B", "#4ADE80", "B (2)"),
        ("FireString1e", "#A78BFA", "e (1, high)")
    ];

    /// <summary>
    ///     GEAR CATEGORY IDENTITY · used in the rig, the gear list and tone
    ///     chips. Verbatim from the spec's <c>GEAR</c> table, in its order.
    /// </summary>
    private static readonly (string Key, string Colour, string What)[] Gear =
    [
        ("FireGearDynamics", "#8FA3BD", "Dynamics"),
        ("FireGearAmps", "#F0A54A", "Amps"),
        ("FireGearCabinets", "#C98F5E", "Cabinets"),
        ("FireGearDrive", "#E05A5A", "Drive"),
        ("FireGearEq", "#4ADE80", "EQ"),
        ("FireGearModulation", "#E879B9", "Modulation"),
        ("FireGearDelayReverb", "#3EC9C0", "Delay / Reverb"),
        ("FireGearUtility", "#A78BFA", "Utility / Routing")
    ];

    /// <summary>
    ///     The palette, as Graphite specifies it
    ///     (artifacts/design-proposals/graphite.md, the one theme since 0.62).
    ///     The Fire mockups' §01 violet table it replaced is in git. The key on
    ///     the left is this repo's; the third column names the role.
    /// </summary>
    private static readonly (string Key, string Colour, string SpecName)[] PaletteRows =
    [
        ("FireGroundScreen", "#111317", "Surface / base"),
        ("FireSurfaceChrome", "#15181D", "Surface / chrome"),
        ("FireSurfaceChromeAlt", "#171A20", "Surface / chrome alt"),
        ("FireSurfaceCard", "#06FFFFFF", "Surface / panel — white .025"),
        ("FireSurfaceField", "#0AFFFFFF", "Surface / raised — white .04"),
        ("FireBorder", "#14FFFFFF", "Border / hairline — white .08"),
        ("FireBorderStrong", "#1FFFFFFF", "Border / strong — white .12"),
        ("FireTextPrimary", "#E8EBEF", "Text / primary"),
        ("FireTextSecondary", "#AEB6C1", "Text / secondary"),
        ("FireTextMuted", "#929BA8", "Text / tertiary"),
        ("FireTextDim", "#7A8390", "Text / quaternary"),
        ("FireTextFaint", "#687080", "Text / disabled"),
        ("FireAccent", "#7AB6FF", "Accent / sky"),
        ("FireAccentStrong", "#5E9FF2", "Accent / sky strong"),
        ("FireAccentDeep", "#3C7BD1", "Accent / sky deep"),
        ("FireAccentBright", "#A8CEFF", "Accent / sky tint"),
        ("FireAccentPale", "#CFE2FF", "Accent / sky text on fills"),
        ("FireWarning", "#F2B557", "Signal / amber"),
        ("FireSuccess", "#5BD08F", "Signal / green"),
        ("FireDanger", "#F07A7A", "Signal / red")
    ];

    public static TheoryData<string, string, string> StringIdentities => Rows(Strings);
    public static TheoryData<string, string, string> GearIdentities => Rows(Gear);
    public static TheoryData<string, string, string> Palette => Rows(PaletteRows);

    private static TheoryData<string, string, string> Rows((string, string, string)[] source)
    {
        var data = new TheoryData<string, string, string>();
        foreach (var (a, b, c) in source) data.Add(a, b, c);
        return data;
    }

    [Theory]
    [MemberData(nameof(StringIdentities))]
    public void EveryStringIdentity_IsExactlyItsSpecifiedColour(string key, string expected, string what)
    {
        AssertBrush(key, expected,
            $"the {what} string identity has drifted. This colour is not a palette entry — it is what a " +
            "player reads without looking, on the highway, in the string gutter, in the tuner and on a " +
            "chord diagram. The spec marks the six FIXED, DO NOT REASSIGN.");
    }

    [Theory]
    [MemberData(nameof(GearIdentities))]
    public void EveryGearCategoryIdentity_IsExactlyItsSpecifiedColour(string key, string expected, string what)
    {
        AssertBrush(key, expected,
            $"the '{what}' gear-category identity has drifted. The category IS the colour — on the board, " +
            "in the gear list and on every tone chip — so a nudge here desynchronises three surfaces at once.");
    }

    [Theory]
    [MemberData(nameof(Palette))]
    public void EveryPaletteToken_IsExactlyItsSpecifiedColour(string key, string expected, string specName)
    {
        AssertBrush(key, expected,
            $"'{specName}' in the spec's §01 colour table. A wrong colour here has no symptom: it builds, " +
            "it binds, it renders, and only a side-by-side with the mockup shows it.");
    }

    /// <summary>
    ///     The six string identities must be SIX DISTINCT colours. Two the same
    ///     is not a cosmetic slip — it makes two strings unreadable from each
    ///     other everywhere they appear at once.
    /// </summary>
    [Fact]
    public void TheSixStringIdentities_AreSixDistinctColours()
    {
        var colours = Strings.Select(s => ColorOf(s.Key)).ToList();

        Assert.Equal(6, colours.Count);
        Assert.True(colours.Distinct().Count() == 6,
            "two string identities are the same colour — that makes two strings unreadable from each other " +
            "everywhere they appear together: the highway, the gutter, the tuner and every chord diagram");
    }

    /// <summary>
    ///     The spacing ladder is CLOSED: 2 · 4 · 6 · 8 · 10 · 12 · 14 · 16 · 18 ·
    ///     20 · 22 · 26 · 32 · 40, and every gap between siblings comes from it.
    ///     The port carried a 9 that is on no step and was missing five that are,
    ///     so the most-used gap in the converted screens sat half a step off
    ///     every other one — invisible per element, cumulative down a column.
    /// </summary>
    [Fact]
    public void TheSpacingLadder_IsExactlyTheSpecScale()
    {
        double[] expected = [2, 4, 6, 8, 10, 12, 14, 16, 18, 20, 22, 26, 32, 40];

        var actual = DoubleTokens()
            .Where(kv => kv.Key.StartsWith("FireSpace", StringComparison.Ordinal))
            .Select(kv => kv.Value)
            .Distinct()
            .Order()
            .ToArray();

        Assert.Equal(expected, actual);
    }

    /// <summary>
    ///     The spec allows exactly three elevations, all "large, soft and very
    ///     negative-spread — no small drop shadows". The port's card shadow was
    ///     tighter and weaker than the spec's, and its glow existed twice at two
    ///     sizes, so a lit button's brightness depended on which key a screen
    ///     happened to pick.
    /// </summary>
    [Theory]
    [InlineData("FireShadowPanel", "0 26 56 -28", "card")]
    [InlineData("FireShadowScreen", "0 60 140 -50", "overlay")]
    public void EveryElevationRecipe_UsesTheSpecGeometry(string key, string geometry, string role)
    {
        var declared = ShadowTokens();
        Assert.True(declared.TryGetValue(key, out var value),
            $"{key} is not declared in FireTokens.axaml");

        Assert.True(value!.StartsWith(geometry + " ", StringComparison.Ordinal),
            $"the '{role}' elevation is '{value}', but the spec's is '{geometry} …'. " +
            "Shadows in this layer are large, soft and very negative-spread; a tighter one reads as a " +
            "small drop shadow, which the spec rules out in as many words.");
    }

    /// <summary>
    ///     <c>FireOverlayFill</c> is the ground a SCREEN stands on, and it is
    ///     fully opaque at every stop — a screen has nothing behind it and letting
    ///     the app bleed through was a real shipped bug. The fix is structural:
    ///     both stops carry alpha FF, so there is no alpha for a later change to
    ///     get wrong.
    ///     <para>
    ///         <b>It is NOT the modal scrim</b>, though it was until
    ///         <c>aa5e587b</c> and this test's name said so for far longer. The
    ///         two roles are opposites and sharing one token is what made every
    ///         dialog read as its own screen — see
    ///         <see cref="TheModalScrim_IsTranslucentButStillDeepEnoughToReadOver" />.
    ///     </para>
    /// </summary>
    [Fact]
    public void TheScreenGroundFill_IsFullyOpaqueAtEveryStop()
    {
        var declaration = Regex.Match(
            Tokens(),
            @"<LinearGradientBrush\s+x:Key=""FireOverlayFill""(?<body>.*?)</LinearGradientBrush>",
            RegexOptions.Singleline);

        Assert.True(declaration.Success, "FireOverlayFill is not declared in FireTokens.axaml");

        var stops = Regex.Matches(declaration.Groups["body"].Value, @"Color=""(?<c>#[0-9A-Fa-f]+)""")
            .Select(m => Color.Parse(m.Groups["c"].Value))
            .ToList();

        Assert.NotEmpty(stops);
        Assert.All(stops, c => Assert.True(c.A == 0xFF,
            $"FireOverlayFill has a stop at alpha 0x{c.A:X2}. It is the ground a SCREEN stands on — a " +
            "translucent one lets the app bleed through, which shipped once already. Both stops must be " +
            "FF. If you are looking for the MODAL scrim, that is FireScrimFill and it is deliberately " +
            "translucent."));
    }

    /// <summary>
    ///     …and the other half of the same rule, which had no guard at all.
    ///     <c>FireScrimFill</c> is what a MODAL lays over the surface it opened
    ///     on, and it is translucent ON PURPOSE: an opaque one covers the thing
    ///     the dialog is about, which is exactly what made every dialog read as
    ///     navigating to a new screen. The owner: "all modals should stay modals
    ///     not own screen".
    ///     <para>
    ///         Asserted in BOTH directions. Opaque is the regression that was
    ///         reported; too thin reintroduces the readability problem that made
    ///         someone reach for the opaque token in the first place.
    ///     </para>
    /// </summary>
    [Fact]
    public void TheModalScrim_IsTranslucentButStillDeepEnoughToReadOver()
    {
        // ColorOf follows the brush to its Color token: since the palette was
        // tokenised the alpha lives on `FireScrimFillColor`, still in one place.
        var colour = ColorOf("FireScrimFill");

        Assert.True(colour.A < 0xFF,
            $"FireScrimFill is fully opaque (alpha 0x{colour.A:X2}). A MODAL scrim is translucent: covering " +
            "the surface the dialog is about is what made every dialog read as its own screen. The opaque " +
            "token is FireOverlayFill, for SCREEN backgrounds.");
        Assert.True(colour.A >= 0xC0,
            $"FireScrimFill is only alpha 0x{colour.A:X2} — too thin for body text over it to stay " +
            "readable, which is the reason the opaque token was reached for in the first place.");
    }

    // ─── helpers ──────────────────────────────────────────────────────────

    private static void AssertBrush(string key, string expected, string why)
    {
        var actual = ColorOf(key);
        Assert.True(Color.Parse(expected) == actual,
            $"{key} is {actual} but the spec says {Color.Parse(expected)} — {why}");
    }

    private static Color ColorOf(string key)
    {
        var match = Regex.Match(Tokens(),
            $@"<SolidColorBrush\s+x:Key=""{Regex.Escape(key)}""\s+Color=""(?<c>[^""]+)""");
        Assert.True(match.Success,
            $"{key} is not declared as a SolidColorBrush in DotCharter.UI/Styles/FireTokens.axaml");

        // Since the palette was tokenised the brush names a Color token
        // (`Color="{DynamicResource FireGroundColor}"`) so a theme can recolour
        // the Fire layer — the same shape as ColorTokens.axaml. Follow it to
        // the literal, which is what this file's spec table is about.
        var value = match.Groups["c"].Value;
        var indirect = Regex.Match(value, @"^\{(?:Dynamic|Static)Resource\s+(?<k>[^}]+)\}$");
        if (!indirect.Success) return Color.Parse(value);

        var token = indirect.Groups["k"].Value.Trim();
        var colour = Regex.Match(Tokens(),
            $@"<Color\s+x:Key=""{Regex.Escape(token)}""\s*>\s*(?<c>#[0-9A-Fa-f]+)\s*</Color>");
        Assert.True(colour.Success,
            $"{key} resolves to {token}, which is not declared as a Color in the Fire token files");
        return Color.Parse(colour.Groups["c"].Value);
    }

    /// <summary>
    ///     Graphite's lit action is "an 8 px rounded rectangle (no chamfer, no
    ///     glow, no hover lift)" (design-proposals/graphite.md § Shape). The
    ///     chamfer token was 0 for a release while the converter read 0 as unset
    ///     and drew the 16 px cut anyway, so all three halves are pinned here.
    /// </summary>
    [Fact]
    public void TheLitAction_IsFlatSquareAndUnlit()
    {
        Assert.Equal(0, DoubleTokens()["FireCutSize"]);
        Assert.Equal(0, DotCharter.Tone.UI.Converters.FireChamferClipConverter.CutFrom([default(Avalonia.Rect), DoubleTokens()["FireCutSize"]]));

        var glow = ShadowTokens()["FireGlowAction"];
        var colour = Color.Parse(glow.Split(' ', StringSplitOptions.RemoveEmptyEntries)[^1]);
        Assert.True(colour.A == 0, $"FireGlowAction is '{glow}': Graphite's lit action carries no glow.");

        var fire = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "DotCharter.Tone.UI", "Styles", "Fire.axaml"));
        Assert.DoesNotContain("translateY(-2px)", fire, StringComparison.Ordinal);
    }

    private static Dictionary<string, double> DoubleTokens()
    {
        var found = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(Tokens(),
                     @"<x:Double\s+x:Key=""(?<key>[^""]+)"">(?<v>[^<]+)</x:Double>"))
        {
            found[m.Groups["key"].Value] =
                double.Parse(m.Groups["v"].Value, CultureInfo.InvariantCulture);
        }

        return found;
    }

    private static Dictionary<string, string> ShadowTokens()
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(Tokens(),
                     @"<BoxShadows\s+x:Key=""(?<key>[^""]+)"">(?<v>[^<]+)</BoxShadows>"))
        {
            found[m.Groups["key"].Value] = m.Groups["v"].Value.Trim();
        }

        return found;
    }

    private static string? _tokens;

    private static string Tokens()
    {
        if (_tokens != null) return _tokens;
        var path = Path.Combine(TestPaths.RepoRoot(), "DotCharter.Tone.UI", "Styles", "FireTokens.axaml");
        Assert.True(File.Exists(path), $"FireTokens.axaml not found at {path}");
        // A CONTAINER over `Styles/FireTokens/` — every declaration this file
        // parses lives in one of the parts, so it has to be read with them.
        return _tokens = StyleFileText.ReadWithIncludes(path);
    }
}
