using System.Reflection;
using DotCharter.UI.Input.Tools;

namespace DotCharter.Tests.UI.Input;

/// <summary>A rescaled imported bend remains valid when the highway preview moves it to an integer beat.</summary>
public sealed class BendRescaleConsumerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RescaledImportedBendsRemainDraggable(bool fallback)
    {
        var start = new FractionalPosition(4, new Fraction(1, 65521));
        var oldSustain = fallback ? new FractionalPosition(1, new Fraction(1, 65519)) : new FractionalPosition(1);
        var note = new GuitarNote
        {
            Position = start, Sustain = new FractionalPosition(2),
            BendValues = { new BendValue(start + new Fraction(1, 4), 2) }
        };
        var expected = 0.5 / oldSustain.DoubleValue;
        BendMath.RescaleToSustain(note.BendValues, start, oldSustain, note.Sustain);
        var target = new FractionalPosition(8);
        var move = typeof(HighwayInteractionTool).GetMethod("MoveNotePreview", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(move);

        move.Invoke(null, [note, target]);

        Assert.Equal(target, note.Position);
        Assert.InRange(Math.Abs((Assert.Single(note.BendValues).Position - target).DoubleValue - expected), 0, 0.000001);
    }
}
