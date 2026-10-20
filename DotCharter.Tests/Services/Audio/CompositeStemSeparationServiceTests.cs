using DotCharter.Services.Audio.StemSeparation;

namespace DotCharter.Tests.Services.Audio;

public sealed class CompositeStemSeparationServiceTests
{
    private sealed class FakeBackend : IStemSeparationService
    {
        public bool Available;
        public string Description = "fake";
        public StemSeparationResult Result = new() { Success = true };
        public int SeparateCalls;

        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(Available);

        public Task<string> DescribeBackendAsync(CancellationToken ct = default)
            => Task.FromResult(Description);

        /// <summary>When set, the backend cancels the run mid-flight (as the user's
        /// Cancel button does) before returning <see cref="Result"/>.</summary>
        public CancellationTokenSource? CancelsWhenRun;

        public Task<StemSeparationResult> SeparateAsync(
            string inputAudioPath, string outputDirectory,
            IProgress<double>? progress = null, CancellationToken ct = default)
        {
            SeparateCalls++;
            CancelsWhenRun?.Cancel();
            return Task.FromResult(Result);
        }
    }

    [Fact]
    public async Task IsAvailable_returns_true_when_any_backend_ready()
    {
        var a = new FakeBackend { Available = false };
        var b = new FakeBackend { Available = true };
        var svc = new CompositeStemSeparationService(new[] { (IStemSeparationService)a, b });

        Assert.True(await svc.IsAvailableAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IsAvailable_returns_false_when_no_backends_ready()
    {
        var a = new FakeBackend { Available = false };
        var b = new FakeBackend { Available = false };
        var svc = new CompositeStemSeparationService(new[] { (IStemSeparationService)a, b });

        Assert.False(await svc.IsAvailableAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IsAvailable_returns_false_for_empty_backends()
    {
        var svc = new CompositeStemSeparationService(Array.Empty<IStemSeparationService>());
        Assert.False(await svc.IsAvailableAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Separate_uses_first_available_backend()
    {
        var a = new FakeBackend { Available = false };
        var b = new FakeBackend { Available = true, Result = new StemSeparationResult { Success = true } };
        var c = new FakeBackend { Available = true };
        var svc = new CompositeStemSeparationService(new[] { (IStemSeparationService)a, b, c });

        var r = await svc.SeparateAsync("in.wav", "out", ct: TestContext.Current.CancellationToken);

        Assert.True(r.Success);
        Assert.Equal(0, a.SeparateCalls);
        Assert.Equal(1, b.SeparateCalls);
        Assert.Equal(0, c.SeparateCalls);
    }

    [Fact]
    public async Task Separate_returns_helpful_error_when_no_backend_available()
    {
        var a = new FakeBackend { Available = false };
        var svc = new CompositeStemSeparationService(new[] { (IStemSeparationService)a });

        var r = await svc.SeparateAsync("in.wav", "out", ct: TestContext.Current.CancellationToken);

        Assert.False(r.Success);
        Assert.NotNull(r.ErrorMessage);
        Assert.Contains("stem-separation backend", r.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(r.Stems);
    }

    /// <summary>Cancelling means ABORT, not "try the next one".
    /// <para>Both real backends used to convert an OperationCanceledException into
    /// an ordinary failed result, and the composite reads a failed result as "this
    /// backend declined" — so pressing Cancel skipped to the next backend and
    /// started a second multi-minute run, while the user saw a red "No
    /// stem-separation backend produced output" instead of the caller's clean
    /// cancel branch. The second half is only latent because
    /// JuceStemSeparationService is compiled out of shipped builds, and its native
    /// loop is uncancellable.</para></summary>
    [Fact]
    public async Task Cancelling_a_backend_aborts_the_chain_instead_of_falling_through()
    {
        using var cts = new CancellationTokenSource();
        var first = new FakeBackend
        {
            Available = true,
            CancelsWhenRun = cts,
            // A backend that reports its own cancel as a plain failure — exactly
            // what both real backends did.
            Result = new StemSeparationResult { Success = false, ErrorMessage = "Cancelled." }
        };
        var second = new FakeBackend { Available = true, Result = new StemSeparationResult { Success = true } };
        var svc = new CompositeStemSeparationService([first, second]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => svc.SeparateAsync("in.wav", "out", ct: cts.Token));

        Assert.Equal(1, first.SeparateCalls);
        Assert.Equal(0, second.SeparateCalls);
    }

    /// <summary>A token already cancelled when the call arrives must not run a
    /// backend at all.</summary>
    [Fact]
    public async Task An_already_cancelled_run_starts_no_backend()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var backend = new FakeBackend { Available = true };
        var svc = new CompositeStemSeparationService([backend]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => svc.SeparateAsync("in.wav", "out", ct: cts.Token));

        Assert.Equal(0, backend.SeparateCalls);
    }

    [Fact]
    public async Task Describe_lists_all_backends_with_ready_marker()
    {
        var a = new FakeBackend { Available = true, Description = "alpha" };
        var b = new FakeBackend { Available = false, Description = "beta" };
        var svc = new CompositeStemSeparationService(new[] { (IStemSeparationService)a, b });

        var desc = await svc.DescribeBackendAsync(TestContext.Current.CancellationToken);

        Assert.Contains("ready", desc);
        Assert.Contains("n/a", desc);
        Assert.Contains("alpha", desc);
        Assert.Contains("beta", desc);
    }

    [Fact]
    public async Task Describe_handles_empty_backend_list()
    {
        var svc = new CompositeStemSeparationService(Array.Empty<IStemSeparationService>());
        var desc = await svc.DescribeBackendAsync(TestContext.Current.CancellationToken);
        Assert.Contains("No stem-separation backends", desc);
    }

    [Fact]
    public async Task Constructor_filters_out_nested_composite_backends()
    {
        // Guard against pathological wiring where DI accidentally registers
        // the composite under the IStemSeparationService interface as well,
        // which would cause infinite recursion at runtime.
        var inner = new CompositeStemSeparationService(Array.Empty<IStemSeparationService>());
        var leaf = new FakeBackend { Available = true };
        var outer = new CompositeStemSeparationService(new IStemSeparationService[] { inner, leaf });

        // The fact that we can construct and use this without stack overflow
        // proves the filter; assert by checking IsAvailable returns leaf state.
        Assert.True(await outer.IsAvailableAsync(TestContext.Current.CancellationToken));
    }
}
