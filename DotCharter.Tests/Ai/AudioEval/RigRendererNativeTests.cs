using DotCharter.Services.Ai.Match;
using DotCharter.Services.Ai.Match.Similarity;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Tests.Ai.AudioEval;

/// <summary>
///     The renderer against the REAL engine on the null device. This is where the
///     determinism promise is actually worth something: the managed tests pin the
///     renderer's own rules, and these pin that the native chain, rebuilt per render,
///     gives the same samples back twice — including after a completely different rig
///     has gone through it in between, which is the exact sequence a search lane
///     produces and the exact one a reverb tail leaks across.
///     <para>
///         Skips, never fails, when <c>juce_sharp.dll</c> is not loadable; and shares
///         the <c>NativeEngine</c> collection with every other test that opens an
///         engine, because two engines on one box is how the ASIO device gets taken.
///     </para>
/// </summary>
[Collection("NativeEngine")]
public sealed class RigRendererNativeTests : IDisposable
{
    private readonly NullDeviceRigHost _host = new();

    public void Dispose() => _host.Dispose();

    private static DcRigPreset Clean() =>
        new([
            new DcRigModule("amp", new Dictionary<string, float> { ["gain"] = 0.15f }),
            new DcRigModule("cab", new Dictionary<string, float>())
        ]);

    private static DcRigPreset Dirty() =>
        new([
            new DcRigModule("fuzz", new Dictionary<string, float> { ["gain"] = 0.9f }),
            new DcRigModule("amp", new Dictionary<string, float> { ["gain"] = 0.8f }),
            new DcRigModule("cab", new Dictionary<string, float>())
        ]);

    private MonoAudio Di() => TrimTo(ReferenceDi.Generate(), 5.0);

    private static MonoAudio TrimTo(MonoAudio audio, double seconds)
    {
        var n = Math.Min(audio.Samples.Length, (int)(seconds * audio.SampleRate));
        var cut = new float[n];
        Array.Copy(audio.Samples, cut, n);
        return new MonoAudio(cut, audio.SampleRate);
    }

    [Fact]
    public async Task SameDiAndRig_AreBitIdentical_EvenAfterAnotherRigHasBeenRendered()
    {
        Assert.SkipUnless(_host.IsAvailable, "native DSP ABI (juce_sharp.dll) not loadable: " + _host.Describe());
        var renderer = new RigRenderer(_host);
        var di = Di();
        var ct = TestContext.Current.CancellationToken;

        var first = await renderer.RenderAsync(di, Dirty(), ct);
        await renderer.RenderAsync(di, Clean(), ct);
        var again = await renderer.RenderAsync(di, Dirty(), ct);

        Assert.Equal(first.Samples, again.Samples);
    }

    [Fact]
    public async Task AFuzzAndACleanAmp_AreFarApartOnTheMetric()
    {
        Assert.SkipUnless(_host.IsAvailable, "native DSP ABI (juce_sharp.dll) not loadable: " + _host.Describe());
        var renderer = new RigRenderer(_host);
        var similarity = new ToneSimilarity();
        var di = Di();
        var ct = TestContext.Current.CancellationToken;

        var clean = await renderer.RenderAsync(di, Clean(), ct);
        var dirty = await renderer.RenderAsync(di, Dirty(), ct);

        var rigDistance = similarity.Distance(clean, dirty);
        var riffDistance = similarity.Distance(
            clean, await renderer.RenderAsync(TrimTo(ReferenceDi.Generate(13), 5.0), Clean(), ct));

        Assert.True(rigDistance > riffDistance,
            $"through the REAL rig, a clean amp and a fuzz ({rigDistance:F4}) are no further apart than " +
            $"the same clean amp on two different riffs ({riffDistance:F4}).");
    }

    /// <summary>
    ///     A render must not be silence. A rig the applier failed to build, or an
    ///     engine that refused the buffer, both come back as a quiet, plausible array
    ///     of floats — and every distance computed from it would be a number.
    /// </summary>
    [Fact]
    public async Task ARenderIsAudible()
    {
        Assert.SkipUnless(_host.IsAvailable, "native DSP ABI (juce_sharp.dll) not loadable: " + _host.Describe());
        var renderer = new RigRenderer(_host);

        var wet = await renderer.RenderAsync(Di(), Dirty(), TestContext.Current.CancellationToken);

        double sq = 0;
        foreach (var s in wet.Samples) sq += s * (double)s;
        var rmsDb = 20 * Math.Log10(Math.Sqrt(sq / Math.Max(wet.Samples.Length, 1)) + 1e-12);
        Assert.True(rmsDb > -40, $"the render came back at {rmsDb:F1} dBFS — that is silence, not a rig.");
    }
}
