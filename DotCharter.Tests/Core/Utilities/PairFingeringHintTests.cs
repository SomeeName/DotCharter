using System.Reflection;

namespace DotCharter.Tests.Core.Utilities;

/// <summary>
///     #511: the arrangement's 1·3 / 1·4 choice reaches <see cref="ChordTemplateFingerSetter" />
///     as an INPUT, so the setter stays a pure function — of the shape and that
///     hint, with no arrangement, no tuning and no state of its own.
/// </summary>
public sealed class PairFingeringHintTests
{
    private static ChordTemplate Suggested(PairFingering? hint, params (int String, int Fret)[] frets)
    {
        var t = new ChordTemplate();
        foreach (var (s, f) in frets) t.Frets[s] = f;
        ChordTemplateFingerSetter.SetSuggestedFingers(t, 6, hint);
        return t;
    }

    [Theory]
    [InlineData(PairFingering.IndexPinky, 4)]
    [InlineData(PairFingering.IndexRing, 3)]
    public void TheHintChoosesTheUpperFingerOfATwoFretPair(PairFingering hint, int upper)
    {
        Assert.Equal(new Dictionary<int, int> { [0] = 1, [1] = upper }, Suggested(hint, (0, 5), (1, 7)).Fingers);
        Assert.Equal(new Dictionary<int, int> { [1] = 1, [3] = upper }, Suggested(hint, (1, 5), (3, 7)).Fingers);
    }

    [Fact]
    public void NoHintIsTheSuggestionItself()
    {
        foreach (var frets in new[] { new[] { (0, 5), (1, 7) }, [(0, 3), (2, 5)], [(2, 7), (3, 9), (4, 9)] })
        {
            var plain = new ChordTemplate();
            foreach (var (s, f) in frets) plain.Frets[s] = f;
            ChordTemplateFingerSetter.SetSuggestedFingers(plain, 6);
            Assert.Equal(plain.Fingers, Suggested(null, frets).Fingers);
        }
    }

    [Fact]
    public void AnythingButATwoFretPairIgnoresTheHint()
    {
        (int, int)[][] shapes =
        [
            [(3, 5), (4, 8)], // three frets apart
            [(0, 5), (1, 7), (2, 7)], // three strings
            [(0, 5), (1, 5)], // same fret
            [(0, 0), (1, 2)], // an open root
        ];
        foreach (var frets in shapes)
            Assert.Equal(Suggested(null, frets).Fingers, Suggested(PairFingering.IndexPinky, frets).Fingers);
    }

    [Fact]
    public void TheSetterTakesNoArrangementAndKeepsNoState()
    {
        var type = typeof(ChordTemplateFingerSetter);
        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
        Assert.DoesNotContain(methods, m => m.GetParameters().Any(p => p.ParameterType == typeof(Arrangement)));
        var fields = type.GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
        Assert.All(fields, f => Assert.True(f.IsInitOnly || f.IsLiteral, $"{f.Name} is mutable static state"));
    }
}
