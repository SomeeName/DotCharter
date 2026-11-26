using DotCharter.Services.Editing.Validation.Rules;
using DotCharter.Services.State;

namespace DotCharter.Services.Editing.Validation;

/// <summary>
///     The one-click repair of D70 (a harmonic with vibrato): clear the vibrato and its strength on the harmonic
///     sound, one undo step. Unambiguous, since a harmonic takes no vibrato; the harmonic itself stays.
///     A chord loses the vibrato on its harmonic notes only.
/// </summary>
public static class HarmonicVibratoFix
{
    public static IChartAction? For(ArrangementIssue issue, ChartLevel level)
    {
        if (issue.Type != ArrangementIssueType.HarmonicVibrato) return null;

        if (issue.Note is { } note && level.Notes.Contains(note) && TechniqueCombinationRule.HarmonicWithVibrato(note))
            return new MutateNotesAction(level.Notes, [note], Clear);

        if (issue.Chord is not { } chord || !level.Chords.Contains(chord)
            || !chord.ChordNotes.Any(TechniqueCombinationRule.HarmonicWithVibrato))
        {
            return null;
        }

        var cleared = chord.Clone();
        foreach (var cn in cleared.ChordNotes.Where(TechniqueCombinationRule.HarmonicWithVibrato)) Clear(cn);
        return new MutateChordsAction(level.Chords, [chord], [chord.Clone()], [cleared]);
    }

    private static void Clear(IGuitarSound sound)
    {
        sound.Vibrato = false;
        sound.VibratoStrength = 0;
    }
}
