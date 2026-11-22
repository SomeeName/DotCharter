using DotCharter.Tone.Audio.Dsp.Presets;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Tests.Audio.Dsp.Presets;

/// <summary>
///     An exported tone names its capture and IR by file name, not by the exporting machine's
///     absolute path, and the loader resolves that name against the local library folder.
/// </summary>
public sealed class RigFilePortabilityTests
{
    private static readonly string Library = Path.Combine(Path.GetTempPath(), "dc-portability", "library");

    private static DspTonePreset Tone(string modelPath, string irPath) => new(
        "t", "Tone", DspToneCategory.Clean, string.Empty,
        new DcRigPreset(
        [
            new DcRigModule("namamp", new Dictionary<string, float>(), false)
                { StringParams = new Dictionary<string, string> { ["modelPath"] = modelPath } },
            new DcRigModule("cab", new Dictionary<string, float>(), false)
                { StringParams = new Dictionary<string, string> { ["irPath"] = irPath } }
        ]),
        DspToneOrigin.User);

    private static string Param(DspTonePreset tone, int module, string key) => tone.Rig.Modules[module].StringParams![key];

    [Fact]
    public void Export_WritesLibraryFilesAsBareNames_AndLeavesOutsidePathsAlone()
    {
        var outside = Path.Combine(Path.GetTempPath(), "elsewhere", "other.wav");
        var json = TonePresetJsonFile.Serialize(Tone(Path.Combine(Library, "amp.nam"), outside), Library);

        var parsed = TonePresetJsonFile.Parse(json).Preset!;
        Assert.Equal("amp.nam", Param(parsed, 0, "modelPath"));
        Assert.Equal(outside, Param(parsed, 1, "irPath"));
    }

    [Fact]
    public void Export_WithoutALibraryFolder_KeepsEveryValue()
    {
        var model = Path.Combine(Library, "amp.nam");
        var parsed = TonePresetJsonFile.Parse(TonePresetJsonFile.Serialize(Tone(model, "ir.wav"))).Preset!;
        Assert.Equal(model, Param(parsed, 0, "modelPath"));
    }

    [Fact]
    public void BareIrName_ResolvesAgainstTheLibrary_AndAnAbsolutePathPassesThrough()
    {
        var absolute = Path.Combine(Path.GetTempPath(), "mine", "ir.wav");
        var tone = Tone("amp.nam", "cab.wav");
        var resolved = DcRigPresetPreparation.ResolveCaptureModelPaths(tone.Rig, Library);
        Assert.Equal(Path.Combine(Library, "cab.wav"), resolved.Modules[1].StringParams!["irPath"]);

        var kept = DcRigPresetPreparation.ResolveCaptureModelPaths(Tone("amp.nam", absolute).Rig, Library);
        Assert.Equal(absolute, kept.Modules[1].StringParams!["irPath"]);
    }

    [Fact]
    public void ExportThenResolve_RoundTripsToTheLocalLibraryPath()
    {
        var model = Path.Combine(Library, "amp.nam");
        var parsed = TonePresetJsonFile.Parse(TonePresetJsonFile.Serialize(Tone(model, Path.Combine(Library, "cab.wav")), Library)).Preset!;
        var resolved = DcRigPresetPreparation.ResolveCaptureModelPaths(parsed.Rig, Library);
        Assert.Equal(model, resolved.Modules[0].StringParams!["modelPath"]);
        Assert.Equal(Path.Combine(Library, "cab.wav"), resolved.Modules[1].StringParams!["irPath"]);
    }

    [Fact]
    public void Export_WritesSubfolderFilesAsForwardSlashRelativePaths_AndTheyRoundTrip()
    {
        var model = Path.Combine(Library, "Amps", "foo.nam");
        var ir = Path.Combine(Library, "Cabs", "Deep", "cab.wav");
        var parsed = TonePresetJsonFile.Parse(TonePresetJsonFile.Serialize(Tone(model, ir), Library)).Preset!;
        Assert.Equal("Amps/foo.nam", Param(parsed, 0, "modelPath"));
        Assert.Equal("Cabs/Deep/cab.wav", Param(parsed, 1, "irPath"));

        var resolved = DcRigPresetPreparation.ResolveCaptureModelPaths(parsed.Rig, Library);
        Assert.Equal(model, resolved.Modules[0].StringParams!["modelPath"]);
        Assert.Equal(ir, resolved.Modules[1].StringParams!["irPath"]);
    }

    [Fact]
    public void Export_NeverWritesADotDotPath_AndResolveRefusesOne()
    {
        var sibling = Path.Combine(Path.GetDirectoryName(Library)!, "library-other", "x.nam");
        var rig = Tone(sibling, sibling).Rig;
        var portable = DcRigPresetPreparation.MakeLibraryPathsPortable(rig, Library);
        Assert.Equal(sibling, portable.Modules[0].StringParams!["modelPath"]);

        var escaping = Tone("../secret/x.nam", "a/../../b.wav").Rig;
        var resolved = DcRigPresetPreparation.ResolveCaptureModelPaths(escaping, Library);
        Assert.Equal("../secret/x.nam", resolved.Modules[0].StringParams!["modelPath"]);
        Assert.Equal("a/../../b.wav", resolved.Modules[1].StringParams!["irPath"]);
    }

    [Fact]
    public void FindNonPortableFiles_NamesOnlyTheFilesOutsideTheLibrary()
    {
        var outside = Path.Combine(Path.GetTempPath(), "elsewhere", "other.wav");
        var rig = Tone(Path.Combine(Library, "Amps", "foo.nam"), outside).Rig;
        Assert.Equal(["other.wav"], DcRigPresetPreparation.FindNonPortableFiles(rig, Library));
        Assert.Empty(DcRigPresetPreparation.FindNonPortableFiles(rig, null));
    }
}
