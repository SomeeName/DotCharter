namespace DotCharter.Services.Editing.Validation.Rules;

/// <summary>
///     E07 — a bend authored on an open string.
///     <para>
///         Ported from Editor on Fire, which blocks the save with "One or more
///         open notes are defined as bends" and seeks to the offender.
///     </para>
///     <para>
///         <b>Why it is invalid rather than merely odd.</b> A Rocksmith bend is
///         a pitch offset from a FRETTED note — the game animates the string
///         being pushed across the fretboard from the stopped position and
///         scores the target pitch against it. An open string has no stopped
///         position to bend from, so the note has no defined behaviour: it
///         neither renders as a bend nor scores as one. (A player can of course
///         bend an open string behind the nut; Rocksmith has no representation
///         for it.)
///     </para>
///     <para>
///         Fret <c>0</c> alone is not the test. A capo makes fret 0 sound at the
///         capo fret, and a string stopped by a capo genuinely cannot be bent
///         either — the capo holds it. So the rule is "at or below the capo",
///         which is the same open-string definition the rest of the app uses
///         (<c>DotCharter.Core/AGENTS.md</c>: a sound's sounding offset is
///         <c>max(fret, capo)</c>) and a strict no-op at capo 0, which is nearly
///         every chart.
///     </para>
///     <para>
///         <b>Measured before it shipped:</b> zero findings over the 74,871
///         sounds in the 89 hand-finished RS2 exports in <c>TestData/EOF</c>.
///         It never fires on a correct chart — which is what makes it worth
///         showing at all. Harness:
///         <c>LocalOnly_PhraseAnchorFalsePositiveProbe</c>.
///     </para>
/// </summary>
public sealed class OpenNoteBendRule : IArrangementRule
{
    public void Validate(ArrangementValidationContext context, ICollection<ArrangementIssue> issues)
    {
        var arr = context.Arrangement;
        var beats = context.Beats;
        var capo = Math.Max(0, arr.Capo);

        foreach (var level in arr.Levels)
        {
            foreach (var note in level.Notes)
            {
                if (note.Fret > capo || !HasBend(note.BendValues)) continue;

                issues.Add(new ArrangementIssue(
                    ArrangementIssueType.OpenNoteWithBend,
                    Message(note.StringIndex, capo),
                    note.Position.GetPositionMs(beats),
                    Note: note, StringIndex: note.StringIndex));
            }

            foreach (var chord in level.Chords)
            {
                foreach (var chordNote in chord.ChordNotes)
                {
                    if (chordNote.Fret > capo || !HasBend(chordNote.BendValues)) continue;

                    issues.Add(new ArrangementIssue(
                        ArrangementIssueType.OpenNoteWithBend,
                        Message(chordNote.StringIndex, capo),
                        chord.Position.GetPositionMs(beats),
                        Chord: chord, StringIndex: chordNote.StringIndex));

                    // One finding per SOUND — a strum whose open strings all
                    // carry a stray bend is one mistake, not six.
                    break;
                }
            }
        }
    }

    /// <summary>
    ///     A bend point of zero steps is not a bend — importers routinely emit a
    ///     leading 0.0 to anchor the curve's start, and flagging those would fire
    ///     on ordinary charts.
    /// </summary>
    private static bool HasBend(IEnumerable<BendValue> bends) => bends.Any(b => Math.Abs(b.Step) > 0.0001);

    private static string Message(int stringIndex, int capo) =>
        capo > 0
            ? $"String {stringIndex + 1} is played open (stopped by the capo at fret {capo}) but carries a " +
              "bend. A capoed string cannot be bent, and Rocksmith has no way to render or score it."
            : $"String {stringIndex + 1} is played open but carries a bend. A Rocksmith bend is an offset " +
              "from a fretted note, so an open string has nothing to bend from.";
}
