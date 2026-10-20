using System.Diagnostics;
using DotCharter.Services.Audio.StemSeparation.GuitarRoles;
using JuceSharp.AudioIo;

namespace DotCharter.Tests.Services.Audio.GuitarRoles;

/// <summary>
///     Measures the Guitar roles split on REAL separated guitar stems: the energy
///     split, the stereo statistics, the verdict and the time it took. Skips unless
///     <c>DOTCHARTER_GUITAR_ROLES_PROBE</c> names one or more WAV files
///     (<c>;</c>-separated). The files are COPIED into a scratch folder first, so
///     the probe never writes beside a stem it was pointed at. Run it at low
///     priority on half the cores; it asserts nothing about time.
/// </summary>
public sealed class GuitarRolesRealStemProbeTests(ITestOutputHelper output)
{
    public const string EnvVar = "DOTCHARTER_GUITAR_ROLES_PROBE";

    [Fact]
    public void ProbeRealGuitarStems()
    {
        var spec = Environment.GetEnvironmentVariable(EnvVar);
        if (string.IsNullOrWhiteSpace(spec)) Assert.Skip($"Set {EnvVar} to one or more guitar stem WAVs.");

        var scratch = Path.Combine(Path.GetTempPath(), "dotcharter-guitar-roles-probe", Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var source in spec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var stems = Path.Combine(scratch, Path.GetFileNameWithoutExtension(source), "stems");
                Directory.CreateDirectory(stems);
                var guitar = Path.Combine(stems, "guitar.wav");
                File.Copy(source, guitar);

                var service = new GuitarRoleStemService();
                var clock = Stopwatch.StartNew();
                var pre = service.Assess(guitar, TestContext.Current.CancellationToken);
                var preMs = clock.Elapsed.TotalMilliseconds;
                clock.Restart();
                var result = service.Derive(guitar, ct: TestContext.Current.CancellationToken);
                var splitS = clock.Elapsed.TotalSeconds;

                var a = result.Assessment;
                var line = $"{Path.GetFileName(source)}: {a.Stats.Frames} frames; correlation {a.Stats.Correlation:0.000}, " +
                           $"side share {a.Stats.SideShare:0.000}, level {a.Stats.RmsDbfs:0.0} dBFS; pre-check {pre.Verdict} in {preMs:0} ms; " +
                           $"split {a.Verdict} in {splitS:0.0} s; lead share {a.LeadShare:0.000}";
                if (result.Pair is { } pair)
                {
                    var lead = Energy(pair.LeadPath, out var leadCorr);
                    var rhythm = Energy(pair.RhythmPath, out var rhythmCorr);
                    line += $"; lead {10 * Math.Log10(lead / (lead + rhythm)):0.0} dB / rhythm {10 * Math.Log10(rhythm / (lead + rhythm)):0.0} dB of the split energy; " +
                            $"L/R correlation lead {leadCorr:0.000}, rhythm {rhythmCorr:0.000}";
                }

                output.WriteLine(line);
            }
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); }
            catch (IOException) { /* best effort */ }
        }
    }

    private static double Energy(string path, out double correlation)
    {
        var ch = WavIo.Read(path).SplitChannels();
        double ll = 0, rr = 0, lr = 0;
        for (var i = 0; i < ch[0].Length; i++)
        {
            ll += ch[0][i] * (double)ch[0][i];
            rr += ch[1][i] * (double)ch[1][i];
            lr += ch[0][i] * (double)ch[1][i];
        }

        correlation = lr / Math.Sqrt(Math.Max(ll * rr, 1e-30));
        return ll + rr;
    }
}
