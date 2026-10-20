using System.IO.Compression;

namespace DotCharter.Services.Export.DynamicDifficulty;

/// <summary>
///     DLC Builder's MLModel level count (<c>LevelCountGeneration.MLModel</c>) without
///     ML.NET: Rocksmith2014.NET's <c>src/Rocksmith2014.DD.Model/MLModel.zip</c> (MIT,
///     Copyright (c) 2020 Tapio Malmberg — see THIRD-PARTY-NOTICES.md § 2.1) is a FastTree
///     regression ensemble (500 trees, bias 0, every weight 1) over a featurizer that
///     one-hot encodes <c>solo</c> ("0", "1"), hash-encodes <c>path</c> into 65,536 slots and
///     concatenates twenty numeric columns. <c>dd-level-count-model.gz</c> holds the
///     trees exactly as ML.NET loaded them (thresholds as float, leaf values as double),
///     dumped with ML.NET 4.0.0's <c>TrainedTreeEnsemble</c>; the three path slots
///     (lead 13671, rhythm 55128, bass 13340) were read off ML.NET's own featurizer.
///     <see cref="Predict" /> walks each tree as FastTree does (<c>value &lt;= threshold</c>
///     goes to the "lte" child) and sums the leaves in double; the score is cast to float
///     as ML.NET returns it. <c>DdLevelCountModelTests</c> holds it to 2,000 ML.NET
///     predictions.
/// </summary>
internal sealed class DdLevelCountModel
{
    // Feature slots of ML.NET's concatenated "Features" vector.
    private const int SoloSlot0 = 0;
    private const int NumericBase = 65538;
    private const int NumericCount = 20;

    private static readonly Lazy<DdLevelCountModel> Shared = new(Load);

    private readonly double _bias;
    private readonly Tree[] _trees;

    private DdLevelCountModel(double bias, Tree[] trees)
    {
        _bias = bias;
        _trees = trees;
    }

    public static DdLevelCountModel Instance => Shared.Value;

    /// <summary>ML.NET's <c>ModelInput</c> columns as <c>LevelCounter.predictLevelCount</c> fills
    /// them, in feature order (lengthMs … maxChordStrings). The F# passes the REPEATED chord
    /// count as "chords" and leaves "repeatedChords", "ignored", "levels" and "uniqueLevels"
    /// at 0; kept as it is.</summary>
    public static float[] Numerics(DdPhraseData p) =>
    [
        p.LengthMs, p.Beats.Count, p.TempoEstimate, p.NoteCount, p.RepeatedNotes, p.RepeatedChords, 0f, p.TechCount,
        p.PalmMuteCount, p.BendCount, p.HarmonicCount, p.PinchHarmonicCount, p.TapCount, p.TremoloCount,
        p.VibratoCount, p.SlideCount, p.UnpitchedSlideCount, 0f, p.Anchors.Count, p.MaxChordStrings,
    ];

    /// <summary><c>predictLevelCount</c>: the rounded score, clamped to 2..30.</summary>
    public int LevelCount(int path, DdPhraseData phrase)
    {
        var score = Predict(path, phrase.SoloPhrase, Numerics(phrase));
        var levels = (int)Math.Round(score);
        return Math.Clamp(levels, DdLevelCounter.MinimumNumberOfLevelsToGenerate, DdLevelCounter.MaximumPossibleLevels);
    }

    /// <summary>The model's score for one input (<paramref name="numerics" /> in feature order).</summary>
    public float Predict(int path, bool solo, IReadOnlyList<float> numerics)
    {
        if (numerics.Count != NumericCount) throw new ArgumentException($"Expected {NumericCount} numeric features.", nameof(numerics));
        var pathSlot = path switch
        {
            0 => 13671,
            1 => 55128,
            2 => 13340,
            _ => -1,
        };

        float Feature(int index)
        {
            if (index >= NumericBase) return numerics[index - NumericBase];
            if (index == SoloSlot0) return solo ? 0f : 1f;
            if (index == SoloSlot0 + 1) return solo ? 1f : 0f;
            return index == pathSlot ? 1f : 0f;
        }

        var output = _bias;
        foreach (var tree in _trees) output += tree.Leaf(Feature) * tree.Weight;
        return (float)output;
    }

    private static DdLevelCountModel Load()
    {
        using var stream = typeof(DdLevelCountModel).Assembly.GetManifestResourceStream(
                               "DotCharter.Services.Export.DynamicDifficulty.dd-level-count-model.gz")
                           ?? throw new InvalidOperationException("The DD level-count model resource is missing.");
        using var gzip = new GZipStream(stream, CompressionMode.Decompress);
        using var reader = new BinaryReader(gzip);
        if (new string(reader.ReadChars(4)) != "DDLC" || reader.ReadInt32() != 1)
            throw new InvalidDataException("Unknown DD level-count model format.");
        var bias = reader.ReadDouble();
        var trees = new Tree[reader.ReadInt32()];
        for (var t = 0; t < trees.Length; t++)
        {
            var weight = reader.ReadDouble();
            var nodes = reader.ReadInt32();
            var lte = new short[nodes];
            var gt = new short[nodes];
            var feature = new int[nodes];
            var threshold = new float[nodes];
            for (var n = 0; n < nodes; n++)
            {
                lte[n] = reader.ReadInt16();
                gt[n] = reader.ReadInt16();
                feature[n] = reader.ReadInt32();
                threshold[n] = reader.ReadSingle();
            }

            var leaves = new double[reader.ReadInt32()];
            for (var l = 0; l < leaves.Length; l++) leaves[l] = reader.ReadDouble();
            trees[t] = new Tree(weight, lte, gt, feature, threshold, leaves);
        }

        return new DdLevelCountModel(bias, trees);
    }

    private sealed record Tree(double Weight, short[] Lte, short[] Gt, int[] Feature, float[] Threshold, double[] Leaves)
    {
        public double Leaf(Func<int, float> feature)
        {
            if (Lte.Length == 0) return Leaves[0];
            var node = 0;
            while (node >= 0)
                node = feature(Feature[node]) <= Threshold[node] ? Lte[node] : Gt[node];
            return Leaves[~node];
        }
    }
}
