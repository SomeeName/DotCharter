using DotCharter.Tone.Audio.Dsp.Presets;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Tests.Audio.Dsp.Presets;

/// <summary>
///     The shareable tone FILE (<see cref="TonePresetJsonFile" />): the persisted
///     <c>UserTonePresetDto</c> schema written out as one document, and read back
///     defensively — the file comes from outside the app, so every module id is
///     checked against the catalogue and every value is forced into range.
///     <para>
///         Nothing here may throw on a hostile document: an import that throws is
///         a crash on somebody else's typo, and an import that silently accepts a
///         nonsense value is a rig the engine refuses one param at a time.
///     </para>
/// </summary>
public sealed class TonePresetJsonFileTests
{
    private static DspTonePreset Tone(params DcRigModule[] modules) =>
        new("my-tone", "Crunchy", DspToneCategory.Crunch, "a description",
            new DcRigPreset(modules), DspToneOrigin.User);

    private static DcRigModule Module(string id, params (string Param, float Value)[] values) =>
        new(id, values.ToDictionary(v => v.Param, v => v.Value, StringComparer.Ordinal));

    [Fact]
    public void ATone_RoundTripsThroughTheFile_WithItsStringsAndBranches()
    {
        var tone = Tone(
            new DcRigModule("namamp", new Dictionary<string, float> { ["gain"] = 0.6f })
            {
                StringParams = new Dictionary<string, string> { ["modelPath"] = @"C:\captures\plexi.nam" },
                Branch = 2
            },
            Module("delay", ("mix", 0.4f)));

        var parsed = TonePresetJsonFile.Parse(TonePresetJsonFile.Serialize(tone));

        Assert.True(parsed.Ok);
        Assert.Equal(TonePresetImportFault.None, parsed.Fault);
        var back = parsed.Preset!;
        Assert.Equal("my-tone", back.Id);
        Assert.Equal("Crunchy", back.Name);
        Assert.Equal(DspToneCategory.Crunch, back.Category);
        Assert.Equal("a description", back.Description);
        Assert.True(back.IsUser);

        Assert.Equal(["namamp", "delay"], back.Rig.Modules.Select(m => m.ModuleTypeId).ToArray());
        var amp = back.Rig.Modules[0];
        Assert.Equal(0.6f, amp.Params["gain"], 5);
        // The ABI-v22 capture and the ABI-v23 branch have to travel, or the file
        // shares a MODELLESS neural amp flattened onto one lane.
        Assert.Equal(@"C:\captures\plexi.nam", amp.StringParams!["modelPath"]);
        Assert.Equal(2, amp.Branch);
    }

    [Fact]
    public void AnImportedTone_HasEveryOmittedParamFilledWithItsDefault()
    {
        // A sparse document must not inherit the values of whatever tone was
        // applied before it (the DcRigApplier node-reuse rule).
        var parsed = TonePresetJsonFile.Parse(TonePresetJsonFile.Serialize(Tone(Module("delay"))));

        var delay = Assert.Single(parsed.Preset!.Rig.Modules);
        Assert.True(delay.Params.Count > 1, "the catalogue defaults were not filled in");
        Assert.True(delay.Params.ContainsKey("mix"));
    }

    [Fact]
    public void OutOfRangeValuesAndBranches_AreForcedIntoRange()
    {
        // Hand-written, because these are values a hand-edited (or older, or
        // newer) file carries and the app's own writer would never produce.
        var parsed = TonePresetJsonFile.Parse(
            """
            {
              "Name": "Wild",
              "Modules": [
                { "ModuleTypeId": "delay",
                  "Params": { "mix": 9.0, "feedback": -4.0, "timeMs": 99999.0 },
                  "Branch": 9 }
              ]
            }
            """);

        Assert.True(parsed.Ok);
        var delay = Assert.Single(parsed.Preset!.Rig.Modules);
        Assert.Equal(1f, delay.Params["mix"], 5);          // clamped to its max
        Assert.Equal(0f, delay.Params["feedback"], 5);     // clamped to its min
        Assert.Equal(2000f, delay.Params["timeMs"], 5);    // the delay's ceiling
        Assert.InRange(delay.Branch, 0, 3);
    }

    [Fact]
    public void AParamNoModuleDeclares_IsCarriedButNeverInvented()
    {
        // An unknown param id passes through unchanged (the native module refuses
        // what it does not know) — it is not a reason to reject a whole tone, and
        // it is not clamped against a range that does not exist.
        var parsed = TonePresetJsonFile.Parse(
            "{\"Name\":\"Odd\",\"Modules\":[{\"ModuleTypeId\":\"delay\",\"Params\":{\"nonesuch\":7.5}}]}");

        Assert.True(parsed.Ok);
        Assert.Equal(7.5f, Assert.Single(parsed.Preset!.Rig.Modules).Params["nonesuch"], 5);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{ \"Name\": ")]
    [InlineData("[1, 2, 3]")]
    [InlineData("42")]
    public void AHostileDocument_IsRefusedWithoutThrowing(string? json)
    {
        var parsed = TonePresetJsonFile.Parse(json);

        Assert.False(parsed.Ok);
        Assert.Equal(TonePresetImportFault.Malformed, parsed.Fault);
    }

    [Fact]
    public void AToneWithNoModules_IsRefused()
    {
        var parsed = TonePresetJsonFile.Parse("{\"Name\":\"Empty\",\"Modules\":[]}");

        Assert.False(parsed.Ok);
        Assert.Equal(TonePresetImportFault.NoModules, parsed.Fault);

        // …and so is one whose module list is absent entirely.
        Assert.Equal(TonePresetImportFault.NoModules,
            TonePresetJsonFile.Parse("{\"Name\":\"Empty\"}").Fault);
    }

    [Fact]
    public void AModuleThisBuildDoesNotHave_IsRefused_AndNamed()
    {
        var parsed = TonePresetJsonFile.Parse(
            "{\"Name\":\"Future\",\"Modules\":[{\"ModuleTypeId\":\"quantumfuzz\",\"Params\":{}}]}");

        Assert.False(parsed.Ok);
        Assert.Equal(TonePresetImportFault.UnknownModule, parsed.Fault);
        // The id has to reach the message: "a module is missing" with no name is
        // an error the player cannot act on.
        Assert.Equal("quantumfuzz", parsed.Detail);

        // A blank id is the same verdict, and still names something.
        var blank = TonePresetJsonFile.Parse(
            "{\"Name\":\"Odd\",\"Modules\":[{\"ModuleTypeId\":\"\",\"Params\":{}}]}");
        Assert.Equal(TonePresetImportFault.UnknownModule, blank.Fault);
        Assert.False(string.IsNullOrEmpty(blank.Detail));
    }

    [Fact]
    public void AnInvalidControlMap_IsDropped_NotAReasonToRefuseTheTone()
    {
        // Sixty-five assignments is past RigControlAssignments.MaximumCount, i.e.
        // a map the domain refuses — but the SOUND is the point of the file.
        var assignments = string.Join(",", Enumerable.Range(0, 65).Select(i =>
            $"{{\"ParameterId\":\"p{i}\",\"Min\":0,\"Max\":1,\"MacroIndex\":0,\"MidiChannel\":-1,\"MidiController\":-1}}"));
        var parsed = TonePresetJsonFile.Parse(
            "{\"Name\":\"Mapped\",\"Modules\":[{\"ModuleTypeId\":\"delay\",\"Params\":{},\"ControlAssignments\":["
            + assignments + "]}]}");

        Assert.True(parsed.Ok);
        Assert.Empty(Assert.Single(parsed.Preset!.Rig.Modules).ControlAssignments);
    }

    [Fact]
    public void TheDocument_IsTheSameSchemaTheConfigStores()
    {
        // One schema, so a tone saved in one install and a tone exported from
        // another are the same bytes — a second serializer here is the drift that
        // cost the snapshot loops their string params.
        var tone = Tone(Module("delay", ("mix", 0.4f)));
        var json = TonePresetJsonFile.Serialize(tone);

        var dto = System.Text.Json.JsonSerializer.Deserialize<UserTonePresetDto>(json)!;
        Assert.Equal(tone.Name, dto.Name);
        Assert.Equal("delay", Assert.Single(dto.Modules).ModuleTypeId);
        Assert.Contains("\n", json); // indented: a human reads these
    }
}
