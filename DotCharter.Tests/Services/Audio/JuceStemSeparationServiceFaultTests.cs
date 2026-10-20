using DotCharter.Services.Audio.StemSeparation;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Audio;

/// <summary>
///     What the native stem backend does with the two answers ABI v36 gave it:
///     a CANCEL and a MISSING MODEL.
///     <para>
///         Both used to land in one bare <c>catch (Exception ex) =&gt; Fail(ex.Message)</c>.
///         For a cancel that is a phantom error — the task completes as
///         <c>RanToCompletion</c> with <c>Success=false</c>, every await-side
///         cancellation handler is bypassed, and
///         <see cref="CompositeStemSeparationService" /> reads the failed result as
///         "this backend declined" and starts the NEXT backend on a separation the
///         user just stopped. For a missing model it is a raw wrapper sentence
///         about <c>DemucsGgmlModelCatalog</c> shown to a player who has an
///         in-app download for exactly that file.
///     </para>
///     <para>
///         Driven through <c>INativeStemSeparator</c>, the seam that exists so
///         these three outcomes are reachable without a
///         <c>-DDOTCHARTER_STEMS_DEMUCS_CPP=ON</c> build, a 300 MB weights file and
///         minutes of inference. <b>A runtime cancel against the real native call
///         additionally depends on the submodule</b> — JuceSharp's
///         <c>JuceStemSeparation.SeparateAsync</c> still forwards
///         <c>CancellationToken.None</c> to the five-argument <c>Separate</c>
///         overload. This service calls the SIX-argument one directly, so the
///         parent side is not blocked by that.
///     </para>
/// </summary>
public sealed class JuceStemSeparationServiceFaultTests : IDisposable
{
    private readonly string _dir =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "dc-stems-" + Guid.NewGuid().ToString("N"))).FullName;

    private readonly string? _previousModelEnv =
        Environment.GetEnvironmentVariable("DOTCHARTER_HTDEMUCS_MODEL");

    public void Dispose()
    {
        // The env var is process-global; restore it rather than leaving a temp
        // path behind for whatever runs next in this host. Nothing else in the
        // suite reads it today, which is the only reason using it is acceptable.
        Environment.SetEnvironmentVariable("DOTCHARTER_HTDEMUCS_MODEL", _previousModelEnv);
        BestEffortDelete.Directory(_dir);
    }

    /// <summary>A backend whose only job is to throw what the wrapper throws.</summary>
    private sealed class ThrowingNative(Exception toThrow) : INativeStemSeparator
    {
        public int Calls { get; private set; }
        public CancellationToken LastToken { get; private set; }
        public bool IsSupported => true;

        public void Separate(string inputAudioPath, string outputDirectory, string modelPath, int numThreads,
            Action<float, string>? onProgress, CancellationToken ct)
        {
            Calls++;
            LastToken = ct;
            throw toThrow;
        }
    }

    private (JuceStemSeparationService Service, string Input) Build(INativeStemSeparator native)
    {
        var input = Path.Combine(_dir, "song.wav");
        File.WriteAllBytes(input, [0x52, 0x49, 0x46, 0x46]);
        // A model file on disk so ResolveModelPath's pre-flight check passes and
        // the call actually reaches the backend; the env var is the discovery
        // step that needs no StemModelManager and no user config.
        var model = Path.Combine(_dir, "ggml-model-htdemucs-4s-f16.bin");
        File.WriteAllBytes(model, [0x67, 0x67, 0x6D, 0x6C]);
        Environment.SetEnvironmentVariable("DOTCHARTER_HTDEMUCS_MODEL", model);
        return (new JuceStemSeparationService(new FakeAppLogger(), models: null, native), input);
    }

    /// <summary>
    ///     A cancel PROPAGATES. This is the assertion the UI's Cancel button
    ///     depends on: the caller's <c>catch (OperationCanceledException)</c> is
    ///     what shows "cancelled" instead of "separation failed".
    /// </summary>
    [Fact]
    public async Task ACancelledNativeRun_PropagatesAsCancellation_NotAFailedResult()
    {
        var native = new ThrowingNative(new OperationCanceledException());
        var (service, input) = Build(native);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.SeparateAsync(input, Path.Combine(_dir, "out"), progress: null,
                TestContext.Current.CancellationToken));

        Assert.Equal(1, native.Calls);
    }

    /// <summary>
    ///     The token reaches the NATIVE call, not just <c>Task.Run</c>. Passing
    ///     <c>CancellationToken.None</c> down still cancels the wrapper task while
    ///     a multi-minute separation runs on and writes stems nobody wanted —
    ///     which is precisely what ABI v36 exists to stop.
    /// </summary>
    [Fact]
    public async Task TheCallersToken_IsHandedToTheNativeCall()
    {
        var native = new ThrowingNative(new InvalidOperationException("boom"));
        var (service, input) = Build(native);
        using var cts = new CancellationTokenSource();

        await service.SeparateAsync(input, Path.Combine(_dir, "out"), progress: null, cts.Token);

        Assert.True(native.LastToken.CanBeCanceled);
        Assert.Equal(cts.Token, native.LastToken);
    }

    /// <summary>
    ///     A missing model becomes the OFFER-THE-DOWNLOAD sentence, not the
    ///     wrapper's internal one. The wrapper names
    ///     <c>DemucsGgmlModelCatalog.EnsureAsync</c> — correct advice for a
    ///     programmer, useless to a player, who has a button for it.
    /// </summary>
    [Fact]
    public async Task AMissingModel_BecomesTheDownloadOffer_NotTheWrappersOwnMessage()
    {
        var native = new ThrowingNative(new FileNotFoundException(
            "The demucs model weights were not found. This is a download, not a bug: "
            + "resolve the file with DemucsGgmlModelCatalog.EnsureAsync before separating.",
            "ggml-model-htdemucs-4s-f16.bin"));
        var (service, input) = Build(native);

        var result = await service.SeparateAsync(input, Path.Combine(_dir, "out"), progress: null,
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(JuceStemSeparationService.ModelMissingMessage, result.ErrorMessage);
        Assert.DoesNotContain("DemucsGgmlModelCatalog", result.ErrorMessage ?? "", StringComparison.Ordinal);
        Assert.Contains("Stem Models", result.ErrorMessage ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    ///     Anti-vacuity for the two arms above: an ORDINARY failure still becomes
    ///     a failed result carrying its own message. Without this, a
    ///     <c>catch (Exception) { throw; }</c> would pass both tests above and
    ///     break every consumer.
    /// </summary>
    [Fact]
    public async Task AnOrdinaryFailure_IsStillAFailedResultWithItsOwnMessage()
    {
        var native = new ThrowingNative(new InvalidOperationException("inference blew up"));
        var (service, input) = Build(native);

        var result = await service.SeparateAsync(input, Path.Combine(_dir, "out"), progress: null,
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal("inference blew up", result.ErrorMessage);
    }
}
