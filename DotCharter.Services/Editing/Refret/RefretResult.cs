namespace DotCharter.Services.Editing.Refret;

/// <summary>
///     Outcome of a refret run. Costs are cost-model totals (see
///     <see cref="RefretOptions" />); travel is the raw, unweighted sum of
///     |Δ anchor fret| between consecutive sounds — the number the status bar
///     reports as "hand-position travel −N%".
/// </summary>
public sealed class RefretResult
{
    /// <summary>Sounds (notes + chords) the optimizer considered.</summary>
    public int SoundsConsidered { get; set; }

    /// <summary>Single notes whose (string, fret) changed.</summary>
    public int NotesChanged { get; set; }

    /// <summary>Chords whose shape changed.</summary>
    public int ChordsChanged { get; set; }

    /// <summary>New chord templates appended for re-fretted shapes.</summary>
    public int TemplatesAdded { get; set; }

    /// <summary>FHP anchors regenerated for the affected range (0 when the level had none).</summary>
    public int AnchorsRegenerated { get; set; }

    /// <summary>Cost-model total of the original assignment.</summary>
    public double CostBefore { get; set; }

    /// <summary>Cost-model total of the applied assignment. Never exceeds <see cref="CostBefore" />.</summary>
    public double CostAfter { get; set; }

    /// <summary>Unweighted Σ|Δ anchor fret| before the refret.</summary>
    public double TravelBefore { get; set; }

    /// <summary>Unweighted Σ|Δ anchor fret| after the refret.</summary>
    public double TravelAfter { get; set; }

    public bool HasChanges => NotesChanged + ChordsChanged > 0;

    /// <summary>Percentage reduction in hand-position travel, clamped to [0, 100].</summary>
    public int TravelImprovementPercent => TravelBefore > 0
        ? Math.Clamp((int)Math.Round(100.0 * (TravelBefore - TravelAfter) / TravelBefore), 0, 100)
        : 0;

    /// <summary>Total re-fretted sound parts for the status message.</summary>
    public int SoundsChanged => NotesChanged + ChordsChanged;

    public void Accumulate(RefretResult other)
    {
        SoundsConsidered += other.SoundsConsidered;
        NotesChanged += other.NotesChanged;
        ChordsChanged += other.ChordsChanged;
        TemplatesAdded += other.TemplatesAdded;
        AnchorsRegenerated += other.AnchorsRegenerated;
        CostBefore += other.CostBefore;
        CostAfter += other.CostAfter;
        TravelBefore += other.TravelBefore;
        TravelAfter += other.TravelAfter;
    }
}
