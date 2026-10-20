using System.Globalization;
using Avalonia.Data;
using Avalonia.Media;
using DotCharter.UI.Converters;

namespace DotCharter.Tests.UI;

/// <summary>
///     The Appearance tab's colour pickers bind the contract's hex strings
///     through <see cref="HexColorConverter" />, two-way. A colour must survive
///     the trip in both directions, alpha included, and a string that is not a
///     colour must convert to NOTHING rather than to black.
/// </summary>
public sealed class HexColorConverterTests
{
    private static readonly HexColorConverter Converter = HexColorConverter.Instance;

    [Theory]
    [InlineData("#102030")]
    [InlineData("#80102030")]
    [InlineData("#FFFFFF")]
    [InlineData("#00000000")]
    public void AReadout_RoundTripsThroughAColour(string hex)
    {
        var color = Converter.Convert(hex, typeof(Color), null, CultureInfo.InvariantCulture);

        Assert.IsType<Color>(color);
        Assert.Equal(hex, Converter.ConvertBack(color, typeof(string), null, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void AnOpaqueColour_DropsItsAlphaPair_AndATranslucentOneKeepsIt()
    {
        Assert.Equal("#0A0B0C", Converter.ConvertBack(Color.FromRgb(10, 11, 12), typeof(string), null, CultureInfo.InvariantCulture));
        Assert.Equal("#7F0A0B0C",
            Converter.ConvertBack(Color.FromArgb(0x7F, 10, 11, 12), typeof(string), null, CultureInfo.InvariantCulture));
    }

    /// <summary>A picker must never be driven to black by a value it cannot read.</summary>
    [Theory]
    [InlineData("not a colour")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData(42)]
    public void Garbage_ConvertsToNothing(object? value)
    {
        Assert.Same(BindingOperations.DoNothing, Converter.Convert(value, typeof(Color), null, CultureInfo.InvariantCulture));
        Assert.Same(BindingOperations.DoNothing, Converter.ConvertBack(value, typeof(string), null, CultureInfo.InvariantCulture));
    }
}
