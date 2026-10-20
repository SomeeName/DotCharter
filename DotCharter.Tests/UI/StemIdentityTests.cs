using System.Xml.Linq;
using DotCharter.UI.Converters;

namespace DotCharter.Tests.UI;

/// <summary>
///     A separated stem's name → its Fire identity token.
///     <para>
///         The mapping is trivial; what is not is that every token it can return
///         must EXIST. A key that resolves to nothing paints no fill at all —
///         `FireTokenBrushConverter` returns null on purpose, so a missing
///         identity is an invisible fader rather than a plausible one — and no
///         other gate in the repo would see it.
///     </para>
/// </summary>
public sealed class StemIdentityTests
{
    [Theory]
    [InlineData("guitar", "FireStemGuitar")]
    [InlineData("Guitar", "FireStemGuitar")]
    [InlineData("  GUITARS ", "FireStemGuitar")]
    [InlineData("bass", "FireStemBass")]
    [InlineData("drums", "FireStemDrums")]
    [InlineData("vocals", "FireStemVocals")]
    [InlineData("piano", "FireStemPiano")]
    [InlineData("other", "FireStemOther")]
    public void EverySeparatorOutputHasItsOwnIdentity(string name, string expected) => Assert.Equal(expected, StemIdentity.TokenFor(name, isMasterMix: false));

    /// <summary>
    ///     A stem the user dropped into the project's own <c>stems/</c> folder
    ///     can be called anything. It takes the neutral slate — never another
    ///     instrument's colour, which would be a confident lie about what is on
    ///     the fader.
    /// </summary>
    [Theory]
    [InlineData("wurlitzer")]
    [InlineData("")]
    [InlineData(null)]
    public void AnUnknownStemTakesTheNeutralIdentity(string? name) => Assert.Equal(StemIdentity.OtherToken, StemIdentity.TokenFor(name, isMasterMix: false));

    /// <summary>
    ///     The master strip is identified by its FLAG. Its display name is the
    ///     literal "Original", and keying on that string would silently repaint
    ///     the row the day it is localized.
    /// </summary>
    [Fact]
    public void TheMasterMixIsKeyedOnItsFlagNotItsName()
    {
        Assert.Equal(StemIdentity.MixToken, StemIdentity.TokenFor("Original", isMasterMix: true));
        Assert.Equal(StemIdentity.MixToken, StemIdentity.TokenFor("guitar", isMasterMix: true));
        Assert.Equal(StemIdentity.OtherToken, StemIdentity.TokenFor("Original", isMasterMix: false));
    }

    [Fact]
    public void EveryIdentityTokenIsDeclaredInFireTokens()
    {
        var declared = FireTokenKeys();
        Assert.Contains("FireAccent", declared); // the scan must not pass by finding nothing

        var used = new[] { "guitar", "bass", "drums", "vocals", "piano", "other", "wurlitzer" }
            .Select(n => StemIdentity.TokenFor(n, isMasterMix: false))
            .Append(StemIdentity.TokenFor("Original", isMasterMix: true))
            .Distinct(StringComparer.Ordinal);

        foreach (var key in used)
        {
            Assert.True(declared.Contains(key),
                $"'{key}' is not declared in FireTokens.axaml — the fader would paint nothing");
        }
    }

    /// <summary>The seven identities must be seven DISTINCT colours, for the
    /// same reason the six strings must be: two stems the same colour are two
    /// faders you cannot tell apart while soloing.</summary>
    [Fact]
    public void TheStemIdentitiesAreDistinctColours()
    {
        var values = FireTokenColours();
        var keys = new[]
        {
            "FireStemGuitar", "FireStemBass", "FireStemDrums", "FireStemVocals",
            "FireStemPiano", "FireStemOther", "FireStemMix"
        };

        var colours = keys.Select(k => values[k]).ToList();
        Assert.Equal(keys.Length, colours.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    private static HashSet<string> FireTokenKeys() =>
        FireTokenColours().Keys.ToHashSet(StringComparer.Ordinal);

    private static Dictionary<string, string> FireTokenColours()
    {
        var path = Path.Combine(TestPaths.RepoRoot(), "DotCharter.Tone.UI", "Styles", "FireTokens.axaml");
        Assert.True(File.Exists(path), $"FireTokens.axaml moved — this guard is blind ({path})");

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        // A CONTAINER over `Styles/FireTokens/` — read it WITH its parts.
        foreach (var e in StyleFileText.ElementsWithIncludes(path))
        {
            var key = (string?)e.Attribute(XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml"));
            if (key == null) continue;
            map[key] = (string?)e.Attribute("Color") ?? e.Value;
        }

        Assert.True(map.Count > 100, $"only {map.Count} Fire tokens parsed — the guard is looking at the wrong file");
        return map;
    }
}
