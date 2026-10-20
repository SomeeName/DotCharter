using JuceSharp.StemSeparation.Onnx;

namespace DotCharter.Tests.Services.Audio.Gpu;

/// <summary>
///     The app reaches ONNX Runtime through a ProjectReference chain (UI → Services →
///     Tone → JuceSharp.StemSeparation.Onnx), and so does this test assembly. The DirectML
///     package's own targets copy DirectML.dll only into a DIRECT referencer, so these
///     pin that the chain delivers it, that the runtime really has DirectML, and that the
///     166 MB CUDA provider is gone from the payload.
/// </summary>
public sealed class DirectMlRuntimeDeploymentTests
{
    [Fact]
    public void DirectMlDll_TravelsThroughTheProjectReferences()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("DirectML is Windows-only.");
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "DirectML.dll")),
            "DirectML.dll is not beside the app's assemblies; onnxruntime.dll would load an older System32 copy.");
    }

    [Fact]
    public void TheCudaProvider_IsNotInThePayload()
    {
        Assert.False(File.Exists(Path.Combine(AppContext.BaseDirectory, "onnxruntime_providers_cuda.dll")),
            "onnxruntime_providers_cuda.dll is back: the Gpu package returned, and with it ~166 MB nobody can load.");
    }

    /// <summary>
    ///     What ships is in Help ▸ License &amp; notices: ONNX Runtime (MIT) and DirectML.dll,
    ///     which is NOT MIT but Microsoft's redistributable licence, so the notice has to
    ///     say so. ONNX Runtime was missing from the notices (then LICENSE.md) before #151 made it ship
    ///     DirectML.dll too.
    /// </summary>
    [Fact]
    public void TheShippedRuntime_IsInTheThirdPartyNotices()
    {
        var notices = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "THIRD-PARTY-NOTICES.md"));
        Assert.Contains("Microsoft.ML.OnnxRuntime.DirectML", notices, StringComparison.Ordinal);
        Assert.Contains("`DirectML.dll`", notices, StringComparison.Ordinal);
        Assert.Contains("DirectML redistributable licence", notices, StringComparison.Ordinal);
    }

    /// <summary>
    ///     DirectML.dll is proprietary and ships beside a GPL program, so the notices carry a GPLv3 section 7
    ///     additional permission for combining DotCharter with it (#852). Without it the bundle relies on the
    ///     system-library exception, which a NuGet copy does not clearly meet.
    /// </summary>
    [Fact]
    public void TheNotices_GrantTheGplSection7PermissionForDirectMl()
    {
        var notices = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "THIRD-PARTY-NOTICES.md"));
        Assert.Contains("Additional permission under GNU GPL version 3 section 7: Microsoft DirectML", notices, StringComparison.Ordinal);
        Assert.Contains("grant you\nadditional permission to convey the resulting work", notices.ReplaceLineEndings("\n"), StringComparison.Ordinal);
    }

    [Fact]
    public void TheLoadedRuntime_OffersDirectMl()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("DirectML is Windows-only.");
        Assert.Null(OnnxSessionFactory.NativeRuntimeFailure);
        Assert.Contains("DmlExecutionProvider", OnnxSessionFactory.AvailableNativeProviders);
    }
}
