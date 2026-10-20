namespace DotCharter.Tone.Audio.Dsp.RsToneConversion;

/// <summary>How faithfully one gear item / knob made it onto the in-house rig.</summary>
public enum ConversionNoteKind
{
    /// <summary>Mapped cleanly to a target module/param.</summary>
    Mapped,

    /// <summary>Reproduced with a different module or a lossy fold (e.g. phaser →
    /// flanger, graphic EQ → 3-band).</summary>
    Approximated,

    /// <summary>No equivalent module — the gear was dropped (pitch, wah, unknown).</summary>
    Skipped,

    /// <summary>Gear/knob was unknown to the catalogue; routed on Type/Category
    /// or defaults only.</summary>
    Fallback
}

/// <summary>One structured note about a conversion decision.</summary>
public sealed record ConversionNote(ConversionNoteKind Kind, string GearKey, string Message);

/// <summary>
/// The per-tone fidelity record: every dropped / approximated / fallback
/// decision the converter made, so the UI can show "N effects, M approximated"
/// and QA can audit coverage. Conversion never throws — it reports instead.
/// </summary>
public sealed class ConversionReport
{
    private readonly List<ConversionNote> _notes = [];

    public IReadOnlyList<ConversionNote> Notes => _notes;

    public void Add(ConversionNoteKind kind, string gearKey, string message)
        => _notes.Add(new ConversionNote(kind, gearKey, message));

    public int Count(ConversionNoteKind kind)
    {
        var n = 0;
        foreach (var note in _notes)
        {
            if (note.Kind == kind)
                n++;
        }

        return n;
    }

    public int ApproximatedCount => Count(ConversionNoteKind.Approximated);
    public int SkippedCount => Count(ConversionNoteKind.Skipped);
}
