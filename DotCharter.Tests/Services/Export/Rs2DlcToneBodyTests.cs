using System.Text;
using System.Text.Json;
using DotCharter.Services.Ai.Generate;
using DotCharter.Services.Export.DlcBuilder;
using DotCharter.Services.Export.Rocksmith;
using DotCharter.Tone.Audio.Dsp.Tones;

namespace DotCharter.Tests.Services.Export;

/// <summary>
///     The <c>.rs2dlc</c> carries the tone BODIES, not just the base tone's name.
///     <para>
///         A project that names <c>BaseTone: "Verse"</c> with <c>Tones: []</c> is
///         one DLC Builder cannot build — the name resolves to nothing. The field
///         existed in the format the whole time; it was empty because the app had
///         no tone bodies to put there, and the generator now produces them.
///     </para>
///     <para>
///         The shape is pinned against a FIXTURE copied verbatim out of the owner's
///         own DLC-Builder-saved project
///         (<c>Fixtures/dlcbuilder-tone-and-arrangement.json</c>), so "the shape DLC
///         Builder writes" is a file in the repo rather than a claim in a comment.
///     </para>
/// </summary>
public sealed class Rs2DlcToneBodyTests
{
    private static readonly Rs2DlcProjectBuilder.Rs2DlcBuildOptions Options =
        new("song.wav", "cover.png", 60_000, "Tester");

    // ---- the fixture ---------------------------------------------------

    private static JsonElement Fixture(string member)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Services", "Export", "Fixtures",
            "dlcbuilder-tone-and-arrangement.json");
        Assert.True(File.Exists(path), $"the DLC Builder reference fixture is missing: {path}");

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty(member).Clone();
    }

    /// <summary>The reference tone rebuilt as an <see cref="RsTone" />: the same
    /// gear in the same slots with the same knob values, so what the writer emits
    /// can be compared to the reference OBJECT and not merely to its outline.</summary>
    private static RsTone ReferenceTone() => new(
        "Lead", "Lead", -22f, ["$[35724]LEAD"],
        new RsAmp("Amp_OrangeAD50", "Amp", new Dictionary<string, float>
        {
            ["Amp_OrangeAD50_Bass"] = 60,
            ["Amp_OrangeAD50_Gain"] = 33,
            ["Amp_OrangeAD50_Mid"] = 44,
            ["Amp_OrangeAD50_Treble"] = 57
        }),
        new RsCab("Cab_OrangePPC212OB_Ribbon_Cone", "Ribbon_Cone", new Dictionary<string, float>()),
        [
            new RsPedal(RsGearRole.PrePedal, 1, "Pedal_Compression", RsGearType.Pedal, "Dynamics",
                new Dictionary<string, float>
                {
                    ["Pedal_Compression_Attack"] = 30,
                    ["Pedal_Compression_Comp"] = 36,
                    ["Pedal_Compression_Release"] = 85
                })
        ],
        [
            new RsPedal(RsGearRole.Rack, 1, "Rack_StudioEQ", RsGearType.Rack, "Filter",
                new Dictionary<string, float>
                {
                    ["Rack_StudioEQ_Bass"] = 1,
                    ["Rack_StudioEQ_BassFreq"] = 120,
                    ["Rack_StudioEQ_HiMid"] = 2,
                    ["Rack_StudioEQ_HiMidFreq"] = 3,
                    ["Rack_StudioEQ_HiMidQ"] = 0,
                    ["Rack_StudioEQ_LoMid"] = -3,
                    ["Rack_StudioEQ_LoMidFreq"] = 260,
                    ["Rack_StudioEQ_LoMidQ"] = 0,
                    ["Rack_StudioEQ_Treble"] = 1,
                    ["Rack_StudioEQ_TrebleFreq"] = 6.6f
                })
        ],
        [
            new RsPedal(RsGearRole.PostPedal, 1, "Pedal_SpringReverb", RsGearType.Pedal, "Reverb",
                new Dictionary<string, float>
                {
                    ["Pedal_SpringReverb_Depth"] = 40,
                    ["Pedal_SpringReverb_Mix"] = 28,
                    ["Pedal_SpringReverb_Time"] = 28
                })
        ]);

    // ---- parity with DLC Builder's own file -----------------------------

    /// <summary>
    ///     Same property names, same nesting, same knob-key naming
    ///     (<c>&lt;GearKey&gt;_&lt;Pot&gt;</c>) — asserted by rebuilding the
    ///     reference tone and comparing the object we write against the one DLC
    ///     Builder wrote, node for node.
    /// </summary>
    [Fact]
    public void A_tone_body_matches_the_shape_DLC_Builder_writes()
    {
        var project = ProjectWith(ReferenceTone());

        var mine = Assert.Single(SerializedTones(project));

        AssertSameStructure(Fixture("Tone"), mine, "Tones[0]");
    }

    /// <summary>Structure is not enough on its own: two objects can agree on every
    /// key and disagree on every value. The rebuilt reference must serialize to the
    /// reference's VALUES too — including <c>Volume</c> as a three-decimal string
    /// and <c>6.6</c> surviving as <c>6.6</c> rather than <c>6.5999999</c>.</summary>
    [Fact]
    public void A_rebuilt_reference_tone_serializes_to_the_reference_values()
    {
        var project = ProjectWith(ReferenceTone());
        var mine = Assert.Single(SerializedTones(project));

        Assert.Equal(Canonical(Fixture("Tone")), Canonical(mine));
    }

    /// <summary>The arrangement object carries its base tone and its tone slots the
    /// way DLC Builder does: <c>BaseTone</c> is a name, <c>Tones</c> is the list of
    /// ADDITIONAL names (the reference arrangement has none; ours has one).</summary>
    [Fact]
    public void An_arrangement_matches_the_shape_DLC_Builder_writes()
    {
        var project = ProjectWith(ReferenceTone());
        project.Arrangements[0].ToneBase = "Lead";
        project.Arrangements[0].ToneA = "Chorus";

        using var document = JsonDocument.Parse(Rs2DlcWriter.Serialize(
            Rs2DlcProjectBuilder.Build(project, Options)));
        var mine = document.RootElement.GetProperty("Arrangements")[0].GetProperty("Fields")[0];

        foreach (var property in Fixture("Arrangement").GetProperty("Fields")[0].EnumerateObject())
        {
            Assert.True(mine.TryGetProperty(property.Name, out var ours),
                $"DLC Builder writes '{property.Name}' on an Instrumental arrangement and we do not");
            Assert.Equal(property.Value.ValueKind, ours.ValueKind);
        }

        Assert.Equal("Lead", mine.GetProperty("BaseTone").GetString());

        // One slot, because this arrangement's placed changes are unnamed — the
        // slot list follows the XML writer's rule, not "everything but the base".
        Assert.Equal(["Chorus"], mine.GetProperty("Tones").EnumerateArray().Select(t => t.GetString()));
    }

    /// <summary>
    ///     The slot list is the SAME answer the RS XML writer gives, because it is
    ///     the same call. DLC Builder reads tone usage out of the arrangement XML,
    ///     not out of the project file, and marks anything the XML never names
    ///     "(Unused)" — so a slot list derived independently here is a bug that
    ///     ships looking fine.
    /// </summary>
    [Fact]
    public void The_slot_list_is_the_same_answer_the_XML_writer_gives()
    {
        var project = ProjectWith(ReferenceTone());
        var arrangement = project.Arrangements[0];
        arrangement.ToneBase = "Lead";
        arrangement.ToneA = "Chorus";
        arrangement.ToneChanges[0].ToneName = "Lead";
        arrangement.ToneChanges[1].ToneName = "Chorus";
        arrangement.ToneChanges[2].ToneName = "Solo";

        var expected = RsToneSlots.Resolve(arrangement);

        using var document = JsonDocument.Parse(Rs2DlcWriter.Serialize(
            Rs2DlcProjectBuilder.Build(project, Options)));
        var fields = document.RootElement.GetProperty("Arrangements")[0].GetProperty("Fields")[0];

        Assert.Equal(expected.ToneBase, fields.GetProperty("BaseTone").GetString());
        Assert.Equal(expected.Slots, fields.GetProperty("Tones").EnumerateArray().Select(t => t.GetString()));

        // Non-trivially: a change switching BACK to the base puts the base in a
        // slot as well, which the "everything except the base" rule would drop.
        Assert.Contains("Lead", expected.Slots);
    }

    // ---- the empty case, which must stay empty --------------------------

    [Fact]
    public void A_project_with_no_tone_definitions_still_writes_an_empty_list()
    {
        using var document = JsonDocument.Parse(Rs2DlcWriter.Serialize(
            Rs2DlcProjectBuilder.Build(EmptyProject(), Options)));

        Assert.Equal(JsonValueKind.Array, document.RootElement.GetProperty("Tones").ValueKind);
        Assert.Empty(document.RootElement.GetProperty("Tones").EnumerateArray());
    }

    /// <summary>A body that is not a tone object cannot be repaired here, and a
    /// save must not fail because of it — it is dropped, and the others still go.</summary>
    [Fact]
    public void A_malformed_tone_body_is_skipped_rather_than_thrown_on()
    {
        var project = ProjectWith(ReferenceTone());
        project.ToneDefinitions.Insert(0,
            new ToneDefinition { Key = "Junk", Name = "Junk", Json = "{ not json" });
        project.ToneDefinitions.Add(
            new ToneDefinition { Key = "Blank", Name = "Blank", Json = "" });

        var tone = Assert.Single(SerializedTones(project));
        Assert.Equal("Lead", tone.GetProperty("Key").GetString());
    }

    // ---- the generator's own output, read back --------------------------

    /// <summary>
    ///     The round trip the feature exists for: tones the generator wrote into the
    ///     project reach the <c>.rs2dlc</c> as N tone objects, and every one of them
    ///     parses back into an <see cref="RsTone" /> whose keys and knobs pass the
    ///     Rocksmith catalogue gate.
    /// </summary>
    [Fact]
    public void Generated_tones_reach_the_rs2dlc_and_pass_the_catalogue_when_read_back()
    {
        var catalog = RsGearCatalog.Shared;
        var project = EmptyProject();
        var arrangement = project.Arrangements[0];

        var generated = new List<GeneratedTone>();
        for (var i = 0; i < 3; i++)
        {
            var tone = ReferenceTone() with { Key = $"Gen_{i}", Name = $"Generated {i}" };
            var window = new ToneWindow(i, tone.Key, 4000 + (i * 4000), 2000 + (i * 4000),
                4000 + (i * 4000), 2000, 2000, ToneWindowClamp.None);
            generated.Add(new GeneratedTone(window, tone, null, null));
        }

        RsToneProjectWriter.Write(project, arrangement, generated, catalog);
        Assert.Equal(3, project.ToneDefinitions.Count);

        var tones = SerializedTones(project);
        Assert.Equal(3, tones.Count);

        foreach (var written in tones)
        {
            Assert.True(RsToneJsonParser.TryParse(written.GetRawText(), out var parsed),
                $"a tone we wrote into the .rs2dlc does not parse back: {written.GetRawText()}");

            var fault = RsToneCatalogValidation.FirstFault(parsed, catalog);
            Assert.True(fault is null, $"a tone we wrote is not in the catalogue: {fault?.Message}");
        }

        // The base tone now names a body that is actually IN the file — which is
        // the whole point: a name with no body is a project DLC Builder cannot build.
        using var document = JsonDocument.Parse(Rs2DlcWriter.Serialize(
            Rs2DlcProjectBuilder.Build(project, Options)));
        var fields = document.RootElement.GetProperty("Arrangements")[0].GetProperty("Fields")[0];
        var baseTone = fields.GetProperty("BaseTone").GetString();
        Assert.Contains(tones, t => t.GetProperty("Key").GetString() == baseTone);

        foreach (var slot in fields.GetProperty("Tones").EnumerateArray())
            Assert.Contains(tones, t => t.GetProperty("Key").GetString() == slot.GetString());
    }

    // ---- helpers --------------------------------------------------------

    private static List<JsonElement> SerializedTones(ChartProject project)
    {
        using var document = JsonDocument.Parse(Rs2DlcWriter.Serialize(
            Rs2DlcProjectBuilder.Build(project, Options)));
        return document.RootElement.GetProperty("Tones").EnumerateArray()
            .Select(t => t.Clone()).ToList();
    }

    private static ChartProject ProjectWith(RsTone tone)
    {
        var project = EmptyProject();
        project.ToneDefinitions.Add(new ToneDefinition
        {
            Key = tone.Key,
            Name = tone.Name,
            Json = RsToneJsonWriter.Write(tone)
        });
        return project;
    }

    private static ChartProject EmptyProject()
    {
        var project = new ChartProject
        {
            Bpm = 120,
            AudioFileName = "song.wav",
            Chart = { SongName = "My Song", Artist = "Tester" }
        };

        for (var i = 0; i < 64; i++)
        {
            project.Beats.Add(new BeatMarker
            {
                Time = i * 500.0,
                IsMeasure = i % 4 == 0,
                Bpm = 120,
                BeatsInMeasure = 4,
                NoteDenominator = 4
            });
        }

        var arrangement = new Arrangement { Name = "Lead" };
        for (var i = 0; i < 6; i++) arrangement.TuningPitches.Add(new TuningOffset(0));
        arrangement.Levels.Add(new ChartLevel { DifficultyIndex = 0 });
        for (var i = 0; i < 3; i++)
        {
            arrangement.ToneChanges.Add(new ToneChange
            {
                Position = TimelineMath.PositionFromMs(4000 + (i * 4000), project.Beats)
            });
        }

        project.Arrangements.Add(arrangement);
        return project;
    }

    /// <summary>Recursive structural comparison: property names IN ORDER, value
    /// kinds, array lengths. Values are deliberately not compared — that is the
    /// other test — so a failure here reads as "wrong shape", not "wrong tone".</summary>
    private static void AssertSameStructure(JsonElement expected, JsonElement actual, string path)
    {
        Assert.True(expected.ValueKind == actual.ValueKind,
            $"{path}: expected a {expected.ValueKind}, got a {actual.ValueKind}");

        switch (expected.ValueKind)
        {
            case JsonValueKind.Object:
                var expectedNames = expected.EnumerateObject().Select(p => p.Name).ToList();
                var actualNames = actual.EnumerateObject().Select(p => p.Name).ToList();
                Assert.True(expectedNames.SequenceEqual(actualNames, StringComparer.Ordinal),
                    $"{path}: DLC Builder writes [{string.Join(", ", expectedNames)}]; " +
                    $"we write [{string.Join(", ", actualNames)}]");

                foreach (var property in expected.EnumerateObject())
                {
                    AssertSameStructure(property.Value, actual.GetProperty(property.Name),
                        $"{path}.{property.Name}");
                }

                break;

            case JsonValueKind.Array:
                var expectedItems = expected.EnumerateArray().ToList();
                var actualItems = actual.EnumerateArray().ToList();
                Assert.True(expectedItems.Count == actualItems.Count,
                    $"{path}: expected {expectedItems.Count} item(s), got {actualItems.Count}");
                for (var i = 0; i < expectedItems.Count; i++)
                    AssertSameStructure(expectedItems[i], actualItems[i], $"{path}[{i}]");
                break;
        }
    }

    /// <summary>Whitespace-independent text of an element, so an indented file and
    /// a compact body compare on content. Property ORDER is preserved on purpose:
    /// matching DLC Builder's order is part of the parity claim.</summary>
    private static string Canonical(JsonElement element)
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
            element.WriteTo(writer);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
