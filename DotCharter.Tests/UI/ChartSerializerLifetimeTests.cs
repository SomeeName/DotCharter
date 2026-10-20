namespace DotCharter.Tests.UI;

/// <summary>
///     A chart serializer owns a writer, which owns an <c>RscpTemplateManager</c>.
///     Four call sites serialize on worker threads that can overlap — the save path,
///     the auto-backup timer, <c>RecoverySnapshotStore</c> and <c>CheckpointService</c>
///     — so a SINGLETON registration shares one writer between a save and a
///     concurrent auto-backup. Those types are stateless today, which made the
///     singleton safe purely by accident; the first mutable field added to either
///     would silently become cross-save corruption, and nothing about the
///     registration would have hinted at it.
///     <para>
///         This is a source lint rather than a behavioural test on purpose. The
///         registration lives in <c>App.axaml.cs</c> and reaching it at runtime means
///         booting the real Avalonia app session, which cannot share a testhost with the
///         gate (the reason <c>*UiPerfSmokeTests</c> runs as its own invocation).
///         The invariant is purely structural, so assert it structurally.
///     </para>
/// </summary>
public sealed class ChartSerializerLifetimeTests
{
    [Fact]
    public void EveryChartSerializerIsRegisteredTransient()
    {
        var src = CompositionRootSource();

        var registrations = src.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Contains("IChartSerializer") && l.Contains("services.AddKeyed"))
            .ToList();

        Assert.Equal(2, registrations.Count); // rscp + xml; a new one must be considered too

        foreach (var line in registrations)
        {
            Assert.True(line.Contains("AddKeyedTransient"),
                $"a chart serializer must not be shared between overlapping saves: {line}");
        }
    }

    /// <summary>
    ///     The composition root is ONE class spread over several partial files
    ///     (<c>App.axaml.cs</c> plus the topical <c>App.*.cs</c> slices), so a lint
    ///     over the registrations must read all of them — anchored on one file it
    ///     silently stops covering whichever slice a registration moved into.
    /// </summary>
    private static string CompositionRootSource()
    {
        return string.Concat(UiSourceTree
            .EnumerateFiles(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI"), "App*.cs")
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(UiSourceTree.ReadAllText));
    }
}
