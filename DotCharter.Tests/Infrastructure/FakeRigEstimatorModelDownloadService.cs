using DotCharter.Services.Ai.Match.Model;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     A rig-estimator downloader that touches no disk and no network, for the
///     settings block and the tone panel's offer row.
///     <para>
///         It models the ONE thing those two surfaces care about and that a real
///         installer makes awkward to stage: that the install happens while the
///         caller is awaiting, so <c>IsInstalled</c> flips UNDER the view model
///         rather than being true from the start.
///     </para>
/// </summary>
public sealed class FakeRigEstimatorModelDownloadService : IRigEstimatorModelDownloadService
{
    /// <summary>Where it claims the model lives.</summary>
    public string ModelDirectory { get; init; } =
        Path.Combine(Path.GetTempPath(), "dc-fake-rig-estimator");

    /// <summary>Flipped by a successful <see cref="EnsureInstalledAsync" />.</summary>
    public bool IsInstalled { get; set; }

    /// <summary>What <see cref="InstalledModel" /> answers once installed.</summary>
    public RigEstimatorModelInfo? Info { get; set; }

    /// <inheritdoc />
    public RigEstimatorModelInfo? InstalledModel => IsInstalled ? Info : null;

    /// <inheritdoc />
    public int ApproximateMegabytes { get; init; } = 200;

    /// <summary>The outcome the next call returns. Null installs successfully.</summary>
    public string? FailWith { get; set; }

    /// <summary>Throws <see cref="OperationCanceledException" /> instead of answering.</summary>
    public bool ThrowCancelled { get; set; }

    /// <summary>Progress steps to report before answering.</summary>
    public IReadOnlyList<string> Steps { get; init; } = ["Downloading…", "Unpacking…"];

    /// <summary>How many times the download was asked for.</summary>
    public int Calls { get; private set; }

    /// <summary>Awaited by the call before it answers; lets a test observe the
    /// in-flight state.</summary>
    public TaskCompletionSource? Gate { get; set; }

    /// <inheritdoc />
    public async Task<RigEstimatorModelInstallResult> EnsureInstalledAsync(
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        Calls++;
        foreach (var step in Steps) progress?.Report(step);

        if (Gate is not null) await Gate.Task.ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (ThrowCancelled) throw new OperationCanceledException();

        if (FailWith is not null) return RigEstimatorModelInstallResult.Failed(FailWith);

        IsInstalled = true;
        return RigEstimatorModelInstallResult.Ok;
    }

    // ---- the tone library ------------------------------------------------------

    /// <summary>Where it claims the library file lives.</summary>
    public string LibraryPath { get; init; } =
        Path.Combine(Path.GetTempPath(), "dc-fake-rig-library", "library.bin");

    /// <summary>Flipped by a successful <see cref="EnsureLibraryInstalledAsync" />.</summary>
    public bool IsLibraryInstalled { get; set; }

    /// <inheritdoc />
    public int LibraryApproximateMegabytes { get; init; } = 9;

    public int InstalledLibraryMegabytes { get; init; } = 7;

    /// <summary>How many times the library download was asked for.</summary>
    public int LibraryCalls { get; private set; }

    /// <inheritdoc />
    public async Task<RigEstimatorModelInstallResult> EnsureLibraryInstalledAsync(
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        LibraryCalls++;
        foreach (var step in Steps) progress?.Report(step);

        if (Gate is not null) await Gate.Task.ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (ThrowCancelled) throw new OperationCanceledException();

        if (FailWith is not null) return RigEstimatorModelInstallResult.Failed(FailWith);

        IsLibraryInstalled = true;
        return RigEstimatorModelInstallResult.Ok;
    }
}
