using DotCharter.Services.Update;

namespace DotCharter.Tests.Services.Update;

/// <summary>
///     What the release payload may and may not drop.
///     <para>
///         A prune is the one release step whose mistakes are invisible until a
///         user runs the build: everything still compiles, the zip still validates,
///         and the app dies at the first call into whatever was removed. So each
///         rule is pinned from both sides — the thing it must remove, and the
///         nearest thing it must not.
///     </para>
/// </summary>
public sealed class ReleasePayloadPruneTests
{
    /// <summary>A payload where every assembly has a sibling .dll, as a real one does.</summary>
    private static Func<string, bool> PayloadWith(params string[] files) =>
        name => files.Contains(name, StringComparer.OrdinalIgnoreCase);

    private static Func<string, bool> NoSiblings() => _ => false;

    // ---- symbols -----------------------------------------------------------

    /// <summary>
    ///     The two files this prune exists for: 109 MB on disk / 27.9 MB compressed
    ///     of debug symbols for native libraries whose source we do not have.
    /// </summary>
    [Theory]
    [InlineData("libSkiaSharp.pdb")]
    [InlineData("libHarfBuzzSharp.pdb")]
    public void ThirdPartyNativeSymbolsAreDropped(string file) =>
        Assert.Equal(PruneReason.ForeignNativeSymbols,
            ReleasePayloadPrune.Classify(file, NoSiblings()));

    /// <summary>
    ///     OUR symbols stay. They cost 1.8 MB compressed and they are what puts
    ///     line numbers in a crash report — dropping them to save under half a
    ///     percent would trade away every future stack trace.
    /// </summary>
    [Theory]
    [InlineData("DotCharter.pdb")]
    [InlineData("DotCharter.Services.pdb")]
    [InlineData("DotCharter.Localization.pdb")]
    [InlineData("JuceSharp.pdb")]
    [InlineData("JuceSharp.Dsp.pdb")]
    public void OurOwnSymbolsAreKept(string file) =>
        Assert.Equal(PruneReason.Keep, ReleasePayloadPrune.Classify(file, NoSiblings()));

    // ---- documentation -----------------------------------------------------

    /// <summary>
    ///     An XML file beside the assembly it documents is IntelliSense output for
    ///     someone referencing us as a library. Nothing reads it at runtime.
    /// </summary>
    [Fact]
    public void XmlDocumentationBesideItsAssemblyIsDropped() =>
        Assert.Equal(PruneReason.XmlDocumentation,
            ReleasePayloadPrune.Classify("DotCharter.xml", PayloadWith("DotCharter.dll")));

    /// <summary>
    ///     …and the rule that keeps that safe: an <c>.xml</c> with no assembly of
    ///     the same name is DATA, not documentation. Deleting a config or catalogue
    ///     file because of its extension is exactly the failure this prune could
    ///     cause and could not be caught by any build.
    /// </summary>
    [Theory]
    [InlineData("settings.xml")]
    [InlineData("tone-catalogue.xml")]
    [InlineData("arrangement.xml")]
    public void AnXmlWithNoSiblingAssemblyIsKept(string file) =>
        Assert.Equal(PruneReason.Keep,
            ReleasePayloadPrune.Classify(file, PayloadWith("DotCharter.dll", "onnxruntime.dll")));

    // ---- link-time artefacts ----------------------------------------------

    [Theory]
    [InlineData("onnxruntime.lib")]
    [InlineData("onnxruntime_providers_cuda.lib")]
    public void ImportLibrariesAreDropped(string file) =>
        Assert.Equal(PruneReason.ImportLibrary,
            ReleasePayloadPrune.Classify(file, NoSiblings()));

    // ---- execution providers ----------------------------------------------

    /// <summary>
    ///     TensorRT is never requested — <c>GpuExecutionProviderDecision</c> has no
    ///     arm for it — and it needs an SDK this app neither ships nor downloads,
    ///     so it could not load if something did.
    /// </summary>
    [Fact]
    public void TheUnusedTensorRtProviderIsDropped() =>
        Assert.Equal(PruneReason.UnusedOnnxProvider,
            ReleasePayloadPrune.Classify("onnxruntime_providers_tensorrt.dll", NoSiblings()));

    /// <summary>
    ///     The providers that ARE reachable stay. CUDA is the whole GPU
    ///     stem-separation feature and <c>onnxruntime_providers_shared.dll</c> is
    ///     the bridge every provider loads through — dropping either silently
    ///     disables GPU separation on the machines that have it.
    /// </summary>
    [Theory]
    [InlineData("onnxruntime.dll")]
    [InlineData("onnxruntime_providers_cuda.dll")]
    [InlineData("onnxruntime_providers_shared.dll")]
    [InlineData("Microsoft.ML.OnnxRuntime.dll")]
    public void ReachableOnnxComponentsAreKept(string file) =>
        Assert.Equal(PruneReason.Keep, ReleasePayloadPrune.Classify(file, NoSiblings()));

    // ---- everything the app actually runs ----------------------------------

    /// <summary>
    ///     A spot-check across every other kind of file a payload holds. The prune
    ///     is opt-IN by extension, so this is what proves it stays that way.
    /// </summary>
    [Theory]
    [InlineData("DotCharter.exe")]
    [InlineData("DotCharter.dll")]
    [InlineData("juce_sharp.dll")]
    [InlineData("coreclr.dll")]
    [InlineData("hostfxr.dll")]
    [InlineData("libSkiaSharp.dll")]
    [InlineData("Silk.NET.Direct3D11.dll")]
    [InlineData("DotCharter.runtimeconfig.json")]
    [InlineData("DotCharter.deps.json")]
    [InlineData("tone_gear_data.json")]
    [InlineData("Fender Twin.nam")]
    [InlineData("ffmpeg.exe")]
    [InlineData("LICENSE")]
    [InlineData("THIRD-PARTY-NOTICES.md")]
    public void EverythingTheAppRunsIsKept(string file) =>
        Assert.Equal(PruneReason.Keep,
            ReleasePayloadPrune.Classify(file, PayloadWith("DotCharter.dll", "libSkiaSharp.dll")));

    /// <summary>
    ///     The files the publish-layout check requires must be beyond the prune's
    ///     reach, stated against that check's own list rather than a copy of it —
    ///     these are the ones whose absence bricks the install outright.
    /// </summary>
    [Fact]
    public void NothingThePublishLayoutCheckRequiresCanBePruned()
    {
        string[] required =
        [
            "DotCharter.exe", "juce_sharp.dll", "hostfxr.dll", "coreclr.dll",
            "DotCharter.runtimeconfig.json", "DotCharter.deps.json"
        ];

        foreach (var file in required)
            Assert.Equal(PruneReason.Keep, ReleasePayloadPrune.Classify(file, _ => true));
    }

    /// <summary>Every prune reason explains itself; a silent deletion is unauditable.</summary>
    [Fact]
    public void EveryReasonHasWords()
    {
        foreach (var reason in Enum.GetValues<PruneReason>())
            Assert.False(string.IsNullOrWhiteSpace(ReleasePayloadPrune.Describe(reason)));
    }

    /// <summary>The release script must actually apply the rules, or they are a library.</summary>
    [Fact]
    public void TheReleaseScriptRunsThePrune()
    {
        var script = File.ReadAllText(RepoPath("tools/release/New-Release.ps1"));

        Assert.Contains("PrunePayload.cs", script, StringComparison.Ordinal);
    }

    /// <summary>
    ///     …and runs it BEFORE the manifest is hashed. The manifest is what the
    ///     in-app updater range-fetches against: hashing first would describe files
    ///     the zip does not contain, and every partial update would fail looking
    ///     for them.
    /// </summary>
    [Fact]
    public void ThePruneRunsBeforeThePayloadIsHashed()
    {
        var script = File.ReadAllText(RepoPath("tools/release/New-Release.ps1"));

        // Both helpers are DECLARED adjacently at the top of the script, so
        // comparing first mentions compares declarations and proves nothing. The
        // order that matters is where each one is CALLED against the payload.
        var prune = script.IndexOf("Remove-DeadPayloadWeight -PublishDir", StringComparison.Ordinal);
        var hash = script.IndexOf("dotnet run $ManifestTool", StringComparison.Ordinal);

        Assert.True(prune >= 0, "the release script never calls the prune on the payload");
        Assert.True(hash >= 0, "the release script never runs the manifest builder");
        Assert.True(prune < hash,
            "the prune must run before the manifest is hashed, or the manifest describes deleted files");
    }

    private static string RepoPath(string relative) =>
        Path.Combine(TestPaths.RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar));
}
