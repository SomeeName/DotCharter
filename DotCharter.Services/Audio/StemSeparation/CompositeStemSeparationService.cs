namespace DotCharter.Services.Audio.StemSeparation;
/// <summary>
/// Picks the first available <see cref="IStemSeparationService"/> from an
/// ordered list.
/// <para><b>What the host registers is the managed ONNX backend first and the
/// bundled native demucs.cpp second</b> (<c>App.AudioServices</c>), and those two
/// are the whole chain. There is no Python demucs fallback: the CLI wrapper and
/// the JuceSharp orchestrator that would have been one were never registered and
/// were deleted.</para>
/// </summary>
public sealed class CompositeStemSeparationService : IStemSeparationService
{
    private readonly IReadOnlyList<IStemSeparationService> _backends;
    private readonly IAppLogger? _logger;
    private readonly InFlightStemSeparationTracker? _tracker;

    public CompositeStemSeparationService(
        IEnumerable<IStemSeparationService> backends,
        IAppLogger? logger = null,
        InFlightStemSeparationTracker? tracker = null)
    {
        _backends = backends.Where(b => b is not CompositeStemSeparationService).ToArray();
        _logger = logger;
        _tracker = tracker;
    }

    /// <summary>The chain this composite routes through, in order.</summary>
    public IReadOnlyList<IStemSeparationService> Backends => _backends;

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        foreach (var b in _backends)
            if (await b.IsAvailableAsync(ct).ConfigureAwait(false)) return true;
        return false;
    }

    public async Task<string> DescribeBackendAsync(CancellationToken ct = default)
    {
        var lines = new List<string>();
        foreach (var b in _backends)
        {
            var ok = await b.IsAvailableAsync(ct).ConfigureAwait(false);
            var desc = await b.DescribeBackendAsync(ct).ConfigureAwait(false);
            lines.Add($"[{(ok ? "ready" : "n/a ")}] {b.GetType().Name}: {desc}");
        }
        return lines.Count == 0
            ? "No stem-separation backends registered."
            : string.Join(Environment.NewLine, lines);
    }

    public async Task<StemSeparationResult> SeparateAsync(
        string inputAudioPath,
        string outputDirectory,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        // Status-bar progress/cancel presentation is owned by the caller
        // (MainWindowViewModel) — the composite only routes to a backend.
        using var _ = _tracker?.BeginRun();
        return await SeparateInnerAsync(inputAudioPath, outputDirectory, progress, ct).ConfigureAwait(false);
    }

    private async Task<StemSeparationResult> SeparateInnerAsync(
        string inputAudioPath,
        string outputDirectory,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        var skipped = new List<string>();
        foreach (var b in _backends)
        {
            // Cancel means ABORT, not "try the next one". The loop never checked
            // the token, and a backend can convert OperationCanceledException
            // into an ordinary failed result — which the soft-skip below would then read as
            // "this backend declined" and answered by starting the NEXT one. The
            // user's Cancel produced a red "No stem-separation backend produced
            // output. Tried: …" instead of the caller's clean cancel branch, and
            // began a second multi-minute run. Both halves are now closed at the
            // source: ABI v36 made the native loop CANCELLABLE (the progress
            // callback returns 0 to stop) and JuceStemSeparationService passes
            // the token down and rethrows OperationCanceledException instead of
            // converting it, exactly as the ONNX backend does. This check stays
            // regardless — it is what stops the loop between backends, and a
            // future backend that gets the rule wrong lands here.
            ct.ThrowIfCancellationRequested();

            var name = b.GetType().Name;
            bool ok;
            try { ok = await b.IsAvailableAsync(ct).ConfigureAwait(false); }
            catch (Exception ex)
            {
                _logger?.Info(AppLogCategory.Audio, $"Stem separation: backend {name} threw during availability probe ({ex.Message}). Skipping.");
                skipped.Add($"{name}: probe failed ({ex.Message})");
                continue;
            }
            if (!ok)
            {
                string why;
                try { why = await b.DescribeBackendAsync(ct).ConfigureAwait(false); } catch { why = "unavailable"; }
                _logger?.Info(AppLogCategory.Audio, $"Stem separation: backend {name} not available — {why}");
                skipped.Add($"{name}: {why}");
                continue;
            }
            _logger?.Info(AppLogCategory.Audio, $"Stem separation: using backend {name}.");
            var result = await b.SeparateAsync(inputAudioPath, outputDirectory, progress, ct).ConfigureAwait(false);
            if (result.Success) return result;
            // Second-line defence for a backend that still reports a cancelled run
            // as a plain failure: without this the soft-skip below would advance.
            ct.ThrowIfCancellationRequested();
            // Backend was "available" but the actual run refused (e.g. ONNX
            // Phase A scaffold that intentionally defers). Treat as soft-skip
            // and continue down the chain.
            _logger?.Info(AppLogCategory.Audio, $"Stem separation: backend {name} did not produce stems — {result.ErrorMessage}. Trying next backend.");
            skipped.Add($"{name}: {result.ErrorMessage}");
        }
        return new StemSeparationResult
        {
            Success = false,
            ErrorMessage = "No stem-separation backend produced output. Tried: " +
                string.Join(" | ", skipped),
            Stems = Array.Empty<StemTrack>(),
        };
    }
}
