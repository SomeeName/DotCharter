using Avalonia.Media;
using DotCharter.UI.Platform;
using DotCharter.UI.Theming;

namespace DotCharter.Tests.UI.Theming;

/// <summary>
///     <b>Picking a colour-blind string preset must not throw away the custom
///     palette.</b>
///     <para>
///         Settings ▸ Appearance carries three things at once: a theme preset
///         list, a "String colours" ComboBox (the accessibility presets) and a
///         "Customize" grid of per-token colour pickers. The ComboBox used to
///         run <c>ApplySelectedTheme</c>, whose override set is the seven
///         <c>String*</c> colours and NOTHING else — so re-applying the preset
///         reset every customized token to the preset's own value. It was
///         invisible for exactly as long as the user stayed on the page: the
///         Save that followed diffed the now-identical colours, wrote an EMPTY
///         <c>ThemeColorOverrides</c>, and the customization was gone from disk
///         too.
///     </para>
///     <para>
///         Play Mode's twin of the same row always merged the persisted
///         overrides before applying, so the two surfaces over one config field
///         disagreed — the shape this repo has shipped before (the count-in
///         rows). <see cref="ThemeOverrides" /> is now the single answer and
///         both the palette row and the save go through it, via <see cref="AppearanceTheme" />.
///     </para>
/// </summary>
public sealed class ThemeOverridesTests
{
    private const string CustomToken = ThemeTokens.AppBackground;
    private static readonly Color CustomColor = Color.Parse("#FF102030");

    // ── The pure rule ──────────────────────────────────────────────────────

    [Fact]
    public void FreeForm_ReportsOnlyTheTokensThatDifferFromThePreset()
    {
        var preset = ThemePresets.Default;
        var current = new Dictionary<string, Color>(preset.Colors) { [CustomToken] = CustomColor };

        var overrides = ThemeOverrides.FreeForm(current, preset.Colors, excludeStringTokens: false);

        Assert.Equal([CustomToken], overrides.Keys);
        Assert.Equal(CustomColor, Color.Parse(overrides[CustomToken]));
    }

    [Fact]
    public void FreeForm_CanDropTheStringTokensTheStringPresetGoverns()
    {
        var preset = ThemePresets.Default;
        var current = new Dictionary<string, Color>(preset.Colors)
        {
            [CustomToken] = CustomColor,
            [ThemeTokens.String1] = Color.Parse("#FFD55E00")
        };

        Assert.Contains(ThemeTokens.String1,
            ThemeOverrides.FreeForm(current, preset.Colors, excludeStringTokens: false).Keys);
        Assert.DoesNotContain(ThemeTokens.String1,
            ThemeOverrides.FreeForm(current, preset.Colors, excludeStringTokens: true).Keys);
    }

    [Fact]
    public void ForStringColorPreset_KeepsTheCustomColoursAndLayersTheSevenStringsOnTop()
    {
        var preset = ThemePresets.Default;
        var current = new Dictionary<string, Color>(preset.Colors) { [CustomToken] = CustomColor };

        var overrides = ThemeOverrides.ForStringColorPreset(current, preset.Colors, "okabe-ito");

        // The whole point: the customized token survives the string-preset pick.
        Assert.True(overrides.ContainsKey(CustomToken),
            "picking a string-colour preset dropped the user's customized colours");
        Assert.Equal(CustomColor, Color.Parse(overrides[CustomToken]));

        foreach (var kv in StringColorPresets.GetOverrides("okabe-ito"))
            Assert.Equal(Color.Parse(kv.Value), Color.Parse(overrides[kv.Key]));
    }

    /// <summary>Going BACK to "Default (theme)" has to restore the theme's own
    /// string colours. That is why the free-form set drops the String* tokens on
    /// this path: bake the currently-applied Okabe-Ito colours in as free-form
    /// overrides and no later choice can ever undo them.</summary>
    [Fact]
    public void ForStringColorPreset_Default_ReleasesTheStringTokensBackToTheTheme()
    {
        var preset = ThemePresets.Default;
        var current = new Dictionary<string, Color>(preset.Colors) { [CustomToken] = CustomColor };
        foreach (var kv in StringColorPresets.GetOverrides("okabe-ito"))
            current[kv.Key] = Color.Parse(kv.Value);

        var overrides = ThemeOverrides.ForStringColorPreset(
            current, preset.Colors, StringColorPresets.DefaultId);

        Assert.True(overrides.ContainsKey(CustomToken));
        foreach (var token in StringColorPresets.GetOverrides("okabe-ito").Keys)
        {
            Assert.False(overrides.ContainsKey(token),
                $"{token} stayed pinned to the previous string preset, so 'Default (theme)' can never restore it");
        }
    }

    // ── …and the same thing driven through the real theme service ──────────

    [Fact]
    public void ThroughTheThemeService_ACustomColourSurvivesAStringPresetPick()
    {
        var service = new ThemeService();
        service.ApplyPreset(ThemePresets.Default,
            new Dictionary<string, string> { [CustomToken] = ThemeOverrides.ToHex(CustomColor) });
        Assert.Equal(CustomColor, service.GetColor(CustomToken));

        // Exactly what AppearanceTheme.ApplyStringPalette does.
        service.ApplyPreset(service.CurrentPreset,
            ThemeOverrides.ForStringColorPreset(
                service.CurrentColors, service.CurrentPreset.Colors, "okabe-ito"));

        Assert.Equal(CustomColor, service.GetColor(CustomToken));
        Assert.Equal(
            Color.Parse(StringColorPresets.GetOverrides("okabe-ito")[ThemeTokens.String1]),
            service.GetColor(ThemeTokens.String1));
    }

    // ── The wiring, through the seam Settings ▸ Appearance drives ──────────

    /// <summary>
    ///     <c>ResetColors</c> is the RESET path and deliberately drops the
    ///     customized colours. The palette row must never route there; that IS
    ///     the bug, so it is asserted on the adapter the view model calls.
    /// </summary>
    [Fact]
    public void TheStringPaletteRow_KeepsTheCustomColours_AndResetDropsThem()
    {
        var service = new ThemeService();
        var theme = new AppearanceTheme(service);
        service.ApplyPreset(ThemePresets.Default,
            new Dictionary<string, string> { [CustomToken] = ThemeOverrides.ToHex(CustomColor) });

        theme.ApplyStringPalette("okabe-ito");
        Assert.Equal(CustomColor, service.GetColor(CustomToken));
        Assert.Equal(Color.Parse(StringColorPresets.GetOverrides("okabe-ito")[ThemeTokens.String1]),
            service.GetColor(ThemeTokens.String1));

        theme.ResetColors("okabe-ito");
        Assert.Equal(ThemePresets.Default.Colors[CustomToken], service.GetColor(CustomToken));
        Assert.Equal(Color.Parse(StringColorPresets.GetOverrides("okabe-ito")[ThemeTokens.String1]),
            service.GetColor(ThemeTokens.String1));
    }

    /// <summary>The save path and the apply path compute "what is an override" the same way.</summary>
    [Fact]
    public void TheSavedOverrides_AreTheSameHelpersAnswer()
    {
        var service = new ThemeService();
        var theme = new AppearanceTheme(service);
        service.ApplyPreset(ThemePresets.Default,
            new Dictionary<string, string> { [CustomToken] = ThemeOverrides.ToHex(CustomColor) });
        theme.ApplyStringPalette("okabe-ito");

        foreach (var exclude in new[] { false, true })
        {
            Assert.Equal(
                ThemeOverrides.FreeForm(service.CurrentColors, service.CurrentPreset.Colors, exclude),
                theme.FreeFormOverrides(exclude));
        }

        Assert.DoesNotContain(ThemeTokens.String1, theme.FreeFormOverrides(excludeStringTokens: true).Keys);
    }

    /// <summary>A persisted theme is the free-form overrides with the palette's string colours winning.</summary>
    [Fact]
    public void ApplyPersisted_LaysThePaletteOverTheFreeFormColours()
    {
        var service = new ThemeService();
        var theme = new AppearanceTheme(service);

        theme.ApplyPersisted(
            new Dictionary<string, string>
            {
                [CustomToken] = ThemeOverrides.ToHex(CustomColor),
                [ThemeTokens.String1] = "#FF010203"
            },
            "ibm");

        Assert.Equal(CustomColor, service.GetColor(CustomToken));
        Assert.Equal(Color.Parse(StringColorPresets.GetOverrides("ibm")[ThemeTokens.String1]),
            service.GetColor(ThemeTokens.String1));
    }

    /// <summary>A colour crosses the seam as a readout string, and a bad one changes nothing.</summary>
    [Fact]
    public void Hex_RoundTripsThroughTheSeam_AndGarbageIsIgnored()
    {
        var service = new ThemeService();
        var theme = new AppearanceTheme(service);

        theme.SetHex(CustomToken, "#102030");
        Assert.Equal("#102030", theme.GetHex(CustomToken));
        theme.SetHex(CustomToken, "#80102030");
        Assert.Equal("#80102030", theme.GetHex(CustomToken));
        theme.SetHex(CustomToken, "not a colour");
        Assert.Equal("#80102030", theme.GetHex(CustomToken));
    }
}
