using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.UI.Controls.Dsp;

namespace DotCharter.Tests.UI;

/// <summary>
///     The rig's artwork catalogue (<see cref="RigArtwork" />) against the
///     NATIVE module catalogue and the shipped asset files. A module whose
///     illustration is missing draws the utility fallback and reads as "some
///     grey box", and a file name typo is a blank image with no exception —
///     both are only ever caught here.
/// </summary>
public sealed class RigArtworkTests
{
    private static string ArtDir() => Path.Combine(TestPaths.RepoRoot(), "DotCharter.Tone.UI", "Assets", "Rig", "Art");

    [Fact]
    public void EveryNativeModule_HasItsOwnIllustration()
    {
        Assert.NotEmpty(DcRigTargetCatalog.ModuleTypeIds);
        var missing = DcRigTargetCatalog.ModuleTypeIds.Where(id => !RigArtwork.HasOwnIllustration(id)).ToList();
        Assert.True(missing.Count == 0, "native modules without artwork: " + string.Join(", ", missing));
    }

    [Fact]
    public void EveryIllustration_NamesAFileThatShips()
    {
        foreach (var id in RigArtwork.IllustratedIds)
        {
            var file = Path.Combine(ArtDir(), RigArtwork.IllustrationFor(id));
            Assert.True(File.Exists(file), $"{id}: {file} does not exist");
        }

        foreach (var file in new[]
                 {
                     RigArtwork.FallbackIllustration, RigArtwork.KnobCap, RigArtwork.PowderCoat,
                     RigArtwork.BrushedAluminium, RigArtwork.ChassisScrew, RigArtwork.JackSocket,
                     RigArtwork.FootswitchUp, RigArtwork.FootswitchDown, RigArtwork.RotaryGrille,
                     RigArtwork.WovenGrille, RigArtwork.BlackTolex, RigArtwork.NamShell, RigArtwork.LegacyShell
                 })
        {
            Assert.True(File.Exists(Path.Combine(ArtDir(), file)), file + " does not ship");
        }
    }

    [Fact]
    public void AnUnknownModule_FallsBackToTheUtilitySprite()
    {
        Assert.Equal(RigArtwork.FallbackIllustration, RigArtwork.IllustrationFor("no-such-module"));
        Assert.Equal(RigEditorFamily.Pedal, RigArtwork.FamilyFor("no-such-module"));
    }

    [Theory]
    [InlineData("amp", RigEditorFamily.AmpFace)]
    [InlineData("namamp", RigEditorFamily.AmpFace)]
    [InlineData("cab", RigEditorFamily.Cabinet)]
    [InlineData("splitter", RigEditorFamily.Routing)]
    [InlineData("merger", RigEditorFamily.Routing)]
    [InlineData("graphiceq", RigEditorFamily.GraphicEq)]
    [InlineData("eq", RigEditorFamily.ParametricEq)]
    [InlineData("wah", RigEditorFamily.Expression)]
    [InlineData("whammy", RigEditorFamily.Expression)]
    [InlineData("swell", RigEditorFamily.Expression)]
    [InlineData("miku", RigEditorFamily.Vocal)]
    [InlineData("mikustomp", RigEditorFamily.Vocal)]
    [InlineData("compressor", RigEditorFamily.Dynamics)]
    [InlineData("gate", RigEditorFamily.Dynamics)]
    [InlineData("noisereducer", RigEditorFamily.Dynamics)]
    [InlineData("overdrive", RigEditorFamily.Pedal)]
    [InlineData("delay", RigEditorFamily.Pedal)]
    public void TheEditorFamily_FollowsTheDesignsDispatch(string id, RigEditorFamily family)
        => Assert.Equal(family, RigArtwork.FamilyFor(id));

    [Theory]
    [InlineData(1, 2)]
    [InlineData(4, 2)]
    [InlineData(5, 3)]
    [InlineData(6, 3)]
    [InlineData(7, 4)]
    [InlineData(8, 4)]
    [InlineData(9, 5)]
    [InlineData(13, 5)]
    public void TheEnclosureGrid_MirrorsTheArtwork(int controls, int columns)
        => Assert.Equal(columns, RigArtwork.EnclosureColumns(controls));

    [Fact]
    public void FitScale_GrowsToTheCeiling_AndNeverShrinksBelowTheLegibilityFloor()
    {
        // Room to spare: grow into it (the 8 px breathing margin comes off first).
        Assert.InRange(RigArtwork.FitScale(300, 400), 1.3, 1.31);
        Assert.Equal(RigArtwork.MaxEditorScale, RigArtwork.FitScale(100, 4000));
        Assert.InRange(RigArtwork.FitScale(400, 400), 0.97, 0.99);
        Assert.InRange(RigArtwork.FitScale(500, 400), 0.75, 0.8);
        Assert.Equal(RigArtwork.MinEditorScale, RigArtwork.FitScale(4000, 400));
    }

    [Fact]
    public void ThePedalFinish_IsTheDesignsForKnownPedals_AndTheCategoryAccentOtherwise()
    {
        var overdrive = RigArtwork.CoatFor("overdrive");
        Assert.Equal((0x20, 0x4d, 0x3b), (overdrive.R, overdrive.G, overdrive.B));
        var accent = RigArtwork.AccentFor("tremolo");
        Assert.Equal((0xf7, 0xa3, 0xd7), (accent.R, accent.G, accent.B));
        Assert.Equal(DspModuleVisuals.AccentColor("wah"), RigArtwork.AccentFor("wah"));
        Assert.Equal(RigArtwork.CoatFor("wah"), RigArtwork.CoatFor("no-such-module"));
    }
}
