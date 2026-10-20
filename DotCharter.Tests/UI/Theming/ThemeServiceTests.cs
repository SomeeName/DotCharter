using Avalonia.Media;
using DotCharter.UI.Theming;

namespace DotCharter.Tests.UI.Theming;

/// <summary>
/// Tests for <see cref="ThemeService"/> covering preset application,
/// per-token overrides, completeness fallback, and event raising.
/// These run without an Avalonia Application, which means the UI-thread
/// resource push is a no-op — exactly the path we want to exercise here
/// (the in-memory dictionary is the source of truth that DynamicResource
/// consumers read once a real Application is running).
/// </summary>
public sealed class ThemeServiceTests
{
    [Fact]
    public void Default_preset_loaded_in_constructor()
    {
        var svc = new ThemeService();
        Assert.Same(ThemePresets.Default, svc.CurrentPreset);
        foreach (var key in ThemeTokens.All)
            Assert.True(svc.CurrentColors.ContainsKey(key), $"missing token: {key}");
    }

    [Fact]
    public void ApplyPreset_swaps_palette_and_keeps_every_registered_token()
    {
        var svc = new ThemeService();
        var target = ThemePresets.Default;
        svc.ApplyPreset(target);

        Assert.Same(target, svc.CurrentPreset);
        foreach (var key in ThemeTokens.All)
        {
            Assert.True(svc.CurrentColors.ContainsKey(key),
                $"After ApplyPreset, token '{key}' was not present");
        }

        Assert.Equal(target.Colors[ThemeTokens.Accent], svc.GetColor(ThemeTokens.Accent));
    }

    [Fact]
    public void ApplyPreset_with_override_applies_user_color()
    {
        var svc = new ThemeService();
        svc.ApplyPreset(ThemePresets.Default,
            new Dictionary<string, string> { [ThemeTokens.Accent] = "#FF00AA55" });

        Assert.Equal(Color.Parse("#FF00AA55"), svc.GetColor(ThemeTokens.Accent));
    }

    [Fact]
    public void ApplyPreset_ignores_invalid_override_hex()
    {
        var svc = new ThemeService();
        var originalAccent = ThemePresets.Default.Colors[ThemeTokens.Accent];
        svc.ApplyPreset(ThemePresets.Default,
            new Dictionary<string, string> { [ThemeTokens.Accent] = "not-a-color" });

        Assert.Equal(originalAccent, svc.GetColor(ThemeTokens.Accent));
    }

    [Fact]
    public void ApplyPreset_falls_back_to_default_for_missing_token()
    {
        // A hand-rolled minimal preset that omits most tokens — ApplyPreset
        // must backfill from the default so DynamicResource lookups never
        // resolve to the magenta sentinel.
        var sparse = new ThemePreset("sparse", "Sparse",
            new Dictionary<string, Color>
            {
                [ThemeTokens.Accent] = Colors.HotPink
            });

        var svc = new ThemeService();
        svc.ApplyPreset(sparse);

        Assert.Equal(Colors.HotPink, svc.GetColor(ThemeTokens.Accent));
        // Backfilled from Default
        Assert.Equal(
            ThemePresets.Default.Colors[ThemeTokens.AppBackground],
            svc.GetColor(ThemeTokens.AppBackground));

        foreach (var key in ThemeTokens.All)
        {
            Assert.True(svc.CurrentColors.ContainsKey(key),
                $"missing token after sparse ApplyPreset: {key}");
        }
    }

    [Fact]
    public void SetColor_updates_only_the_target_token_and_fires_event()
    {
        var svc = new ThemeService();
        var fired = 0;
        svc.ThemeChanged += () => fired++;

        var newAccent = Color.FromArgb(0xFF, 0x12, 0x34, 0x56);
        svc.SetColor(ThemeTokens.Accent, newAccent);

        Assert.Equal(newAccent, svc.GetColor(ThemeTokens.Accent));
        Assert.Equal(1, fired);

        // Other tokens unaffected.
        Assert.Equal(
            ThemePresets.Default.Colors[ThemeTokens.PanelBackground],
            svc.GetColor(ThemeTokens.PanelBackground));
    }

    [Fact]
    public void ApplyPreset_raises_ThemeChanged_once()
    {
        var svc = new ThemeService();
        var fired = 0;
        svc.ThemeChanged += () => fired++;

        svc.ApplyPreset(ThemePresets.Default);
        Assert.Equal(1, fired);
    }

    /// <summary>
    ///     Re-applying a preset DROPS every per-token override taken since it was
    ///     applied — the mechanism Settings ▸ Appearance ▸ "Reset to preset"
    ///     stands on (<c>AppearanceSettingsViewModel.ResetColorsCommand</c> →
    ///     <c>AppearanceTheme.ResetColors</c>). Replaces the old
    ///     <c>ResetToPreset_re_applies_current_preset_dropping_overrides</c>,
    ///     which named an <c>IThemeService</c> member no product code called and
    ///     that was deleted in 0.60: a bare re-apply of <c>CurrentPreset</c>
    ///     would drop the colour-blind-safe string-colour preset layered over the
    ///     <c>String*</c> tokens, which is why the live path passes the overrides
    ///     it wants to KEEP instead.
    /// </summary>
    [Fact]
    public void ApplyPreset_drops_per_token_overrides_taken_since_it_was_applied()
    {
        var svc = new ThemeService();
        svc.SetColor(ThemeTokens.Accent, Colors.Magenta);
        Assert.Equal(Colors.Magenta, svc.GetColor(ThemeTokens.Accent));

        svc.ApplyPreset(svc.CurrentPreset);

        Assert.Equal(
            ThemePresets.Default.Colors[ThemeTokens.Accent],
            svc.GetColor(ThemeTokens.Accent));
    }

    // The other half — that the overrides it is HANDED survive the re-apply, which
    // is exactly what the deleted ResetToPreset could not do — is already
    // ApplyPreset_with_override_applies_user_color above.

    [Fact]
    public void GetColor_returns_magenta_sentinel_for_unknown_key()
    {
        var svc = new ThemeService();
        Assert.Equal(Colors.Magenta, svc.GetColor("ThisKeyDoesNotExist"));
    }
}
