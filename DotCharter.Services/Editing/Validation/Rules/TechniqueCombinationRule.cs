namespace DotCharter.Services.Editing.Validation.Rules;

/// <summary>
///     Technique pairings that do not belong together:
///     <list type="bullet">
///         <item>
///             D70 (<see cref="ArrangementIssueType.HarmonicVibrato" />): a harmonic with vibrato. A harmonic rings as
///             one clean tone, so the vibrato is never heard; a tab import already drops a harmonic's sustain.
///             <see cref="HarmonicVibratoFix" /> removes it.
///         </item>
///         <item>
///             D71 (<see cref="ArrangementIssueType.UnpitchedSlideOneFret" />): an unpitched slide that spans a single
///             fret. It is too short to hear as a slide. A slide that ends on its start fret is E01's finding, not
///             this one; there is no fix, since the charter may want a longer slide or none.
///             A fully muted unpitched slide is a pick scrape (the dead-note slide of a tab import): it is noise, and a
///             short one near the end of the neck is legitimate, so it is exempt.
///         </item>
///     </list>
///     A note that is both fret-hand muted and palm muted cannot be written: <see cref="MuteType" /> holds one value,
///     and every import picks one (see the import notes in the core guide), so there is nothing to check.
/// </summary>
public sealed class TechniqueCombinationRule : IArrangementRule
{
    private const string HarmonicVibratoKey = "Validation_HarmonicVibrato";
    private const string OneFretKey = "Validation_UnpitchedSlideOneFret";

    public void Validate(ArrangementValidationContext context, ICollection<ArrangementIssue> issues)
    {
        var beats = context.Beats;
        foreach (var level in context.Arrangement.Levels)
        {
            foreach (var note in level.Notes)
            {
                var timeMs = note.Position.GetPositionMs(beats);
                if (HarmonicWithVibrato(note))
                {
                    issues.Add(ArrangementIssues.Note(ArrangementIssueType.HarmonicVibrato, note, timeMs,
                        HarmonicVibratoKey));
                }

                if (OneFretUnpitched(note))
                {
                    issues.Add(ArrangementIssues.Note(ArrangementIssueType.UnpitchedSlideOneFret, note, timeMs,
                        OneFretKey, args: [note.Fret, note.SlideTo!.Value]));
                }
            }

            foreach (var chord in level.Chords)
            {
                var timeMs = chord.Position.GetPositionMs(beats);
                if (chord.ChordNotes.FirstOrDefault(HarmonicWithVibrato) is { } harmonic)
                {
                    issues.Add(ArrangementIssues.Chord(ArrangementIssueType.HarmonicVibrato, chord, timeMs,
                        HarmonicVibratoKey, harmonic.StringIndex));
                }

                foreach (var cn in chord.ChordNotes.Where(OneFretUnpitched))
                {
                    issues.Add(ArrangementIssues.Chord(ArrangementIssueType.UnpitchedSlideOneFret, chord, timeMs,
                        OneFretKey, cn.StringIndex, [cn.Fret, cn.SlideTo!.Value]));
                }
            }
        }
    }

    internal static bool HarmonicWithVibrato(IGuitarSound sound) =>
        sound.Harmonic != HarmonicType.None && sound.Vibrato;

    private static bool OneFretUnpitched(IGuitarSound sound) =>
        sound is { UnpitchedSlide: true, Mute: not MuteType.Full, SlideTo: { } to } && Math.Abs(to - sound.Fret) == 1;
}
