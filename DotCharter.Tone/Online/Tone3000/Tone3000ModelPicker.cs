namespace DotCharter.Tone.Online.Tone3000;

/// <summary>
///     Which of a tone's model files to download. ONE rule for the two places that
///     download: the capture browser's Download button and the AI designer's
///     capture search (<c>Tone3000CaptureSource</c>).
/// </summary>
public static class Tone3000ModelPicker
{
    /// <summary>
    ///     Picks which of a tone's models to download. The size tiers are the
    ///     same capture at different quality/CPU points, and the engine
    ///     defaults to the lite/feather tiers, so
    ///     prefer lite → feather → standard → nano within NAM, then any NAM
    ///     model, then the first entry.
    ///
    ///     <para><paramref name="architecture"/> narrows the pool FIRST when the
    ///     search asked for one — <c>GET /models?tone_id=</c> lists every
    ///     architecture of the tone, so a search filtered to A2 would otherwise
    ///     download the A1 file and the filter would look like it did nothing.
    ///     A tone with no model in the requested architecture keeps the full
    ///     pool rather than failing the download.</para>
    ///
    ///     <para>A model's <c>Format</c> is EMPTY on the current API (format is
    ///     a property of the parent tone), so empty reads as UNKNOWN and stays
    ///     eligible — otherwise every tier preference fell through to
    ///     <c>models[0]</c>.</para>
    /// </summary>
    public static Tone3000Model Pick(
        IReadOnlyList<Tone3000Model> models,
        Tone3000Architecture architecture = Tone3000Architecture.Default)
    {
        ArgumentNullException.ThrowIfNull(models);
        if (models.Count == 0) throw new ArgumentException("A tone with no models has nothing to pick.", nameof(models));

        var pool = models;
        if (Tone3000Wire.ArchitectureValue(architecture) is { } wanted)
        {
            var matching = models
                .Where(m => string.Equals(m.Architecture, wanted, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matching.Count > 0) pool = matching;
        }

        return Tier(pool, "lite") ?? Tier(pool, "feather") ?? Tier(pool, "standard") ?? Tier(pool, "nano")
               ?? pool.FirstOrDefault(IsNeuralCapture)
               ?? pool[0];

        static Tone3000Model? Tier(IReadOnlyList<Tone3000Model> pool, string tier) =>
            pool.FirstOrDefault(m => IsNeuralCapture(m) && m.SizeTier == tier);
    }

    /// <summary>True for a model <c>namamp</c> can load: a NAM file, or one whose
    /// format the API left empty (unknown, see <see cref="Pick" />).</summary>
    public static bool IsNeuralCapture(Tone3000Model model) => model.Format is "" or "nam";
}
