using DotCharter.Services.Audio.StemSeparation.GuitarRoles;

namespace DotCharter.Tests.Services.Audio.GuitarRoles;

/// <summary>
///     The Guitar roles split on synthetic stereo signals whose parts are known:
///     what is centred must land in Lead, what is hard-panned in Rhythm, the two
///     must sum back to the input, and an image with nothing to split must be
///     refused rather than split into two copies.
/// </summary>
public sealed class CentreSideSplitterTests(ITestOutputHelper output)
{
    private const int Rate = 44100;
    private const int Length = 4 * Rate;

    // Measure away from the edges, where the STFT has fewer neighbours to smooth over.
    private const int From = Rate / 2;
    private const int To = Length - Rate / 2;

    [Fact]
    public void LeadPlusRhythm_IsTheInput()
    {
        var (l, r) = (Noise(1, 0.3f), Noise(2, 0.3f));
        AddInto(l, Noise(3, 0.3f));
        AddInto(r, Noise(3, 0.3f));

        var split = CentreSideSplitter.SplitAll(l, r);

        var worst = 0.0;
        for (var i = 0; i < Length; i++)
        {
            worst = Math.Max(worst, Math.Abs(split.LeadL[i] + split.RhythmL[i] - l[i]));
            worst = Math.Max(worst, Math.Abs(split.LeadR[i] + split.RhythmR[i] - r[i]));
        }

        output.WriteLine($"max |lead + rhythm - input| = {worst:E2}");
        Assert.True(worst < 1e-4, $"The split does not sum back to the input: max error {worst:E2}.");
    }

    [Fact]
    public void ACentredTone_GoesToLead_AndHardPannedTones_GoToRhythm()
    {
        var centre = Tone(440.0, 0.3f);
        var left = Tone(329.63, 0.3f);
        var right = Tone(554.37, 0.3f);
        var l = Sum(centre, left);
        var r = Sum(centre, right);

        var split = CentreSideSplitter.SplitAll(l, r);

        // Rejection: how far each part is pushed down in the output it does NOT belong to.
        var sideInLead = Math.Min(
            RejectionDb(Amplitude(left, 329.63), Amplitude(split.LeadL, 329.63)),
            RejectionDb(Amplitude(right, 554.37), Amplitude(split.LeadR, 554.37)));
        var centreInRhythm = Math.Min(
            RejectionDb(Amplitude(centre, 440.0), Amplitude(split.RhythmL, 440.0)),
            RejectionDb(Amplitude(centre, 440.0), Amplitude(split.RhythmR, 440.0)));
        // …and how much of each part survives in the output it DOES belong to.
        var centreKeptInLead = Amplitude(split.LeadL, 440.0) / Amplitude(centre, 440.0);
        var sideKeptInRhythm = Amplitude(split.RhythmL, 329.63) / Amplitude(left, 329.63);

        output.WriteLine($"side tones in Lead: -{sideInLead:0.0} dB; centre tone in Rhythm: -{centreInRhythm:0.0} dB; " +
                         $"centre kept {centreKeptInLead:0.000}, side kept {sideKeptInRhythm:0.000}; lead share {split.LeadShare:0.000}");
        Assert.True(sideInLead >= 40, $"Hard-panned tones leak into Lead at only -{sideInLead:0.0} dB.");
        Assert.True(centreInRhythm >= 40, $"The centred tone leaks into Rhythm at only -{centreInRhythm:0.0} dB.");
        Assert.InRange(centreKeptInLead, 0.98, 1.02);
        Assert.InRange(sideKeptInRhythm, 0.98, 1.02);
    }

    [Fact]
    public void ACentredLead_OverDoubleTrackedRhythm_SeparatesFarBetterThanAMidSideSum()
    {
        // The case the feature is for: three guitar-like parts (harmonic notes with a
        // pluck envelope, guitar range) playing at the same time — a lead in the
        // centre, and two DIFFERENT rhythm performances hard left and right. Their
        // spectra overlap, so position has to be read per bin and over time.
        var centre = Notes(10, 0.2f, 0.23);
        var sideL = Notes(11, 0.2f, 0.31);
        var sideR = Notes(12, 0.2f, 0.29);
        var l = Sum(centre, sideL);
        var r = Sum(centre, sideR);

        var split = CentreSideSplitter.SplitAll(l, r);
        var leadSdr = Math.Min(Sdr(centre, split.LeadL), Sdr(centre, split.LeadR));
        var rhythmSdr = Math.Min(Sdr(sideL, split.RhythmL), Sdr(sideR, split.RhythmR));

        // The crude answer this replaces: Lead = (L+R)/2, Rhythm = (L-R)/2 per side.
        var mid = new float[Length];
        var side = new float[Length];
        for (var i = 0; i < Length; i++)
        {
            mid[i] = 0.5f * (l[i] + r[i]);
            side[i] = 0.5f * (l[i] - r[i]);
        }

        var midSideSdr = Sdr(centre, mid);
        output.WriteLine($"lead vs true centre: {leadSdr:0.0} dB SDR; rhythm vs true sides: {rhythmSdr:0.0} dB SDR; " +
                         $"mid/side sum: {midSideSdr:0.0} dB; lead share {split.LeadShare:0.000}");
        Assert.True(leadSdr >= midSideSdr + 6,
            $"Lead ({leadSdr:0.0} dB) is not clearly better than a mid/side sum ({midSideSdr:0.0} dB).");
        Assert.True(leadSdr >= 9, $"Lead is only {leadSdr:0.0} dB from the centred part.");
        Assert.True(rhythmSdr >= 9, $"Rhythm is only {rhythmSdr:0.0} dB from the side parts.");
    }

    [Fact]
    public void AMonoStem_IsRefusedAsMono()
    {
        var mono = Sum(Noise(20, 0.3f), Tone(220, 0.2f));
        var assessment = Assess(mono, (float[])mono.Clone());

        output.WriteLine($"{assessment.Verdict}: correlation {assessment.Stats.Correlation:0.000}, side share {assessment.Stats.SideShare:0.0000}");
        Assert.Equal(GuitarRoleVerdict.Mono, assessment.Verdict);
        Assert.False(assessment.CanSplit);
    }

    [Fact]
    public void ACentredStemWithAFaintWidth_IsRefusedAsAllCentred()
    {
        // Not mono — a little room on the sides — but the split would hand back the
        // same guitar plus a residue. That is two copies of one thing, so: refused.
        var centre = Noise(30, 0.3f);
        var l = Sum(centre, Noise(31, 0.04f));
        var r = Sum(centre, Noise(32, 0.04f));
        var assessment = Assess(l, r);

        output.WriteLine($"{assessment.Verdict}: side share {assessment.Stats.SideShare:0.0000}, lead share {assessment.LeadShare:0.000}");
        Assert.Equal(GuitarRoleVerdict.AllCentred, assessment.Verdict);
    }

    [Fact]
    public void OnlyHardPannedGuitars_AreRefusedAsNothingCentred()
    {
        var assessment = Assess(Noise(40, 0.3f), Noise(41, 0.3f));

        output.WriteLine($"{assessment.Verdict}: lead share {assessment.LeadShare:0.000}");
        Assert.Equal(GuitarRoleVerdict.NothingCentred, assessment.Verdict);
    }

    [Fact]
    public void ARealImage_IsSplittable()
    {
        var centre = Noise(50, 0.25f);
        var assessment = Assess(Sum(centre, Noise(51, 0.25f)), Sum(centre, Noise(52, 0.25f)));

        Assert.Equal(GuitarRoleVerdict.Splittable, assessment.Verdict);
        Assert.True(assessment.CanSplit);
    }

    [Fact]
    public void Silence_IsRefusedAsSilent()
    {
        var assessment = Assess(new float[Length], new float[Length]);
        Assert.Equal(GuitarRoleVerdict.Silent, assessment.Verdict);
    }

    [Fact]
    public void StreamingInOddBlocks_GivesTheSameOutputAsOneBlock()
    {
        var centre = Noise(60, 0.2f);
        var l = Sum(centre, Tone(330, 0.2f));
        var r = Sum(centre, Noise(61, 0.2f));
        var whole = CentreSideSplitter.SplitAll(l, r);

        var splitter = new CentreSideSplitter();
        var leadL = new List<float>();
        var buf = new float[4][];
        for (var i = 0; i < 4; i++) buf[i] = new float[8192];
        var at = 0;
        var sizes = new[] { 1, 777, 1024, 5000, 13 };
        var s = 0;
        while (at < Length)
        {
            var n = Math.Min(sizes[s++ % sizes.Length], Length - at);
            splitter.Push(l.AsSpan(at, n), r.AsSpan(at, n));
            at += n;
            Drain(splitter, buf, leadL);
        }

        splitter.Complete();
        Drain(splitter, buf, leadL);

        Assert.Equal(Length, leadL.Count);
        for (var i = 0; i < Length; i++)
            Assert.True(leadL[i] == whole.LeadL[i], $"Sample {i} differs: {leadL[i]} vs {whole.LeadL[i]}.");
    }

    [Fact]
    public void AnInputShorterThanOneFrame_ComesBackWithItsOwnLength()
    {
        var l = Noise(70, 0.3f)[..1000];
        var r = Noise(71, 0.3f)[..1000];
        var split = CentreSideSplitter.SplitAll(l, r);
        Assert.Equal(1000, split.LeadL.Length);
        for (var i = 0; i < 1000; i++)
            Assert.True(Math.Abs(split.LeadL[i] + split.RhythmL[i] - l[i]) < 1e-4);
    }

    private static void Drain(CentreSideSplitter splitter, float[][] buf, List<float> into)
    {
        int n;
        while ((n = splitter.Read(buf[0], buf[1], buf[2], buf[3])) > 0)
            into.AddRange(buf[0].AsSpan(0, n).ToArray());
    }

    internal static GuitarRoleAssessment Assess(float[] l, float[] r)
    {
        var meter = new StereoImageMeter();
        meter.Add(l, r);
        var split = CentreSideSplitter.SplitAll(l, r);
        return GuitarRoleAssessment.FromSplit(meter.Result(), 2, split.LeadShare);
    }

    internal static float[] Tone(double hz, float amplitude)
    {
        var x = new float[Length];
        for (var i = 0; i < Length; i++) x[i] = amplitude * (float)Math.Sin(2 * Math.PI * hz * i / Rate);
        return x;
    }

    /// <summary>Guitar-like part: a run of random notes (82–660 Hz fundamentals, six
    /// partials falling 1/h, a 5 ms attack and an exponential decay).</summary>
    internal static float[] Notes(int seed, float amplitude, double noteSeconds)
    {
        var rng = new Random(seed);
        var x = new float[Length];
        var noteLength = (int)(noteSeconds * Rate);
        for (var start = 0; start < Length; start += noteLength)
        {
            var f0 = 82.0 * Math.Pow(2, rng.NextDouble() * Math.Log2(660.0 / 82.0));
            for (var i = 0; i < noteLength && start + i < Length; i++)
            {
                var t = (double)i / Rate;
                var env = Math.Min(1.0, t / 0.005) * Math.Exp(-3.0 * i / noteLength);
                double v = 0;
                for (var h = 1; h <= 6; h++)
                {
                    if (f0 * h >= Rate / 2.0) break;
                    v += Math.Sin(2 * Math.PI * f0 * h * t) / h;
                }

                x[start + i] = (float)(amplitude * env * v);
            }
        }

        return x;
    }

    internal static float[] Noise(int seed, float amplitude)
    {
        var rng = new Random(seed);
        var x = new float[Length];
        for (var i = 0; i < Length; i++) x[i] = amplitude * (float)(rng.NextDouble() * 2 - 1);
        return x;
    }

    internal static float[] Sum(float[] a, float[] b)
    {
        var x = new float[a.Length];
        for (var i = 0; i < a.Length; i++) x[i] = a[i] + b[i];
        return x;
    }

    private static void AddInto(float[] into, float[] add)
    {
        for (var i = 0; i < into.Length; i++) into[i] += add[i];
    }

    private static double Amplitude(float[] x, double hz)
    {
        double s = 0, c = 0;
        for (var i = From; i < To; i++)
        {
            var phase = 2 * Math.PI * hz * i / Rate;
            s += x[i] * Math.Sin(phase);
            c += x[i] * Math.Cos(phase);
        }

        var n = To - From;
        return 2 * Math.Sqrt(s * s + c * c) / n;
    }

    private static double RejectionDb(double input, double leaked)
        => 20 * Math.Log10(input / Math.Max(leaked, 1e-12));

    private static double Sdr(float[] truth, float[] estimate)
    {
        double signal = 0, error = 0;
        for (var i = From; i < To; i++)
        {
            signal += (double)truth[i] * truth[i];
            var e = estimate[i] - truth[i];
            error += e * e;
        }

        return 10 * Math.Log10(signal / Math.Max(error, 1e-20));
    }
}
