using System.Text.Json;
using DotCharter.Tone.Audio.Dsp.Presets;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Tests.Audio.Dsp.Presets;

/// <summary>
/// Pins the ABI-v23 branch tag through the user-tone-profile persistence shape:
/// it round-trips through <see cref="UserTonePresetDto"/> and is OMITTED from the
/// JSON when 0 (schema tolerance — an old store with no <c>Branch</c> field reads
/// as branch 0).
/// </summary>
public sealed class UserTonePresetDtoBranchTests
{
    [Fact]
    public void Branch_RoundTrips_ThroughFromModule_And_ToModule()
    {
        var preset = new DcRigPreset(new[]
        {
            new DcRigModule("splitter", new Dictionary<string, float> { ["enabledBranches"] = 2f }),
            new DcRigModule("overdrive", new Dictionary<string, float> { ["drive"] = 0.5f }) { Branch = 2 },
            new DcRigModule("merger", new Dictionary<string, float>()),
        });
        var tone = new DspTonePreset("t", "T", DspToneCategory.HighGain, "", preset, DspToneOrigin.User);

        var dto = UserTonePresetDto.FromPreset(tone);
        Assert.Equal(0, dto.Modules[0].Branch);
        Assert.Equal(2, dto.Modules[1].Branch);

        var back = dto.ToPreset();
        Assert.Equal(2, back.Rig.Modules[1].Branch);
        Assert.Equal(0, back.Rig.Modules[0].Branch);
    }

    [Fact]
    public void Branch_Zero_IsOmitted_FromJson_ButNonZero_IsWritten()
    {
        var dto = new UserTonePresetDto
        {
            Id = "t",
            Name = "T",
            Modules =
            [
                new UserToneModuleDto { ModuleTypeId = "overdrive" },              // branch 0
                new UserToneModuleDto { ModuleTypeId = "distortion", Branch = 3 }, // branch 3
            ],
        };

        var json = JsonSerializer.Serialize(dto);

        // The branch-0 module writes no "Branch" property; the branch-3 module does.
        Assert.Contains("\"Branch\":3", json);
        Assert.DoesNotContain("\"Branch\":0", json);
    }

    [Fact]
    public void OldJson_WithoutBranchField_ReadsAsBranchZero()
    {
        const string legacy = "{\"Id\":\"t\",\"Name\":\"T\",\"Modules\":[{\"ModuleTypeId\":\"overdrive\",\"Params\":{},\"Bypassed\":false}]}";

        var dto = JsonSerializer.Deserialize<UserTonePresetDto>(legacy)!;

        Assert.Equal(0, dto.Modules[0].Branch);
        Assert.Equal(0, dto.ToPreset().Rig.Modules[0].Branch);
    }
}
