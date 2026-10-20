namespace DotCharter.Services.Ai.Match.Search;

/// <summary>How the search is run. Deterministic for a <see cref="Seed" />.</summary>
/// <param name="Seed">RNG seed. Two runs with the same seed, the same start point
/// and the same objective produce byte-identical trajectories.</param>
/// <param name="PopulationSize">λ. Null = the standard 4 + ⌊3·ln n⌋.</param>
/// <param name="InitialSigma">Step size in normalised units. 0.3 covers roughly a
/// third of every knob's travel, which is the right scale for a start point that
/// retrieval already put in the right neighbourhood.</param>
/// <param name="MaxEvaluations">The hard budget, in objective evaluations — which
/// for the matcher means RENDERS. Counted, not timed.</param>
/// <param name="TargetValue">Stop as soon as the objective reaches this.</param>
/// <param name="TolFun">Stop when the best-of-generation has moved less than this
/// over <see cref="StallGenerations" /> generations.</param>
/// <param name="StallGenerations">How many flat generations end the run.</param>
public sealed record CmaEsOptions(
    int Seed = 0,
    int? PopulationSize = null,
    double InitialSigma = 0.3,
    int MaxEvaluations = 400,
    double TargetValue = double.NegativeInfinity,
    double TolFun = 1e-12,
    int StallGenerations = 12);

/// <summary>The outcome: the best point seen, its value, what it cost and why it
/// stopped.</summary>
public sealed record CmaEsResult(double[] Best, double BestValue, int Evaluations, int Generations, string StopReason);

/// <summary>
///     A small, dependency-free CMA-ES over the unit box <c>[0,1]^n</c> — the
///     gradient-free optimiser ST-ITO (arXiv 2410.21233) uses over a
///     non-differentiable effect chain, and the one Okita 2026 searches knobs
///     with. Standard Hansen formulation (weighted recombination, rank-one +
///     rank-μ covariance update, cumulative step-size adaptation) with a Jacobi
///     eigensolver, so nothing outside the BCL is needed.
///     <para>
///         <b>Bounds are handled by REPAIR</b>: a sampled point is clamped into the
///         box before it is evaluated AND the clamped point is what enters the
///         recombination. The cheaper alternative — evaluating the clamped point
///         but updating from the unclamped one — lets the distribution's mean walk
///         arbitrarily far outside the box while every evaluation sees the same
///         face of it, and the run then reports a "best" that decodes to the same
///         rig as a point a hundred sigma away. Repair biases the distribution
///         towards the boundary, which for a knob at 0 or 1 is the correct answer.
///     </para>
///     <para>
///         <b>The budget is in EVALUATIONS, not wall time</b>, because an
///         evaluation is a render and a render's cost depends on the rig — a
///         <c>convreverb</c> + <c>namamp</c> chain is several times a
///         <c>gain</c> + <c>eq</c> one. A generation is never left half-evaluated:
///         the loop stops at the generation boundary that would exceed the budget,
///         so λ divides the work evenly and the covariance is never updated from a
///         partial population.
///     </para>
/// </summary>
public static class CmaEs
{
    /// <summary>Minimises a synchronous objective.</summary>
    public static CmaEsResult Minimize(
        Func<double[], double> objective,
        double[] initial,
        CmaEsOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(objective);
        // Every continuation completes synchronously, so this never blocks a
        // thread on real asynchrony.
        return MinimizeAsync((x, _) => new ValueTask<double>(objective(x)), initial, options, ct)
            .GetAwaiter().GetResult();
    }

    /// <summary>Minimises an asynchronous objective (the matcher's: render → features
    /// → distance). Cancellation is honoured between evaluations.</summary>
    public static async Task<CmaEsResult> MinimizeAsync(
        Func<double[], CancellationToken, ValueTask<double>> objective,
        double[] initial,
        CmaEsOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(objective);
        ArgumentNullException.ThrowIfNull(initial);
        ArgumentNullException.ThrowIfNull(options);
        var n = initial.Length;
        if (n == 0) throw new ArgumentException("Nothing to optimise.", nameof(initial));

        var lambda = options.PopulationSize ?? 4 + (int)Math.Floor(3.0 * Math.Log(n));
        lambda = Math.Max(4, lambda);
        var mu = lambda / 2;

        // Recombination weights.
        var w = new double[mu];
        var wsum = 0.0;
        for (var i = 0; i < mu; i++)
        {
            w[i] = Math.Log(mu + 0.5) - Math.Log(i + 1);
            wsum += w[i];
        }

        var w2 = 0.0;
        for (var i = 0; i < mu; i++)
        {
            w[i] /= wsum;
            w2 += w[i] * w[i];
        }

        var muEff = 1.0 / w2;

        // Strategy parameters.
        var cc = (4.0 + muEff / n) / (n + 4.0 + 2.0 * muEff / n);
        var cs = (muEff + 2.0) / (n + muEff + 5.0);
        var c1 = 2.0 / ((n + 1.3) * (n + 1.3) + muEff);
        var cmu = Math.Min(1.0 - c1, 2.0 * (muEff - 2.0 + 1.0 / muEff) / ((n + 2.0) * (n + 2.0) + muEff));
        var damps = 1.0 + 2.0 * Math.Max(0.0, Math.Sqrt((muEff - 1.0) / (n + 1.0)) - 1.0) + cs;
        var chiN = Math.Sqrt(n) * (1.0 - 1.0 / (4.0 * n) + 1.0 / (21.0 * n * n));

        var mean = new double[n];
        for (var i = 0; i < n; i++) mean[i] = Math.Clamp(initial[i], 0.0, 1.0);
        var sigma = options.InitialSigma;
        var pc = new double[n];
        var ps = new double[n];
        var c = Identity(n);
        var (b, d) = Eigen(c);

        var rng = new DeterministicGaussian(options.Seed);
        var best = (double[])mean.Clone();
        var bestValue = double.PositiveInfinity;
        var evaluations = 0;
        var generations = 0;
        var stall = 0;
        var lastBestOfGen = double.PositiveInfinity;
        var stop = "budget";

        var xs = new double[lambda][];
        var zs = new double[lambda][];
        var fs = new double[lambda];
        var idx = new int[lambda];

        while (evaluations + lambda <= options.MaxEvaluations)
        {
            ct.ThrowIfCancellationRequested();
            for (var k = 0; k < lambda; k++)
            {
                var z = new double[n];
                for (var i = 0; i < n; i++) z[i] = rng.NextGaussian();
                var y = new double[n];
                // y = B * (D .* z)
                for (var i = 0; i < n; i++)
                {
                    var acc = 0.0;
                    for (var j = 0; j < n; j++) acc += b[i][j] * d[j] * z[j];
                    y[i] = acc;
                }

                var x = new double[n];
                for (var i = 0; i < n; i++) x[i] = Math.Clamp(mean[i] + sigma * y[i], 0.0, 1.0);
                xs[k] = x;
                zs[k] = y;
            }

            for (var k = 0; k < lambda; k++)
            {
                ct.ThrowIfCancellationRequested();
                fs[k] = await objective(xs[k], ct).ConfigureAwait(false);
                evaluations++;
                if (!double.IsNaN(fs[k]) && fs[k] < bestValue)
                {
                    bestValue = fs[k];
                    best = (double[])xs[k].Clone();
                }
            }

            generations++;
            for (var k = 0; k < lambda; k++) idx[k] = k;
            Array.Sort(idx, (a2, b2) => Compare(fs[a2], fs[b2]));

            // Recombination — from the REPAIRED points, so the mean stays in the box.
            var oldMean = (double[])mean.Clone();
            Array.Clear(mean);
            for (var i = 0; i < mu; i++)
            {
                var xi = xs[idx[i]];
                for (var j = 0; j < n; j++) mean[j] += w[i] * xi[j];
            }

            // The realised step, expressed in the sampling basis.
            var yw = new double[n];
            for (var j = 0; j < n; j++) yw[j] = (mean[j] - oldMean[j]) / Math.Max(sigma, 1e-300);

            // ps = (1-cs) ps + sqrt(cs (2-cs) mueff) * C^(-1/2) yw
            var invSqrtYw = MulInvSqrt(b, d, yw, n);
            var psNorm = 0.0;
            for (var j = 0; j < n; j++)
            {
                ps[j] = (1.0 - cs) * ps[j] + Math.Sqrt(cs * (2.0 - cs) * muEff) * invSqrtYw[j];
                psNorm += ps[j] * ps[j];
            }

            psNorm = Math.Sqrt(psNorm);

            var hsig = psNorm / Math.Sqrt(1.0 - Math.Pow(1.0 - cs, 2.0 * generations)) / chiN
                       < 1.4 + 2.0 / (n + 1.0)
                ? 1.0
                : 0.0;

            for (var j = 0; j < n; j++)
                pc[j] = (1.0 - cc) * pc[j] + hsig * Math.Sqrt(cc * (2.0 - cc) * muEff) * yw[j];

            // Covariance: rank-one + rank-mu.
            var deltaH = (1.0 - hsig) * cc * (2.0 - cc);
            for (var i = 0; i < n; i++)
            {
                for (var j = 0; j <= i; j++)
                {
                    var rankMu = 0.0;
                    for (var k = 0; k < mu; k++)
                    {
                        var xk = xs[idx[k]];
                        var yi = (xk[i] - oldMean[i]) / Math.Max(sigma, 1e-300);
                        var yj = (xk[j] - oldMean[j]) / Math.Max(sigma, 1e-300);
                        rankMu += w[k] * yi * yj;
                    }

                    var v = (1.0 - c1 - cmu) * c[i][j]
                            + c1 * (pc[i] * pc[j] + deltaH * c[i][j])
                            + cmu * rankMu;
                    c[i][j] = v;
                    c[j][i] = v;
                }
            }

            sigma *= Math.Exp(cs / damps * (psNorm / chiN - 1.0));
            sigma = Math.Clamp(sigma, 1e-12, 10.0);

            (b, d) = Eigen(c);

            var bestOfGen = fs[idx[0]];
            if (Math.Abs(bestOfGen - lastBestOfGen) < options.TolFun) stall++;
            else stall = 0;
            lastBestOfGen = bestOfGen;

            if (bestValue <= options.TargetValue)
            {
                stop = "target";
                break;
            }

            if (stall >= options.StallGenerations)
            {
                stop = "stalled";
                break;
            }
        }

        return new CmaEsResult(best, bestValue, evaluations, generations, stop);
    }

    private static int Compare(double a, double b)
    {
        // A NaN objective (a render that produced silence, a distance over an
        // empty feature vector) sorts LAST rather than poisoning the ordering.
        var an = double.IsNaN(a);
        var bn = double.IsNaN(b);
        if (an && bn) return 0;
        if (an) return 1;
        if (bn) return -1;
        return a.CompareTo(b);
    }

    private static double[][] Identity(int n)
    {
        var m = new double[n][];
        for (var i = 0; i < n; i++)
        {
            m[i] = new double[n];
            m[i][i] = 1.0;
        }

        return m;
    }

    /// <summary>C^(-1/2) · v, from the eigen decomposition.</summary>
    private static double[] MulInvSqrt(double[][] b, double[] d, double[] v, int n)
    {
        var t = new double[n];
        for (var j = 0; j < n; j++)
        {
            var acc = 0.0;
            for (var i = 0; i < n; i++) acc += b[i][j] * v[i];
            t[j] = acc / Math.Max(d[j], 1e-20);
        }

        var r = new double[n];
        for (var i = 0; i < n; i++)
        {
            var acc = 0.0;
            for (var j = 0; j < n; j++) acc += b[i][j] * t[j];
            r[i] = acc;
        }

        return r;
    }

    /// <summary>Symmetric eigen decomposition by cyclic Jacobi rotations. Returns
    /// the eigenvector matrix and the SQUARE ROOTS of the eigenvalues (CMA-ES's
    /// D), floored at a tiny positive value so a numerically negative eigenvalue
    /// cannot produce a NaN sample.</summary>
    private static (double[][] B, double[] D) Eigen(double[][] c)
    {
        var n = c.Length;
        var a = new double[n][];
        for (var i = 0; i < n; i++) a[i] = (double[])c[i].Clone();
        var v = Identity(n);

        for (var sweep = 0; sweep < 64; sweep++)
        {
            var off = 0.0;
            for (var i = 0; i < n; i++)
            {
                for (var j = i + 1; j < n; j++)
                    off += a[i][j] * a[i][j];
            }

            if (off < 1e-24) break;

            for (var p = 0; p < n - 1; p++)
            {
                for (var q = p + 1; q < n; q++)
                {
                    if (Math.Abs(a[p][q]) < 1e-18) continue;
                    var theta = (a[q][q] - a[p][p]) / (2.0 * a[p][q]);
                    var t = Math.Sign(theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1.0));
                    if (theta == 0.0) t = 1.0;
                    var cos = 1.0 / Math.Sqrt(t * t + 1.0);
                    var sin = t * cos;

                    for (var k = 0; k < n; k++)
                    {
                        var akp = a[k][p];
                        var akq = a[k][q];
                        a[k][p] = cos * akp - sin * akq;
                        a[k][q] = sin * akp + cos * akq;
                    }

                    for (var k = 0; k < n; k++)
                    {
                        var apk = a[p][k];
                        var aqk = a[q][k];
                        a[p][k] = cos * apk - sin * aqk;
                        a[q][k] = sin * apk + cos * aqk;
                    }

                    for (var k = 0; k < n; k++)
                    {
                        var vkp = v[k][p];
                        var vkq = v[k][q];
                        v[k][p] = cos * vkp - sin * vkq;
                        v[k][q] = sin * vkp + cos * vkq;
                    }
                }
            }
        }

        var d = new double[n];
        for (var i = 0; i < n; i++) d[i] = Math.Sqrt(Math.Max(a[i][i], 1e-20));
        return (v, d);
    }

    /// <summary>A seeded Gaussian source. <see cref="Random" /> with an explicit
    /// seed is stable across runs of the same runtime; Box–Muller over it keeps the
    /// whole optimiser reproducible without a dependency.</summary>
    private sealed class DeterministicGaussian(int seed)
    {
        private readonly Random _rng = new(seed);
        private double _spare;
        private bool _hasSpare;

        public double NextGaussian()
        {
            if (_hasSpare)
            {
                _hasSpare = false;
                return _spare;
            }

            double u, v, s;
            do
            {
                u = _rng.NextDouble() * 2.0 - 1.0;
                v = _rng.NextDouble() * 2.0 - 1.0;
                s = u * u + v * v;
            } while (s is >= 1.0 or 0.0);

            var f = Math.Sqrt(-2.0 * Math.Log(s) / s);
            _spare = v * f;
            _hasSpare = true;
            return u * f;
        }
    }
}
