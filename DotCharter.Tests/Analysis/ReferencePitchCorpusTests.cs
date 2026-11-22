using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using DotCharter.Analysis.Tuning;
using DotCharter.Services.Export.Audio;

namespace DotCharter.Tests.Analysis;

/// <summary>
///     The A440 estimator (#821) on REAL songs: <c>TestData/EOF</c> folders holding a recording and a
///     Rocksmith XML whose <c>centOffset</c> is the song's known reference. Skips without the git-ignored
///     corpus. Every confident reading must land within <see cref="ToleranceCents" /> of the authored
///     residual, and at least half the songs must read confidently. One minute from each song's middle.
/// </summary>
public sealed class ReferencePitchCorpusTests(ITestOutputHelper output)
{
    private const double ToleranceCents = 8.0;
    private const int MaxSongs = 12;
    private const double Seconds = 60.0;

    [Fact]
    public void LocalOnly_CorpusSongs_MeasureTheirAuthoredOffset()
    {
        var corpus = FindCorpus();
        Assert.SkipWhen(corpus is null, "No TestData/EOF corpus (it is git-ignored).");
        var songs = Directory.EnumerateDirectories(corpus).Order(StringComparer.Ordinal)
            .Select(Song).OfType<(string Audio, double CentOffset)>().Take(MaxSongs).ToList();
        Assert.SkipWhen(songs.Count == 0, "TestData/EOF has no folder with a recording and a reference XML.");

        var confident = 0;
        var misses = new List<string>();
        for (var i = 0; i < songs.Count; i++)
        {
            var (audio, authored) = songs[i];
            var pcm = PcmAudioLoader.Load(audio, []);
            var estimate = ReferencePitchEstimator.Estimate(Middle(pcm), pcm.SampleRate, TestContext.Current.CancellationToken);
            var error = ReferencePitchEstimator.FoldToSemitone(estimate.Cents - ReferencePitchEstimator.FoldToSemitone(authored));
            output.WriteLine($"song {i + 1}: authored {authored:0.#}, measured {estimate.Cents:+0.0;-0.0}, " +
                $"confidence {estimate.Confidence:0.00}, peaks {estimate.PeakCount}");
            if (estimate.Confidence < ReferencePitchEstimator.DefaultMinConfidence) continue;
            confident++;
            if (Math.Abs(error) > ToleranceCents) misses.Add($"song {i + 1}: off by {error:+0.0;-0.0} cents");
        }

        Assert.True(misses.Count == 0, string.Join(Environment.NewLine, misses));
        Assert.True(confident * 2 >= songs.Count, $"only {confident} of {songs.Count} songs read confidently");
    }

    private static (string Audio, double CentOffset)? Song(string folder)
    {
        var audio = Directory.EnumerateFiles(folder, "*.wav")
            .Where(f => !Path.GetFileName(f).Contains("preview", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal).FirstOrDefault();
        if (audio == null) return null;
        foreach (var xml in Directory.EnumerateFiles(folder, "*_RS2*.xml").Order(StringComparer.Ordinal))
        {
            try
            {
                var value = XDocument.Load(xml).Root?.Element("centOffset")?.Value;
                if (double.TryParse(value, CultureInfo.InvariantCulture, out var cents)) return (audio, cents);
            }
            catch (XmlException)
            {
            }
        }

        return null;
    }

    private static float[] Middle(PcmAudio pcm)
    {
        var channels = Math.Max(1, pcm.Channels);
        var frames = pcm.Samples.Length / channels;
        var length = Math.Min(frames, (int)(Seconds * pcm.SampleRate));
        var start = (frames - length) / 2;
        var mono = new float[length];
        for (var f = 0; f < length; f++)
        {
            var sum = 0f;
            for (var c = 0; c < channels; c++) sum += pcm.Samples[(start + f) * channels + c];
            mono[f] = sum / (channels * 32768f);
        }

        return mono;
    }

    private static string? FindCorpus()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "TestData", "EOF");
            if (Directory.Exists(candidate)) return candidate;
        }

        return null;
    }
}
