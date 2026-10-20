using System.Globalization;
using System.IO.Compression;
using DotCharter.Services.Export.DynamicDifficulty;

namespace DotCharter.Tests.Services.Export.DynamicDifficulty;

/// <summary>
///     <see cref="DdLevelCountModel" /> returns ML.NET's own score, bit for bit.
///     <c>Fixtures/mlnet-predictions.csv.gz</c> was written by a throwaway console (outside
///     the repository) that loaded Rocksmith2014.NET's <c>MLModel.zip</c> with Microsoft.ML
///     4.0.0 + Microsoft.ML.FastTree 4.0.0, built the <c>PredictionEngine</c> DLC Builder's
///     <c>ConsumeModel</c> builds, and predicted 2,000 random inputs (seed 42; every path,
///     both solo values, counts beyond what a phrase holds). Columns: path, lengthMs,
///     lengthBeats, tempo, notes, repeatedNotes, chords, techCount, palmMutes, bends,
///     harmonics, pharmonics, taps, tremolos, vibratos, slides, unpSlides, anchors,
///     maxChordStrings, solo, score ("repeatedChords" and "ignored" were left at 0, as
///     <c>LevelCounter.predictLevelCount</c> leaves them).
/// </summary>
public sealed class DdLevelCountModelTests
{
    [Fact]
    public void Every_score_is_the_one_MLNET_predicted()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Services", "Export", "DynamicDifficulty", "Fixtures",
            "mlnet-predictions.csv.gz");
        using var reader = new StreamReader(new GZipStream(File.OpenRead(path), CompressionMode.Decompress));
        var rows = 0;
        while (reader.ReadLine() is { Length: > 0 } line)
        {
            var c = line.Split(',');
            var v = c.Take(19).Select(x => float.Parse(x, CultureInfo.InvariantCulture)).ToArray();
            float[] numerics = [v[1], v[2], v[3], v[4], v[5], v[6], 0f, v[7], v[8], v[9], v[10], v[11], v[12], v[13], v[14], v[15], v[16], 0f, v[17], v[18]];
            var expected = float.Parse(c[20], CultureInfo.InvariantCulture);

            var actual = DdLevelCountModel.Instance.Predict((int)v[0], c[19] == "1", numerics);

            Assert.True(BitConverter.SingleToInt32Bits(expected) == BitConverter.SingleToInt32Bits(actual),
                $"row {rows}: ML.NET {expected:R}, port {actual:R}");
            rows++;
        }

        Assert.Equal(2000, rows);
    }
}
