using System.Globalization;
using System.Xml.Linq;
using DotCharter.Services.Parsing.GuitarPro;

namespace DotCharter.Tests.Services.Parsing.GuitarPro;

/// <summary>
///     Guitar Pro stores every bend as origin, middle and destination. On a plain bend (0 → Full) or a plain
///     release the middle point lies on the straight line between the other two and adds nothing but a second label
///     ("↗1/2 ↗Full"), so the import drops it. A middle point OFF that line, or a held middle (two middle offsets), is a
///     real three-point curve and stays.
/// </summary>
public sealed class GpBendMiddlePointTests
{
    [Fact]
    public void APlainBend_DropsItsCollinearMiddlePoint() =>
        Assert.Equal([(0, 0.0), (30, 2.0)], Points(origin: (0, 0), middle: (15, 15, 50), destination: (30, 100)));

    [Fact]
    public void APlainRelease_DropsItsCollinearMiddlePoint() =>
        Assert.Equal([(0, 2.0), (60, 0.0)], Points(origin: (0, 100), middle: (30, 30, 50), destination: (60, 0)));

    [Fact]
    public void AMiddlePointOffTheLine_IsARealCurve_AndStays() =>
        Assert.Equal([(0, 0.0), (10, 1.0), (60, 2.0)], Points(origin: (0, 0), middle: (10, 10, 50), destination: (60, 100)));

    [Fact]
    public void AHeldMiddle_StaysAsTwoPoints() =>
        Assert.Equal([(0, 0.0), (20, 2.0), (40, 2.0), (60, 0.0)],
            Points(origin: (0, 0), middle: (20, 40, 100), destination: (60, 0)));

    /// <summary>GP keeps its default middle point (12, ½) when the destination comes first (8, Full) and does
    /// not draw it; kept, the curve dipped back to ½ after reaching Full.</summary>
    [Fact]
    public void AMiddlePointAfterTheDestination_IsNotDrawn_AndDropped() =>
        Assert.Equal([(0, 0.0), (8, 2.0)], Points(origin: (0, 0), middle: (12, 12, 50), destination: (8, 100)));

    [Fact]
    public void AMiddlePointBeforeTheOrigin_IsNotDrawn_AndDropped() =>
        Assert.Equal([(20, 0.0), (60, 2.0)], Points(origin: (20, 0), middle: (10, 10, 50), destination: (60, 100)));

    [Fact]
    public void ABendRelease_KeepsItsPeak() =>
        Assert.Equal([(0, 0.0), (30, 2.0), (60, 0.0)], Points(origin: (0, 0), middle: (30, 30, 100), destination: (60, 0)));

    [Fact]
    public void APrebendRelease_KeepsItsHold() =>
        Assert.Equal([(0, 2.0), (20, 2.0), (40, 2.0), (60, 0.0)],
            Points(origin: (0, 100), middle: (20, 40, 100), destination: (60, 0)));

    [Fact]
    public void APrebendHeld_IsFlat() =>
        Assert.Equal([(0, 2.0), (60, 2.0)], Points(origin: (0, 100), middle: (30, 30, 100), destination: (60, 100)));

    /// <summary>One bent note held a whole bar of 100 quarters' worth of percent: its points as (percent, half steps).</summary>
    private static (int Percent, double Step)[] Points((double Offset, double Value) origin,
        (double Offset1, double Offset2, double Value) middle, (double Offset, double Value) destination)
    {
        static XElement Prop(string name, double value) =>
            new("Property", new XAttribute("name", name),
                new XElement("Float", value.ToString(CultureInfo.InvariantCulture)));

        var node = new XElement("Note", new XElement("Properties",
            new XElement("Property", new XAttribute("name", "String"), new XElement("String", "0")),
            new XElement("Property", new XAttribute("name", "Fret"), new XElement("Fret", "7")),
            new XElement("Property", new XAttribute("name", "Bended"), new XElement("Enable")),
            Prop("BendOriginOffset", origin.Offset), Prop("BendOriginValue", origin.Value),
            Prop("BendMiddleOffset1", middle.Offset1), Prop("BendMiddleOffset2", middle.Offset2),
            Prop("BendMiddleValue", middle.Value),
            Prop("BendDestinationOffset", destination.Offset), Prop("BendDestinationValue", destination.Value)));

        var note = new GpNoteParser().ParseNote(node, FractionalPosition.Zero, new FractionalPosition(100), 6, false);
        return [.. note.BendValues.Select(b => ((int)Math.Round(b.Position.DoubleValue), b.Step))];
    }
}
