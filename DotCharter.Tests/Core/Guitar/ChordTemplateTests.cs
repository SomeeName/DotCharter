namespace DotCharter.Tests.Core.Guitar;

public class ChordTemplateTests
{
    private static ChordTemplate BuildCmajor()
    {
        return new ChordTemplate
        {
            Name = "C",
            Frets = new Dictionary<int, int> { [0] = 0, [1] = 3, [2] = 2, [3] = 0, [4] = 1, [5] = 0 },
            Fingers = new Dictionary<int, int> { [1] = 3, [2] = 2, [4] = 1 }
        };
    }

    [Fact]
    public void GetLowestAndHighestString_WithFrets()
    {
        var t = BuildCmajor();
        Assert.Equal(0, t.GetLowestString());
        Assert.Equal(5, t.GetHighestString());
    }

    [Fact]
    public void GetLowestNotOpenFret_SkipsOpenStrings()
    {
        var t = BuildCmajor();
        Assert.Equal(1, t.GetLowestNotOpenFret());
    }

    [Fact]
    public void GetLowestNotOpenFret_RespectsCapo()
    {
        var t = BuildCmajor();
        Assert.Equal(2, t.GetLowestNotOpenFret(1));
    }

    [Fact]
    public void GetFret_MissingString_ReturnsNegativeOne()
    {
        var t = new ChordTemplate { Frets = new Dictionary<int, int> { [1] = 3 } };
        Assert.Equal(-1, t.GetFret(0));
        Assert.Equal(3, t.GetFret(1));
    }

    [Fact]
    public void GetTemplateFrets_AllLowFrets_NoSpaces()
    {
        var t = BuildCmajor();
        Assert.Equal("032010", t.GetTemplateFrets());
    }

    [Fact]
    public void GetTemplateFrets_WithDoubleDigitFret_InsertsSpaces()
    {
        var t = new ChordTemplate { Frets = new Dictionary<int, int> { [0] = 10, [1] = 12, [2] = 12 } };
        var s = t.GetTemplateFrets(3);
        Assert.Contains(" ", s);
        Assert.Contains("10", s);
        Assert.Contains("12", s);
    }

    [Fact]
    public void GetTemplateFingers_MissingFingers_RenderedAsSpace()
    {
        var t = new ChordTemplate { Fingers = new Dictionary<int, int> { [0] = 1, [2] = 3 } };
        var s = t.GetTemplateFingers(4);
        Assert.Equal("1 3 ", s);
    }

    [Fact]
    public void GetDisplayName_Arpeggio_AppendsSuffix()
    {
        var t = new ChordTemplate { Name = "C", Arpeggio = true };
        Assert.Equal("C(arp)", t.GetDisplayName());
    }

    [Fact]
    public void Clone_DeepCopiesDictionaries()
    {
        var t = BuildCmajor();
        var clone = t.Clone();
        clone.Frets[0] = 99;
        clone.Fingers[0] = 4;
        Assert.Equal(0, t.Frets[0]);
        Assert.False(t.Fingers.ContainsKey(0));
    }

    [Fact]
    public void Equals_IdenticalTemplates_AreEqual()
    {
        Assert.Equal(BuildCmajor(), BuildCmajor());
        Assert.Equal(BuildCmajor().GetHashCode(), BuildCmajor().GetHashCode());
    }

    [Fact]
    public void Equals_DifferentArpeggio_AreUnequal()
    {
        var a = BuildCmajor();
        var b = BuildCmajor();
        b.Arpeggio = true;
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Equals_DifferentFret_AreUnequal()
    {
        var a = BuildCmajor();
        var b = BuildCmajor();
        b.Frets[1] = 5;
        Assert.NotEqual(a, b);
    }
}
