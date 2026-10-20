using System.Xml.Linq;

namespace DotCharter.Tests.UI;

/// <summary>
///     The gear browser's tile (the AmpliTube layout, 2026-09-27): the faceplate preview on
///     its plate with the star, the + and the IN RIG chip over the plate's corners, the name
///     UNDER the plate. The IN RIG chip and the add button shared one grid column until
///     2026-09-08, and since a module can be in the rig AND addable again, the + drew over the
///     chip's first letter (the owner's "the overlap still exists"); then the chip took the
///     name's line and left the name about 80 px ("Neural A…", 2026-09-24). On a tile the chip
///     and the + sit in OPPOSITE corners of the plate (bottom / top) and the name is outside it,
///     so the chip can meet neither. Pinned on the markup here, and measured by
///     <c>Perf/RigAmpliTubeLayoutUiPerfSmokeTests</c>.
/// </summary>
public sealed class RigGearRowTests
{
    [Fact]
    public void TheInRigChip_AndTheAddButton_SitInOppositeCornersOfThePlate_UnderWhichTheNameSits()
    {
        var root = TestPaths.RepoRoot();
        var doc = XDocument.Load(Path.Combine(root, "DotCharter.Tone.UI", "Views", "PlayMode", "RigGearTileTemplate.axaml"));
        var tile = Assert.Single(doc.Descendants(), e =>
            e.Name.LocalName == "DataTemplate"
            && e.Attributes().Any(a => a.Name.LocalName == "Key" && a.Value == "RigGearTileTemplate"));
        var add = Assert.Single(tile.Descendants(), e =>
            e.Name.LocalName == "Button" && (e.Attribute("Classes")?.Value ?? "").Contains("FireGearAdd"));
        var chip = Assert.Single(tile.Descendants(), e => e.Attribute("IsVisible")?.Value == "{Binding IsInRig}");

        // both ride the plate, as its overlays…
        var plate = add.Parent!;
        Assert.Contains("FireGearTileArt", plate.Attribute("Classes")?.Value ?? "");
        Assert.Same(plate, chip.Parent);
        // …and the name is the plate's SIBLING, under it — never on it
        var name = Assert.Single(tile.Descendants(), e => (e.Attribute("Classes")?.Value ?? "").Contains("FireGearTileName"));
        Assert.Same(plate.Parent, name.Parent);
        Assert.Contains("DisplayName", name.Attribute("Text")?.Value ?? "");

        // the corners are the STYLES': the + at the top (its ControlTheme in
        // FireRigUnitsAndGear.axaml, #240), the chip at the bottom (the FireTagBorder
        // theme it wears with FireTag, FireMarksAndMeters.axaml, #242)
        static string Setter(string file, string selector, string property) => XDocument
            .Load(Path.Combine(TestPaths.RepoRoot(), "DotCharter.Tone.UI", "Styles", "Fire", file)).Descendants()
            .Single(e => e.Name.LocalName == "Style" && e.Attribute("Selector")?.Value == selector)
            .Elements().Single(e => e.Attribute("Property")?.Value == property).Attribute("Value")!.Value;
        Assert.Contains("FireGearTileAdd", add.Attribute("Classes")!.Value);
        Assert.Contains("FireGearTileBadge", chip.Attribute("Classes")!.Value);
        Assert.Equal("Top", Setter("FireRigUnitsAndGear.axaml", "^.FireGearTileAdd", "VerticalAlignment"));
        Assert.Equal("Bottom", Setter("FireMarksAndMeters.axaml", "^.FireGearTileBadge", "VerticalAlignment"));
    }
}
