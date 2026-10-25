using System.Collections.ObjectModel;

namespace DotCharter.Services.Editing.ChordShapes;

/// <summary>Moves a chord's notes onto a shape's frets.</summary>
public static class ChordNoteProjection
{
    /// <summary>
    ///     The notes <paramref name="chord" /> has once it plays <paramref name="template" />: one per string of
    ///     the template, keeping an existing note's per-string attributes (mute, hammer-on, bends, ...) where the
    ///     string already sounded and adding a bare note where it did not. The chord passed in must already be a
    ///     clone — its notes are mutated in place.
    /// </summary>
    public static ObservableCollection<ChordNote> OnTo(Chord chord, ChordTemplate template)
    {
        ArgumentNullException.ThrowIfNull(chord);
        ArgumentNullException.ThrowIfNull(template);
        var notes = new List<ChordNote>();
        foreach (var (stringIndex, fret) in template.Frets)
        {
            var existing = chord.ChordNotes.FirstOrDefault(n => n.StringIndex == stringIndex);
            if (existing != null)
            {
                existing.Fret = fret;
                notes.Add(existing);
            }
            else
            {
                notes.Add(new ChordNote { StringIndex = stringIndex, Fret = fret });
            }
        }

        return new ObservableCollection<ChordNote>(notes);
    }
}
