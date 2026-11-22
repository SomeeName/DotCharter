using DotCharter.Tone.Audio.Dsp.Presets;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Tests.Audio.Dsp.Presets;

/// <summary>
///     v0.27 slice: the preset→rig preparation pipeline extracted from
///     <see cref="DspTonePresetService" /> so the offline take re-amp shares
///     it verbatim. The service-level behavior (defaults fill, factory
///     loudness, live apply) stays covered by DspTonePresetLibraryTests;
///     these pin the extracted NAM path resolution + purity.
/// </summary>
public sealed class DcRigPresetPreparationTests
{
    private static DcRigPreset NamRig(string modelPath) => new(new List<DcRigModule>
    {
        new("namamp", new Dictionary<string, float>(), false)
        {
            StringParams = new Dictionary<string, string> { ["modelPath"] = modelPath }
        }
    });

    [Fact]
    public void BareFileName_ResolvesUnderTheCaptureDirectory()
    {
        var resolved = DcRigPresetPreparation.ResolveCaptureModelPaths(NamRig("amp.nam"), "X:/nam");
        Assert.Equal(Path.Combine("X:/nam", "amp.nam"),
            resolved.Modules[0].StringParams!["modelPath"]);
    }

    [Theory]
    [InlineData(@"C:\captures\amp.nam")] // rooted
    public void PathLikeValues_PassThroughUnchanged(string modelPath)
    {
        var rig = NamRig(modelPath);
        var resolved = DcRigPresetPreparation.ResolveCaptureModelPaths(rig, "X:/nam");
        Assert.Same(rig, resolved); // nothing to rewrite → the SAME instance
        Assert.Equal(modelPath, resolved.Modules[0].StringParams!["modelPath"]);
    }

    [Fact]
    public void ForwardSlashRelativePath_ResolvesUnderTheLibrary()
    {
        var resolved = DcRigPresetPreparation.ResolveCaptureModelPaths(NamRig("subdir/amp.nam"), "X:/nam");
        Assert.Equal(Path.Combine("X:/nam", "subdir", "amp.nam"), resolved.Modules[0].StringParams!["modelPath"]);
    }

    [Fact]
    public void BackslashRelativePath_IsKeptVerbatimUnderTheLibrary()
    {
        // Only forward slashes are normalised; a backslash is already the Windows separator.
        var resolved = DcRigPresetPreparation.ResolveCaptureModelPaths(NamRig(@"subdir\amp.nam"), "X:/nam");
        Assert.Equal(Path.Combine("X:/nam", @"subdir\amp.nam"), resolved.Modules[0].StringParams!["modelPath"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoCaptureDirectory_PassesThroughUnchanged(string? dir)
    {
        var rig = NamRig("amp.nam");
        Assert.Same(rig, DcRigPresetPreparation.ResolveCaptureModelPaths(rig, dir));
    }

    [Fact]
    public void TheInputRig_IsNeverMutated()
    {
        var rig = NamRig("amp.nam");
        var resolved = DcRigPresetPreparation.ResolveCaptureModelPaths(rig, "X:/nam");

        Assert.NotSame(rig, resolved);
        Assert.Equal("amp.nam", rig.Modules[0].StringParams!["modelPath"]); // original intact
    }

    [Theory]
    [InlineData("cab", "irPathB")]
    [InlineData("convreverb", "irFile")]
    public void AdditionalIrFiles_RoundTripPortablePaths_WithoutMutatingThePreset(string moduleType, string key)
    {
        var directory = Path.GetFullPath("X:/nam");
        var path = Path.Combine(directory, "IRs", "room.wav");
        var rig = new DcRigPreset([new(moduleType, new Dictionary<string, float>())
        {
            StringParams = new Dictionary<string, string> { [key] = path }
        }]);

        var portable = DcRigPresetPreparation.MakeLibraryPathsPortable(rig, directory);
        Assert.Equal("IRs/room.wav", portable.Modules[0].StringParams![key]);
        var resolved = DcRigPresetPreparation.ResolveCaptureModelPaths(portable, directory);
        Assert.Equal(path, resolved.Modules[0].StringParams![key]);
        Assert.Equal(path, rig.Modules[0].StringParams![key]);
        Assert.Equal("IRs/room.wav", portable.Modules[0].StringParams![key]);
    }

    [Theory]
    [InlineData("cab", "irPathB")]
    [InlineData("convreverb", "irFile")]
    public void AdditionalIrFiles_OutsideTheLibrary_AreReported(string moduleType, string key)
    {
        var path = Path.GetFullPath("X:/other/room.wav");
        var rig = new DcRigPreset([new(moduleType, new Dictionary<string, float>())
        {
            StringParams = new Dictionary<string, string> { [key] = path }
        }]);

        Assert.Same(rig, DcRigPresetPreparation.MakeLibraryPathsPortable(rig, "X:/nam"));
        Assert.Equal(["room.wav"], DcRigPresetPreparation.FindNonPortableFiles(rig, "X:/nam"));
    }

    [Fact]
    public void ModulesWithoutModelPath_AreLeftAlone()
    {
        var rig = new DcRigPreset(new List<DcRigModule>
        {
            new("amp", new Dictionary<string, float> { ["gain"] = 0.5f }, false)
        });

        Assert.Same(rig, DcRigPresetPreparation.ResolveCaptureModelPaths(rig, "X:/nam"));
    }
}
