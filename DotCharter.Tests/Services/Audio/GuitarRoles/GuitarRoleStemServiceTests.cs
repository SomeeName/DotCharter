using DotCharter.Services.Audio.StemSeparation.GuitarRoles;
using JuceSharp.AudioIo;
using static DotCharter.Tests.Services.Audio.GuitarRoles.CentreSideSplitterTests;

namespace DotCharter.Tests.Services.Audio.GuitarRoles;

/// <summary>
///     The derived stems on disk: written once beside the guitar stem, reused while
///     the guitar stem is unchanged, re-derived (from the guitar stem, never by a
///     second separation) when it changes, and never written for an image that
///     cannot be split.
/// </summary>
public sealed class GuitarRoleStemServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dotcharter-guitar-roles-tests", Guid.NewGuid().ToString("N"));

    public GuitarRoleStemServiceTests() => Directory.CreateDirectory(Path.Combine(_dir, "stems"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string GuitarPath => Path.Combine(_dir, "stems", "guitar.wav");

    [Fact]
    public void ASplittableStem_IsDerivedBesideIt_AndTheTwoFilesSumBackToIt()
    {
        var (l, r) = WriteSplittableGuitar(WavSampleFormat.Float32);

        var result = new GuitarRoleStemService().Derive(GuitarPath, ct: Ct);

        Assert.Equal(GuitarRoleVerdict.Splittable, result.Assessment.Verdict);
        Assert.NotNull(result.Pair);
        Assert.Equal(Path.Combine(_dir, "stems", GuitarRoleStems.FolderName), Path.GetDirectoryName(result.Pair.LeadPath));
        var lead = WavIo.Read(result.Pair.LeadPath).SplitChannels();
        var rhythm = WavIo.Read(result.Pair.RhythmPath).SplitChannels();
        Assert.Equal(l.Length, lead[0].Length);
        var worst = 0.0;
        for (var i = 0; i < l.Length; i++)
        {
            worst = Math.Max(worst, Math.Abs(lead[0][i] + rhythm[0][i] - l[i]));
            worst = Math.Max(worst, Math.Abs(lead[1][i] + rhythm[1][i] - r[i]));
        }

        Assert.True(worst < 1e-4, $"The derived WAVs do not sum back to the guitar stem: max error {worst:E2}.");
        // Nothing lands at the top level, where the mixer would adopt it as a stem.
        Assert.Equal(["guitar.wav"], Directory.EnumerateFiles(Path.Combine(_dir, "stems")).Select(Path.GetFileName));
    }

    [Fact]
    public void ACurrentSplit_IsReused_NotRewritten()
    {
        WriteSplittableGuitar(WavSampleFormat.Pcm16);
        var service = new GuitarRoleStemService();
        var first = service.Derive(GuitarPath, ct: Ct).Pair!;
        var written = File.GetLastWriteTimeUtc(first.LeadPath);

        var again = service.Derive(GuitarPath, ct: Ct).Pair;
        var probed = service.TryGetDerived(GuitarPath);

        Assert.Equal(first, again);
        Assert.Equal(first, probed);
        Assert.Equal(written, File.GetLastWriteTimeUtc(first.LeadPath));
    }

    [Fact]
    public void ANewGuitarStem_MakesTheOldSplitStale_AndReDerivingReplacesIt()
    {
        WriteSplittableGuitar(WavSampleFormat.Float32, seed: 1);
        var service = new GuitarRoleStemService();
        var old = service.Derive(GuitarPath, ct: Ct).Pair!;

        WriteSplittableGuitar(WavSampleFormat.Float32, seed: 2); // a re-separation
        Assert.Null(service.TryGetDerived(GuitarPath));

        var fresh = service.Derive(GuitarPath, ct: Ct).Pair!;
        Assert.NotEqual(old.LeadPath, fresh.LeadPath);
        Assert.False(File.Exists(old.LeadPath), "The stale split was left behind.");
        Assert.True(File.Exists(fresh.LeadPath));
    }

    [Fact]
    public void ADeletedDerivedFile_IsNotServed_AndIsDerivedAgain()
    {
        WriteSplittableGuitar(WavSampleFormat.Float32);
        var service = new GuitarRoleStemService();
        var pair = service.Derive(GuitarPath, ct: Ct).Pair!;
        File.Delete(pair.RhythmPath);

        Assert.Null(service.TryGetDerived(GuitarPath));
        Assert.Equal(GuitarRoleVerdict.Plausible, service.Assess(GuitarPath, Ct).Verdict);
        Assert.Equal(pair, service.Derive(GuitarPath, ct: Ct).Pair);
        Assert.True(File.Exists(pair.RhythmPath));
    }

    [Fact]
    public void AMonoFile_IsRefused_AndNoAudioIsWritten()
    {
        var mono = Sum(Notes(5, 0.2f, 0.25), Noise(6, 0.05f));
        WavIo.WriteFloat32(GuitarPath, new AudioBuffer(mono, 44100, 1));
        var service = new GuitarRoleStemService();

        Assert.Equal(GuitarRoleVerdict.Mono, service.Assess(GuitarPath, Ct).Verdict);
        var result = service.Derive(GuitarPath, ct: Ct);

        Assert.Equal(GuitarRoleVerdict.Mono, result.Assessment.Verdict);
        Assert.Null(result.Pair);
        Assert.Empty(Directory.EnumerateFiles(GuitarRoleStems.FolderFor(GuitarPath), "*.wav"));
        // …and the refusal is remembered, so the next open shows it without a pass.
        Assert.Equal(GuitarRoleVerdict.Mono, service.Assess(GuitarPath, Ct).Verdict);
    }

    [Fact]
    public void AStereoFileWithIdenticalChannels_IsMono()
    {
        var x = Notes(7, 0.2f, 0.25);
        WriteStereo(x, (float[])x.Clone(), WavSampleFormat.Pcm16);

        Assert.Equal(GuitarRoleVerdict.Mono, new GuitarRoleStemService().Assess(GuitarPath, Ct).Verdict);
    }

    [Fact]
    public void ACentredImage_IsRefusedAfterTheSplit_AndTheVerdictIsRemembered()
    {
        var centre = Notes(8, 0.2f, 0.25);
        WriteStereo(Sum(centre, Noise(9, 0.02f)), Sum(centre, Noise(10, 0.02f)), WavSampleFormat.Float32);
        var service = new GuitarRoleStemService();

        Assert.Equal(GuitarRoleVerdict.Plausible, service.Assess(GuitarPath, Ct).Verdict);
        var result = service.Derive(GuitarPath, ct: Ct);

        Assert.Equal(GuitarRoleVerdict.AllCentred, result.Assessment.Verdict);
        Assert.Null(result.Pair);
        Assert.Empty(Directory.EnumerateFiles(GuitarRoleStems.FolderFor(GuitarPath), "*.wav*"));
        Assert.Equal(GuitarRoleVerdict.AllCentred, service.Assess(GuitarPath, Ct).Verdict);
    }

    [Fact]
    public void AnUnreadableFile_IsAVerdict_NotAnException()
    {
        File.WriteAllText(GuitarPath, "not a wave file");
        var service = new GuitarRoleStemService();

        Assert.Equal(GuitarRoleVerdict.Unreadable, service.Assess(GuitarPath, Ct).Verdict);
        Assert.Equal(GuitarRoleVerdict.Unreadable, service.Derive(GuitarPath, ct: Ct).Assessment.Verdict);
        Assert.Null(service.TryGetDerived(GuitarPath));
    }

    [Theory]
    [InlineData(WavSampleFormat.Pcm16, 1.0 / 32768)]
    [InlineData(WavSampleFormat.Pcm24, 1.0 / 8388608)]
    [InlineData(WavSampleFormat.Float32, 0)]
    public void TheStreamReader_ReadsWhatTheWriterWrote(WavSampleFormat format, double lsb)
    {
        var l = Noise(20, 0.5f)[..10000];
        var r = Noise(21, 0.5f)[..10000];
        WriteStereo(l, r, format);

        using var reader = StereoWavStreamReader.Open(GuitarPath);
        Assert.Equal(2, reader.Channels);
        Assert.Equal(10000, reader.Frames);
        var gotL = new float[3000];
        var gotR = new float[3000];
        var at = 0;
        int n;
        while ((n = reader.Read(gotL, gotR)) > 0)
        {
            for (var i = 0; i < n; i++)
            {
                Assert.True(Math.Abs(gotL[i] - l[at + i]) <= lsb, $"L[{at + i}]");
                Assert.True(Math.Abs(gotR[i] - r[at + i]) <= lsb, $"R[{at + i}]");
            }

            at += n;
        }

        Assert.Equal(10000, at);
    }

    private (float[] L, float[] R) WriteSplittableGuitar(WavSampleFormat format, int seed = 1)
    {
        var centre = Notes(seed * 10, 0.2f, 0.23);
        var l = Sum(centre, Notes(seed * 10 + 1, 0.2f, 0.31));
        var r = Sum(centre, Notes(seed * 10 + 2, 0.2f, 0.29));
        WriteStereo(l, r, format);
        return (l, r);
    }

    private void WriteStereo(float[] l, float[] r, WavSampleFormat format)
    {
        using var writer = new WavWriter(GuitarPath, 44100, 2, format);
        writer.WritePlanar([l, r]);
    }
}
