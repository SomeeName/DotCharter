using System.Text.RegularExpressions;
using Avalonia.Media;
using DotCharter.UI.Theming;

namespace DotCharter.Tests.UI;

/// <summary>
///     Fluent's stock controls read <c>SystemAccentColor</c>, which defaults to
///     the Windows accent: the onboarding radio dots were Windows blue beside
///     every Graphite control. The first frame's values live in
///     <c>ColorTokens.axaml</c> and the theme service rewrites them from the
///     accent; the two must agree or the first frame differs from the second.
/// </summary>
public sealed class FluentAccentTests
{
    [Fact]
    public void TheStaticDefaults_AreWhatTheAccentDerives()
    {
        var tokens = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "DotCharter.Tone.UI", "Styles", "ColorTokens.axaml"));
        var accent = Color.Parse(Declared(tokens, "AccentColorValue"));

        var derived = FluentAccent.Derive(accent);
        Assert.Equal(7, derived.Count);
        foreach (var (key, colour) in derived)
            Assert.Equal(colour, Color.Parse(Declared(tokens, key)));
    }

    [Fact]
    public void TheFamilyIsTheAccent_ThenLighterAndDarkerSteps()
    {
        var accent = Color.Parse("#7AB6FF");
        var d = FluentAccent.Derive(accent);
        Assert.Equal(accent, d[FluentAccent.Key]);
        Assert.True(Luma(d[FluentAccent.Key + "Light1"]) > Luma(accent));
        Assert.True(Luma(d[FluentAccent.Key + "Light3"]) > Luma(d[FluentAccent.Key + "Light1"]));
        Assert.True(Luma(d[FluentAccent.Key + "Dark1"]) < Luma(accent));
        Assert.True(Luma(d[FluentAccent.Key + "Dark3"]) < Luma(d[FluentAccent.Key + "Dark1"]));
    }

    private static double Luma(Color c) => 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B;

    private static string Declared(string text, string key)
    {
        var m = Regex.Match(text, $@"<Color x:Key=""{Regex.Escape(key)}"">(?<v>[^<]+)</Color>");
        Assert.True(m.Success, $"{key} is not declared in ColorTokens.axaml");
        return m.Groups["v"].Value.Trim();
    }
}
