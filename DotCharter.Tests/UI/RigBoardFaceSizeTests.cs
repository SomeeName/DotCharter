using DotCharter.Tone.UI.Controls.Dsp;
using DotCharter.Tone.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI;

/// <summary>
///     The chain shows every unit as its own gear, as tall as the board allows up
///     to a thumbnail ceiling, and as the board's width allows. The arithmetic is pure, so it is
///     pinned here; the layout itself is <c>Perf/RigBoardFaceplateUiPerfSmokeTests</c>.
/// </summary>
public sealed class RigBoardFaceSizeTests
{
    [Theory]
    [InlineData(1200, 1, RigBoardFaceSize.Max)] // a tall board: capped, the chain is an overview
    [InlineData(220, 1, 220 - RigBoardFaceSize.BoardChrome)]
    [InlineData(360, 2, (360 - RigBoardFaceSize.BoardChrome - RigBoardFaceSize.LaneChrome) / 2)]
    [InlineData(300, 3, RigBoardFaceSize.Min)] // too short for three lanes: the floor, and the zoom fits it
    [InlineData(0, 1, RigBoardFaceSize.Min)]
    [InlineData(double.NaN, 1, RigBoardFaceSize.Min)]
    public void TheFace_TakesTheBoardsHeight_SharedBetweenLanes(double board, int lanes, double expected)
        => Assert.Equal(Math.Round(expected), RigBoardFaceSize.For(board, lanes));

    /// <summary>A path is fixed jacks and wires plus Σ aspect × face height. Shrinking
    /// by the fit converges on the height that fills the room, from above, without
    /// ever overshooting — and grows back the same way when the room widens.</summary>
    [Fact]
    public void TheWidthLimit_ConvergesOnTheRoom_FromEitherSide()
    {
        const double fixedWidth = 300, aspectSum = 6;
        static double PathAt(double h) => fixedWidth + aspectSum * h;

        var room = 1500.0;
        var limit = double.PositiveInfinity;
        var height = 400.0; // what the board's height alone would give
        for (var pass = 0; pass < 12; pass++)
        {
            var before = height;
            limit = RigBoardFaceSize.NextWidthLimit(height, room / PathAt(height), limit);
            height = Math.Min(400, limit);
            Assert.True(height <= before, $"pass {pass}: shrinking went back up ({before} → {height})");
            Assert.True(height >= (room - fixedWidth) / aspectSum * 0.97, $"pass {pass}: overshot to {height}");
        }

        // within the 2 % the board's transform absorbs, never past it
        var fits = (room - fixedWidth) / aspectSum; // 200
        Assert.InRange(height, fits * 0.97, (room / 0.98 - fixedWidth) / aspectSum);

        room = 2400; // the window widened: the faces grow back, and never past the room
        for (var pass = 0; pass < 12; pass++)
        {
            limit = RigBoardFaceSize.NextWidthLimit(height, room / PathAt(height), limit);
            height = Math.Min(400, limit);
            Assert.True(PathAt(height) <= room + 1, $"pass {pass}: grew past the room");
        }

        Assert.InRange(height, 340, 350); // (2400 - 300) / 6
    }

    [Fact]
    public void TheWidthLimit_NeverTakesAFaceBelowTheFloor_AndIgnoresAnUnlaidBoard()
    {
        Assert.Equal(RigBoardFaceSize.Min, RigBoardFaceSize.NextWidthLimit(130, 0.1, double.PositiveInfinity));
        Assert.Equal(double.PositiveInfinity, RigBoardFaceSize.NextWidthLimit(300, 1.0, double.PositiveInfinity));
        Assert.Equal(250, RigBoardFaceSize.NextWidthLimit(300, -0.1, 250)); // a board of zero width says nothing
    }

    /// <summary>A unit with a faceplate is edited on the board, so the dock opens the
    /// unit inspector (the pedal view) for it whatever its art family; the cabinet
    /// and routing consoles stay, and a module with no face keeps its family view.</summary>
    [Fact]
    public void EveryUnitWithAFace_OpensTheInspector_ExceptTheCabinetAndRoutingConsoles()
    {
        foreach (var id in new[] { "namamp", "amp", "compressor", "graphiceq", "eq", "wah", "miku", "rat" })
        {
            Assert.Equal(RigEditorFamily.Pedal, RigDockUnit.EditorFamilyFor(id, hasFaceplate: true));
            Assert.Equal(RigArtwork.FamilyFor(id), RigDockUnit.EditorFamilyFor(id, hasFaceplate: false));
        }

        // the mic unit opens its mic placement (the speaker, the mic on it, its distance)
        Assert.Equal(RigEditorFamily.MicPlacement, RigDockUnit.EditorFamilyFor("micpos", hasFaceplate: true));
        Assert.Equal(RigEditorFamily.Cabinet, RigDockUnit.EditorFamilyFor("cab", hasFaceplate: true));
        Assert.Equal(RigEditorFamily.Routing, RigDockUnit.EditorFamilyFor("splitter", hasFaceplate: true));
    }
}
