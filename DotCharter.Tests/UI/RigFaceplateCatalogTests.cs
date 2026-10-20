using DotCharter.Tone.UI.Controls.Dsp;

namespace DotCharter.Tests.UI;

/// <summary>
///     The shipped faceplate catalogue (<c>Assets/Rig/faceplates.json</c>, written by
///     <c>tools/rig-art/import-faceplates.py</c>): every face names an image that
///     ships, every knob style a cap that ships, every control stands on the face,
///     and no two controls share one nut — a shared nut is a knob that turns two
///     params, or one that sits under another.
/// </summary>
public sealed class RigFaceplateCatalogTests
{
    private static string RigAssets() => Path.Combine(TestPaths.RepoRoot(), "DotCharter.Tone.UI", "Assets", "Rig");

    private static IReadOnlyDictionary<string, RigFaceplate> Shipped()
        => RigFaceplates.Parse(File.ReadAllText(Path.Combine(RigAssets(), "faceplates.json")));

    [Fact]
    public void TheCatalogue_CoversTheWholeGearList()
    {
        var faces = Shipped();
        // 100 native modules as of JuceSharp 0.9; the importer writes one face each.
        Assert.True(faces.Count >= 100, $"only {faces.Count} faceplates");
        foreach (var id in new[] { "namamp", "amp", "rat", "delay", "wah", "cab", "merger", "looper" })
            Assert.True(faces.ContainsKey(id), $"no faceplate for {id}");
    }

    [Fact]
    public void EveryFaceAndEveryCap_Ships()
    {
        var root = RigAssets();
        var missing = new List<string>();
        foreach (var face in Shipped().Values)
        {
            if (!File.Exists(Path.Combine(root, "Faces", face.File))) missing.Add(face.File);
            var cap = face.KnobCapUri[RigFaceplates.CapsRoot.Length..];
            if (face.Controls.Any(c => c.Kind is RigFaceControlKind.Knob or RigFaceControlKind.SteppedKnob)
                && !File.Exists(Path.Combine(root, "Caps", cap)))
            {
                missing.Add(cap);
            }
        }

        foreach (var shared in new[] { RigFaceplates.ToggleUp, RigFaceplates.ToggleDown, RigFaceplates.SelectorCap })
            if (!File.Exists(Path.Combine(root, "Caps", shared[RigFaceplates.CapsRoot.Length..]))) missing.Add(shared);
        Assert.True(missing.Count == 0, "not shipped: " + string.Join(", ", missing.Distinct()));
    }

    [Fact]
    public void EveryControl_StandsOnItsFace_OnANutOfItsOwn()
    {
        var problems = new List<string>();
        foreach (var face in Shipped().Values)
        {
            foreach (var c in face.Controls)
            {
                if (c.X is < 0 or > 1 || c.Y is < 0 or > 1) problems.Add($"{face.ModuleTypeId}.{c.Param} is off the face ({c.X}, {c.Y})");
                if (c.Diameter is <= 0 or > 0.3) problems.Add($"{face.ModuleTypeId}.{c.Param} has a nut of {c.Diameter}");
            }

            var controls = face.Controls.ToList();
            for (var i = 0; i < controls.Count; i++)
            {
                for (var j = i + 1; j < controls.Count; j++)
                {
                    var a = controls[i];
                    var b = controls[j];
                    var dx = (a.X - b.X) * face.PixelWidth;
                    var dy = (a.Y - b.Y) * face.PixelHeight;
                    if (Math.Sqrt(dx * dx + dy * dy) < 0.02 * face.PixelWidth)
                        problems.Add($"{face.ModuleTypeId}: {a.Param} and {b.Param} share a nut");
                }
            }

            if (controls.Select(c => c.Param).Distinct().Count() != controls.Count)
                problems.Add($"{face.ModuleTypeId}: a param is placed twice");
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    /// <summary>
    ///     Every face is stored at (close to) the generated art's own resolution: the
    ///     rig draws the selected gear BIG, on high-DPI screens too, and a face stored
    ///     smaller than it is drawn is upscaled — the printed scales, numbers and labels
    ///     came out blurry when faces were stored at 560 / 900 px (owner, 2026-09-27).
    /// </summary>
    [Fact]
    public void EveryFace_IsStoredSharpEnoughForTheStage()
    {
        var thin = Shipped().Values
            // the old 560 px stompboxes and 900 px heads both fail this; the narrowest
            // generated art (the wah's treadle, 654 px) and the squarest (the mic
            // position unit, 971 px) pass at their own resolution
            .Where(f => f.PixelHeight > f.PixelWidth ? f.PixelWidth < 640 : f.PixelWidth < 950)
            .Select(f => $"{f.ModuleTypeId} {f.PixelWidth}x{f.PixelHeight}")
            .ToList();
        Assert.True(thin.Count == 0, "stored too small to draw big without blurring: " + string.Join(", ", thin));
    }

    /// <summary>Every floor unit's printed footswitch is found (the board makes it the
    /// bypass), stands on its face, and is clear of every knob: a footswitch hit area
    /// over a knob would bypass the unit instead of turning the knob.</summary>
    [Fact]
    public void EveryStompbox_HasItsFootswitch_ClearOfItsControls()
    {
        var faces = Shipped();
        var problems = new List<string>();
        foreach (var face in faces.Values.Where(f => f.Footswitch is not null))
        {
            var foot = face.Footswitch!;
            if (foot.X is < 0 or > 1 || foot.Y is < 0.5 or > 1) problems.Add($"{face.ModuleTypeId}: footswitch at ({foot.X}, {foot.Y})");
            if (foot.Diameter is < 0.06 or > 0.4) problems.Add($"{face.ModuleTypeId}: footswitch {foot.Diameter} wide");
            foreach (var c in face.Controls)
            {
                var dx = (c.X - foot.X) * face.PixelWidth;
                var dy = (c.Y - foot.Y) * face.PixelHeight;
                var clear = (foot.Diameter * 1.25 / 2 + face.KnobCapDiameter / 2) * face.PixelWidth;
                if (Math.Sqrt(dx * dx + dy * dy) < clear) problems.Add($"{face.ModuleTypeId}: footswitch over {c.Param}");
            }
        }

        foreach (var id in new[] { "rat", "overdrive", "delay", "chorus", "compressor", "looper" })
            if (faces[id].Footswitch is null) problems.Add($"{id}: no footswitch");
        foreach (var id in new[] { "namamp", "amp", "cab", "wah" })
            if (faces[id].Footswitch is not null) problems.Add($"{id}: a footswitch on a unit that has none");
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    /// <summary>
    ///     Every rotary selector knows where its positions are PRINTED, one angle per
    ///     value: the art puts labels on four sides or round a whole circle, and the
    ///     even −135..+135° sweep pointed BETWEEN them (the miku octave, owner
    ///     2026-09-27). Measured into tools/rig-art/selector-angles.json.
    /// </summary>
    [Fact]
    public void EverySelector_PointsAtItsPrintedLabels()
    {
        var problems = new List<string>();
        foreach (var face in Shipped().Values)
        {
            foreach (var c in face.Controls.Where(c => c.Kind == RigFaceControlKind.Selector))
            {
                var steps = (int)Math.Round(c.Max - c.Min) + 1;
                if (c.Angles is null) problems.Add($"{face.ModuleTypeId}.{c.Param}: no printed angles");
                else if (c.Angles.Count != steps) problems.Add($"{face.ModuleTypeId}.{c.Param}: {c.Angles.Count} angles for {steps} values");
                else if (c.Angles.Any(a => a is < -180 or > 360)) problems.Add($"{face.ModuleTypeId}.{c.Param}: an angle off the dial");
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        // the one the owner reported: -1 Oct left, Unison top, +1 Oct right, +2 Oct bottom
        Assert.Equal([-90.0, 0, 90, 180], Shipped()["miku"].ControlFor("octave")!.Angles!);
    }

    [Theory]
    [InlineData(-1, -90)]
    [InlineData(0, 0)]
    [InlineData(2, 180)]
    [InlineData(1.4, 90)]   // a value between steps snaps to the nearest printed one
    [InlineData(9, 180)]    // …and past the ends to the last
    public void AStepLandsOnItsPrintedAngle(double value, double expected)
        => Assert.Equal(expected, KnobMath.AngleForStep(value, -1, [-90, 0, 90, 180]));

    [Fact]
    public void Parse_ReadsKinds_Ranges_AndTheKnobStyle()
    {
        const string json = """
            {"faces": {"x": {"file": "x-face.webp", "width": 560, "height": 840, "controls": [
              {"param": "a", "kind": "knob", "x": 0.3, "y": 0.2, "d": 0.08, "min": 0, "max": 10, "default": 5},
              {"param": "b", "kind": "toggle", "x": 0.5, "y": 0.5, "d": 0.05},
              {"param": "c", "kind": "model_window", "x": 0.5, "y": 0.6, "d": 0.2},
              {"param": "d", "kind": "rotary_selector", "x": 0.7, "y": 0.2, "d": 0.08},
              {"param": "e", "kind": "knob_stepped", "x": 0.3, "y": 0.4, "d": 0.08, "angles": [-40, 40]}],
              "footswitch": {"x": 0.5, "y": 0.86, "d": 0.2}}},
             "knobStyles": {"x": "Cream"}}
            """;
        var face = RigFaceplates.Parse(json)["x"];

        Assert.Equal(560.0 / 840.0, face.AspectRatio, 6);
        Assert.Equal(RigFaceplates.CapsRoot + "knob-cream.webp", face.KnobCapUri);
        Assert.Equal(
            [RigFaceControlKind.Knob, RigFaceControlKind.Toggle, RigFaceControlKind.Window, RigFaceControlKind.Selector, RigFaceControlKind.SteppedKnob],
            face.Controls.Select(c => c.Kind));
        Assert.Equal((0, 10, 5), (face.Controls[0].Min, face.Controls[0].Max, face.Controls[0].Default));
        Assert.Equal((0, 1, 0.5), (face.Controls[1].Min, face.Controls[1].Max, face.Controls[1].Default));
        // one cap size for the whole face: 1.6x the typical knob nut
        Assert.Equal(0.08 * 1.6, face.KnobCapDiameter, 6);
        Assert.Equal(new RigFaceSpot(0.5, 0.86, 0.2), face.Footswitch);
        Assert.Equal([-40.0, 40], face.Controls[4].Angles!);
        Assert.Null(face.Controls[0].Angles);
    }
}
