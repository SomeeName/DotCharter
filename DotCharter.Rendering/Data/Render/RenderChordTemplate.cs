namespace DotCharter.Rendering.Data.Render;

public unsafe struct RenderChordTemplate
{
    public int Id;
    public string Name;

    /// <summary>
    ///     The FOLDED arpeggio answer — <c>ChordTemplate.IsArpeggio</c>, which is
    ///     <c>Arpeggio || ForceArpeggioInRs</c>.
    ///     Two flags spell arpeggio and the import paths set different ones, so
    ///     the projection folds them once here and every renderer reads this.
    /// </summary>
    public bool Arpeggio;
    public fixed sbyte Frets[8];
    public fixed sbyte Fingers[8];

    public int GetFret(int stringIndex)
    {
        if (stringIndex is < 0 or >= 8) return -1;
        return Frets[stringIndex];
    }

    public int GetFinger(int stringIndex)
    {
        if (stringIndex is < 0 or >= 8) return -1;
        return Fingers[stringIndex];
    }

    /// <summary>Safe writer for callers that may not use <c>unsafe</c> (the
    /// test project): a fixed buffer can only be indexed from unsafe code.
    /// Out-of-range strings are ignored, mirroring <see cref="GetFret" />.</summary>
    public void SetFret(int stringIndex, int fret)
    {
        if (stringIndex is < 0 or >= 8) return;
        Frets[stringIndex] = (sbyte)fret;
    }

    /// <summary>Safe writer; see <see cref="SetFret" />. Rocksmith convention:
    /// -1 no finger, 0 thumb, 1..4 index..pinky.</summary>
    public void SetFinger(int stringIndex, int finger)
    {
        if (stringIndex is < 0 or >= 8) return;
        Fingers[stringIndex] = (sbyte)finger;
    }
}
