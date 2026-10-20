using Avalonia.Input;

namespace DotCharter.Tests.Services.Configuration;

public class KeyChordTests
{
    [Fact]
    public void Default_None_IsUnbound()
    {
        Assert.False(KeyChord.None.IsBound);
        Assert.Equal("—", KeyChord.None.ToString());
    }

    [Fact]
    public void Matches_AllFlagsMustAlign()
    {
        var chord = new KeyChord(Key.S, KeyModifiers.Control | KeyModifiers.Shift);

        Assert.True(chord.Matches(Key.S, ctrl: true, shift: true, alt: false));
        Assert.False(chord.Matches(Key.S, ctrl: false, shift: true, alt: false));
        Assert.False(chord.Matches(Key.S, ctrl: true, shift: false, alt: false));
        Assert.False(chord.Matches(Key.S, ctrl: true, shift: true, alt: true));
        Assert.False(chord.Matches(Key.A, ctrl: true, shift: true, alt: false));
    }

    [Theory]
    [InlineData(Key.D0, "0")]
    [InlineData(Key.D9, "9")]
    [InlineData(Key.OemPlus, "=")]
    [InlineData(Key.OemMinus, "-")]
    [InlineData(Key.OemComma, ",")]
    [InlineData(Key.OemPeriod, ".")]
    [InlineData(Key.OemOpenBrackets, "[")]
    [InlineData(Key.OemCloseBrackets, "]")]
    [InlineData(Key.OemSemicolon, ";")]
    [InlineData(Key.OemQuotes, "'")]
    [InlineData(Key.OemBackslash, "\\")]
    [InlineData(Key.OemPipe, "\\")]
    [InlineData(Key.OemQuestion, "/")]
    [InlineData(Key.OemTilde, "`")]
    [InlineData(Key.Add, "Num+")]
    [InlineData(Key.Subtract, "Num-")]
    [InlineData(Key.Multiply, "Num*")]
    [InlineData(Key.Divide, "Num/")]
    public void ToString_FormatsSpecialKeys(Key k, string expected) => Assert.Equal(expected, new KeyChord(k).ToString());

    [Fact]
    public void ToString_OrdersModifiers_CtrlAltShift()
    {
        var chord = new KeyChord(Key.S, KeyModifiers.Shift | KeyModifiers.Alt | KeyModifiers.Control);
        Assert.Equal("Ctrl+Alt+Shift+S", chord.ToString());
    }

    [Theory]
    [InlineData(null, Key.None, KeyModifiers.None)]
    [InlineData("", Key.None, KeyModifiers.None)]
    [InlineData("   ", Key.None, KeyModifiers.None)]
    [InlineData("None", Key.None, KeyModifiers.None)]
    [InlineData("none", Key.None, KeyModifiers.None)]
    [InlineData("S", Key.S, KeyModifiers.None)]
    [InlineData("Ctrl+S", Key.S, KeyModifiers.Control)]
    [InlineData("control+s", Key.S, KeyModifiers.Control)]
    [InlineData("Ctrl+Shift+Z", Key.Z, KeyModifiers.Control | KeyModifiers.Shift)]
    [InlineData("Alt+F4", Key.F4, KeyModifiers.Alt)]
    [InlineData("Ctrl+Alt+Shift+F1", Key.F1, KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift)]
    public void Parse_AcceptsVariousFormats(string? raw, Key expectedKey, KeyModifiers expectedMods)
    {
        var chord = KeyChordJsonConverter.Parse(raw);
        Assert.Equal(expectedKey, chord.Key);
        Assert.Equal(expectedMods, chord.Modifiers);
    }

    [Fact]
    public void Parse_UnknownTokens_AreIgnored()
    {
        var chord = KeyChordJsonConverter.Parse("Ctrl+Blah+Z");
        Assert.Equal(Key.Z, chord.Key);
        Assert.Equal(KeyModifiers.Control, chord.Modifiers);
    }

    [Fact]
    public void Serialize_None_ReturnsLiteralNone() => Assert.Equal("None", KeyChordJsonConverter.Serialize(KeyChord.None));

    [Fact]
    public void Serialize_RoundtripsThroughParse()
    {
        var original = new KeyChord(Key.S, KeyModifiers.Control | KeyModifiers.Shift);
        var serialized = KeyChordJsonConverter.Serialize(original);
        var parsed = KeyChordJsonConverter.Parse(serialized);
        Assert.Equal(original, parsed);
    }
}
