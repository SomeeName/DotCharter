using DotCharter.Presentation.Fakes.Dialogs;
using DotCharter.Presentation.Fakes.PlayMode;

namespace DotCharter.Tests.Presentation;

/// <summary>
///     The gallery is where a screen is judged before it is wired, so its fakes must read the
///     way the app would. Two did not: the Start screen's facts were captioned in capitals
///     ("LIBRARY") where every other caption is sentence case, and the Tone designer headed a
///     slot "Lead" with a "Lead" chip the real view model leaves out.
/// </summary>
public sealed class FakeCopyTests
{
    [Fact]
    public void TheStartScreensFacts_AreCaptionedInSentenceCase()
    {
        var shouted = FakeStartScreen.Variants
            .SelectMany(v => v.Create().StatusFacts.Select(f => $"{v.Name}: {f.Caption}"))
            .Where(c => c.Split(": ")[1] is { Length: > 3 } caption && caption == caption.ToUpperInvariant())
            .ToList();
        Assert.True(shouted.Count == 0, "all-caps captions: " + string.Join(", ", shouted));
    }

    [Fact]
    public void TheToneDesigner_NeverChipsATypeTheNameAlreadySays()
    {
        var doubled = FakeToneDesignerScreen.Variants
            .SelectMany(v => v.Create().Arrangements.Select(a => (v.Name, a.Name, a.TypeLabel)))
            .Where(r => string.Equals(r.Item2.Trim(), r.TypeLabel, StringComparison.OrdinalIgnoreCase))
            .Select(r => $"{r.Item1}: {r.Item2}")
            .ToList();
        Assert.True(doubled.Count == 0, "name and type chip say the same: " + string.Join(", ", doubled));
    }

    /// <summary>A picker beside its row label lists values: the Showlights fake offered
    /// "Fog: Green" in the Fog row, which the real dialog (plain color names) never did.</summary>
    [Fact]
    public void TheShowlightsPickers_ListValuesNotTheirRowLabel()
    {
        var prefixed = FakeShowlightsScreen.Variants
            .SelectMany(v =>
            {
                var s = v.Create();
                return s.FogOptions.Concat(s.BeamsOptions).Concat(s.LasersOptions).Select(o => $"{v.Name}: {o}");
            })
            .Where(o => o.Contains("Fog:") || o.Contains("Beams:") || o.Contains("Lasers:"))
            .ToList();
        Assert.True(prefixed.Count == 0, "options that repeat their row: " + string.Join(", ", prefixed));
    }
}
