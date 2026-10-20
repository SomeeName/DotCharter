using DotCharter.Services.Export.Sng.Model;

namespace DotCharter.Services.Export.Package;

/// <summary>
/// Technique ID detection for the manifest technique map.
/// Ported from Rocksmith2014.NET Manifest/Techniques.fs.
/// </summary>
internal static class RsTechniques
{
    private const uint ChordNotesDoubleStop = RsNoteMask.DoubleStop | RsNoteMask.ChordNotes;

    private static bool HasFlag(RsNote note, uint flag) => (note.Mask & flag) == flag;

    private static bool IsPowerChord(RocksmithSng sng, RsNote note)
    {
        if (!HasFlag(note, RsNoteMask.DoubleStop)) return false;
        var frets = sng.Chords[note.ChordId].Frets;
        var s1 = Array.FindIndex(frets, f => f >= 0);
        var s2 = Array.FindLastIndex(frets, f => f >= 0);
        if (s1 < 0 || s2 < 0) return false;
        // Root on D string or lower.
        return s1 <= 2 && s1 + 1 == s2 && frets[s1] + 2 == frets[s2];
    }

    private static bool IsDropTuning(short[] tuning) => tuning.Length >= 3 && tuning[0] == tuning[1] - 2 && tuning[0] == tuning[2] - 2;

    private static bool IsDropDPower(RocksmithSng sng, RsNote note)
    {
        if (!HasFlag(note, RsNoteMask.Chord) || !IsDropTuning(sng.MetaData.Tuning)) return false;
        var frets = sng.Chords[note.ChordId].Frets;
        return frets[0] != -1 && frets[0] == frets[1] && frets[0] == frets[2];
    }

    private static bool IsChord(RsNote note) => (note.Mask & (RsNoteMask.Chord | RsNoteMask.Sustain | RsNoteMask.DoubleStop)) == RsNoteMask.Chord;

    private static bool IsBarre(RocksmithSng sng, RsNote note)
    {
        if ((note.Mask & (RsNoteMask.Chord | RsNoteMask.DoubleStop)) != RsNoteMask.Chord) return false;
        return sng.Chords[note.ChordId].Fingers
            .Where(f => f != -1)
            .GroupBy(f => f)
            .Any(group => group.Count() >= 3);
    }

    private static bool IsObliqueBend(RocksmithSng sng, RsNote note)
    {
        return HasFlag(note, RsNoteMask.ChordNotes) &&
               sng.ChordNotes[note.ChordNotesId].BendData.Count(b => b.Count > 0) == 1;
    }

    private static bool IsCompoundBend(RsNote note)
    {
        return HasFlag(note, RsNoteMask.Bend)
               && note.BendData.Count >= 3
               && Math.Abs(note.BendData[0].Step - note.BendData[1].Step) > float.Epsilon
               && Math.Abs(note.BendData[1].Step - note.BendData[2].Step) > float.Epsilon;
    }

    private static bool IsPreBend(RsNote note)
    {
        if (!HasFlag(note, RsNoteMask.Bend) || note.BendData.Count == 0) return false;
        var firstBend = note.BendData[0];
        return firstBend.Time == note.Time && firstBend.Step > 0f;
    }

    private static bool IsChordHammerOn(RocksmithSng sng, RsNote note)
    {
        return HasFlag(note, RsNoteMask.ChordNotes) &&
               sng.ChordNotes[note.ChordNotesId].Mask.Any(m => (m & RsNoteMask.HammerOn) != 0);
    }

    private static bool IsDoubleStopAdjacentStrings(RocksmithSng sng, RsNote note)
    {
        if (!HasFlag(note, RsNoteMask.DoubleStop)) return false;
        var frets = sng.Chords[note.ChordId].Frets;
        var s1 = Array.FindIndex(frets, f => f >= 0);
        var s2 = Array.FindLastIndex(frets, f => f >= 0);
        return s1 + 1 == s2;
    }

    private static bool IsDoubleStopNonAdjacentStrings(RocksmithSng sng, RsNote note) => HasFlag(note, RsNoteMask.DoubleStop) && !IsDoubleStopAdjacentStrings(sng, note);

    private static bool IsChordSlide(RocksmithSng sng, RsNote note)
    {
        return (note.Mask & ChordNotesDoubleStop) == RsNoteMask.ChordNotes &&
               sng.ChordNotes[note.ChordNotesId].SlideTo.Any(s => s != -1);
    }

    private static bool IsChordTremolo(RocksmithSng sng, RsNote note)
    {
        return (note.Mask & ChordNotesDoubleStop) == RsNoteMask.ChordNotes &&
               sng.ChordNotes[note.ChordNotesId].Mask.Any(m => (m & RsNoteMask.Tremolo) != 0);
    }

    private static bool IsDoubleStopSlide(RocksmithSng sng, RsNote note)
    {
        return HasFlag(note, ChordNotesDoubleStop) &&
               sng.ChordNotes[note.ChordNotesId].SlideTo.Any(s => s != -1);
    }

    private static bool IsDoubleStopTremolo(RocksmithSng sng, RsNote note)
    {
        return HasFlag(note, ChordNotesDoubleStop) &&
               sng.ChordNotes[note.ChordNotesId].Mask.Any(m => (m & RsNoteMask.Tremolo) != 0);
    }

    private static bool IsDoubleStopBend(RocksmithSng sng, RsNote note)
    {
        return HasFlag(note, ChordNotesDoubleStop) &&
               sng.ChordNotes[note.ChordNotesId].BendData.Any(b => b.Count > 0);
    }

    public static IEnumerable<int> GetTechniques(RocksmithSng sng, RsNote note)
    {
        if (note.Mask is 0 or
            RsNoteMask.Single or
            (RsNoteMask.Single | RsNoteMask.Open))
        {
            yield break;
        }

        var isHopo = (note.Mask & (RsNoteMask.HammerOn | RsNoteMask.PullOff)) != 0;
        var isPower = IsPowerChord(sng, note);

        if (HasFlag(note, RsNoteMask.Accent)) yield return 0;
        if (HasFlag(note, RsNoteMask.Bend)) yield return 1;
        if (HasFlag(note, RsNoteMask.FretHandMute)) yield return 2;
        if (HasFlag(note, RsNoteMask.HammerOn)) yield return 3;
        if (HasFlag(note, RsNoteMask.Harmonic)) yield return 4;
        if (HasFlag(note, RsNoteMask.PinchHarmonic)) yield return 5;
        if (isHopo) yield return 6;
        if (HasFlag(note, RsNoteMask.PalmMute)) yield return 7;
        if (HasFlag(note, RsNoteMask.Pluck)) yield return 8;
        if (HasFlag(note, RsNoteMask.PullOff)) yield return 9;
        if (HasFlag(note, RsNoteMask.Slap)) yield return 10;
        if (HasFlag(note, RsNoteMask.Slide)) yield return 11;
        if (HasFlag(note, RsNoteMask.UnpitchedSlide)) yield return 12;
        if (HasFlag(note, RsNoteMask.Single) && HasFlag(note, RsNoteMask.Sustain)) yield return 13;
        if (HasFlag(note, RsNoteMask.Tap)) yield return 14;
        if (HasFlag(note, RsNoteMask.Tremolo)) yield return 15;
        if (HasFlag(note, RsNoteMask.Vibrato)) yield return 16;
        if (HasFlag(note, RsNoteMask.PalmMute | RsNoteMask.Accent)) yield return 17;
        if (HasFlag(note, RsNoteMask.PalmMute | RsNoteMask.Harmonic)) yield return 18;
        if (HasFlag(note, RsNoteMask.PalmMute | RsNoteMask.HammerOn)) yield return 19;
        if (HasFlag(note, RsNoteMask.PalmMute | RsNoteMask.PullOff)) yield return 20;
        if (HasFlag(note, RsNoteMask.FretHandMute | RsNoteMask.Accent)) yield return 21;
        if (HasFlag(note, RsNoteMask.Bend | RsNoteMask.Tremolo)) yield return 26;
        if (HasFlag(note, RsNoteMask.Tremolo | RsNoteMask.Slide)) yield return 27;
        if (HasFlag(note, RsNoteMask.Tremolo | RsNoteMask.Vibrato)) yield return 28;
        if (IsPreBend(note)) yield return 29;
        if (IsObliqueBend(sng, note)) yield return 30;
        if (IsCompoundBend(note)) yield return 31;
        if (!isPower && IsDoubleStopAdjacentStrings(sng, note)) yield return 33;
        if (!isPower && IsDoubleStopNonAdjacentStrings(sng, note)) yield return 34;
        if (isPower) yield return 35;
        if (IsDropDPower(sng, note)) yield return 36;
        if (IsBarre(sng, note)) yield return 37;
        if (IsChord(note)) yield return 38;
        if (isHopo && note.FingerPrintId0 != -1) yield return 40;
        if (IsChordSlide(sng, note)) yield return 41;
        if (IsChordTremolo(sng, note)) yield return 42;
        if (IsChordHammerOn(sng, note)) yield return 43;
        if (IsDoubleStopSlide(sng, note)) yield return 44;
        if (IsDoubleStopTremolo(sng, note)) yield return 45;
        if (IsDoubleStopBend(sng, note)) yield return 46;
    }
}
