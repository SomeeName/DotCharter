using DotCharter.Services.Ai.Match.Search;

namespace DotCharter.Tests.Ai.Search;

/// <summary>The optimiser on its own, on the standard benchmarks. Everything the
/// matcher claims about knob recovery rests on this converging.</summary>
public sealed class CmaEsTests
{
    /// <summary>The search box is [0,1]^n; the benchmarks live on [-5,5]^n, so the
    /// objectives map the unit coordinates onto that. Sphere's optimum lands at
    /// 0.5 and Rosenbrock's at 0.6 — both interior, so a run that reaches the
    /// optimum is doing so by SEARCHING and not by being clamped onto a face.</summary>
    private static double[] ToBox(double[] u)
    {
        var x = new double[u.Length];
        for (var i = 0; i < u.Length; i++) x[i] = -5.0 + 10.0 * u[i];
        return x;
    }

    private static double Sphere(double[] u)
    {
        var x = ToBox(u);
        var s = 0.0;
        foreach (var v in x) s += v * v;
        return s;
    }

    private static double Rosenbrock(double[] u)
    {
        var x = ToBox(u);
        var s = 0.0;
        for (var i = 0; i < x.Length - 1; i++)
        {
            var a = 1.0 - x[i];
            var b = x[i + 1] - x[i] * x[i];
            s += a * a + 100.0 * b * b;
        }

        return s;
    }

    // Measured 2026-09-12 in this worktree (Debug, single core, LOW priority):
    //   sphere 6-D      → 1e-3 in   387 evaluations; 1.07e-20 at the 2,000 budget
    //   Rosenbrock 6-D  → 1e-3 in 2,196 evaluations; 1.46e-28 at the 8,000 budget
    // Re-measure rather than quoting these; they move with the strategy constants.
    [Fact]
    public void Sphere6D_ReachesTheOptimum()
    {
        var start = Enumerable.Repeat(0.8, 6).ToArray();
        var r = CmaEs.Minimize(Sphere, start, new CmaEsOptions(Seed: 1, MaxEvaluations: 2000, TolFun: 0), TestContext.Current.CancellationToken);
        Assert.True(r.BestValue < 1e-3, $"sphere best={r.BestValue:E3} after {r.Evaluations} evals ({r.StopReason})");
        foreach (var u in r.Best) Assert.InRange(u, 0.49, 0.51);
    }

    [Fact]
    public void Rosenbrock6D_ReachesTheOptimum()
    {
        var start = Enumerable.Repeat(0.4, 6).ToArray();
        var r = CmaEs.Minimize(Rosenbrock, start, new CmaEsOptions(Seed: 2, MaxEvaluations: 8000, TolFun: 0), TestContext.Current.CancellationToken);
        Assert.True(r.BestValue < 1e-3,
            $"rosenbrock best={r.BestValue:E3} after {r.Evaluations} evals ({r.StopReason})");
    }

    [Fact]
    public void IsDeterministicForASeed_AndDiffersAcrossSeeds()
    {
        var start = Enumerable.Repeat(0.7, 4).ToArray();
        var a = CmaEs.Minimize(Rosenbrock, start, new CmaEsOptions(Seed: 5, MaxEvaluations: 400), TestContext.Current.CancellationToken);
        var b = CmaEs.Minimize(Rosenbrock, start, new CmaEsOptions(Seed: 5, MaxEvaluations: 400), TestContext.Current.CancellationToken);
        var c = CmaEs.Minimize(Rosenbrock, start, new CmaEsOptions(Seed: 6, MaxEvaluations: 400), TestContext.Current.CancellationToken);

        Assert.Equal(a.BestValue, b.BestValue);
        Assert.Equal(a.Best, b.Best);
        Assert.NotEqual(a.BestValue, c.BestValue);
    }

    [Fact]
    public void NeverExceedsTheEvaluationBudget_AndNeverLeavesTheBox()
    {
        var calls = 0;
        var r = CmaEs.Minimize(u =>
        {
            calls++;
            foreach (var v in u) Assert.InRange(v, 0.0, 1.0);
            return Sphere(u);
        }, Enumerable.Repeat(0.2, 5).ToArray(), new CmaEsOptions(Seed: 3, MaxEvaluations: 97), TestContext.Current.CancellationToken);

        Assert.True(calls <= 97, $"{calls} evaluations for a budget of 97");
        Assert.Equal(calls, r.Evaluations);
    }

    [Fact]
    public void StopsEarlyOnATargetValue()
    {
        var r = CmaEs.Minimize(Sphere, Enumerable.Repeat(0.55, 4).ToArray(),
            new CmaEsOptions(Seed: 4, MaxEvaluations: 4000, TargetValue: 1e-2), TestContext.Current.CancellationToken);
        Assert.Equal("target", r.StopReason);
        Assert.True(r.Evaluations < 4000);
    }

    [Fact]
    public async Task HonoursCancellationBetweenEvaluations()
    {
        using var cts = new CancellationTokenSource();
        var calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CmaEs.MinimizeAsync(
            (u, _) =>
            {
                if (++calls == 3) cts.Cancel();
                return new ValueTask<double>(Sphere(u));
            },
            Enumerable.Repeat(0.5, 3).ToArray(),
            new CmaEsOptions(Seed: 9, MaxEvaluations: 1000),
            cts.Token));
        Assert.True(calls < 20, $"kept evaluating after cancellation: {calls}");
    }

    [Fact]
    public void ANaNObjective_DoesNotPoisonTheRanking()
    {
        // A render that produced silence makes the distance NaN; those points must
        // sort LAST, not become the "best".
        var flip = 0;
        var r = CmaEs.Minimize(u => ++flip % 3 == 0 ? double.NaN : Sphere(u),
            Enumerable.Repeat(0.6, 4).ToArray(), new CmaEsOptions(Seed: 11, MaxEvaluations: 800), TestContext.Current.CancellationToken);
        Assert.False(double.IsNaN(r.BestValue));
        Assert.True(r.BestValue < 1.0, $"best={r.BestValue:E3}");
    }
}
