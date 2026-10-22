using System.Collections.ObjectModel;
using Chord = DotCharter.Core.Domain.Guitar.Chord;
using GuitarNote = DotCharter.Core.Domain.Guitar.GuitarNote;

namespace DotCharter.Services.Editing.Highway.HighwayCommands;

/// <summary>
///     Everything a keyboard mutation verb needs to act on the SELECTION —
///     notes and chords alike.
///     <para>
///         A note-only <see cref="SelectedNoteMutationCommands" />
///         (<c>ObservableCollection&lt;GuitarNote&gt;</c>) would make every technique /
///         fret / string keybind a silent no-op on a chord — the thing a
///         Rocksmith charter spends most of the day on — and leave the inspector
///         the only route.
///     </para>
///     <para>
///         <see cref="Arrangement" /> is required for one reason and it is not
///         optional: a <c>ChordTemplate</c> is SHARED, so any verb that changes
///         a chord note's FRET or STRING must resolve-or-create a template with
///         the new shape and repoint <c>TemplateId</c>. In the <c>.dcproj</c> a
///         <c>&lt;chordNote&gt;</c> carries no <c>fret</c> attribute at all — the
///         reader takes it from the template — so a fret change that does not
///         repoint is DISCARDED on the next save (format audit 02 §6.5).
///         A null arrangement (or an untemplated chord, <c>TemplateId &lt; 0</c>)
///         leaves the id alone: there is nothing for it to disagree with.
///     </para>
/// </summary>
internal readonly record struct SelectedSoundMutationTargets(
    ObservableCollection<GuitarNote> Notes,
    ObservableCollection<Chord> Chords,
    IReadOnlyList<GuitarNote> SelectedNotes,
    IReadOnlyList<Chord> SelectedChords,
    Arrangement? Arrangement,
    StringEditMask Mask,
    Action? OnPostExecute)
{
    /// <summary>
    ///     The chords a TECHNIQUE verb writes in place of the selected ones when the "Apply to"
    /// ladder reaches past the selection, with the levels they sit on; null for the
    ///     selection. Only the technique verbs set it, so a ladder write never changes a shape and
    ///     never mints a template.
    /// </summary>
    public ChordLadderReach? Ladder { get; init; }

    /// <summary>The chords the chord half of a verb writes: the ladder's, else the selection's,
    /// through the mask either way.</summary>
    public List<Chord> TargetChords() => Ladder is { } ladder ? MaskedChords(ladder.Chords) : MaskedChords();

    /// <summary>Selected standalone notes the mask lets through. A note lives on
    /// exactly one string, so the mask decides whether it is edited at all —
    /// EOF's per-lane bitmask semantics (<c>song.c:8467-8555</c>).</summary>
    public List<GuitarNote> MaskedNotes()
    {
        var result = new List<GuitarNote>();
        foreach (var note in SelectedNotes)
        {
            if (Mask.Includes(note.StringIndex))
                result.Add(note);
        }

        return result;
    }

    /// <summary>Selected chords the mask lets through: a chord is a target when
    /// it plays at least one masked string (every chord while the mask is
    /// empty). WHICH of its strings a per-string verb then touches is a second
    /// question, asked per <c>ChordNote</c>.</summary>
    public List<Chord> MaskedChords() => MaskedChords(SelectedChords);

    private List<Chord> MaskedChords(IReadOnlyList<Chord> chords)
    {
        var result = new List<Chord>();
        foreach (var chord in chords)
        {
            if (Mask.IsEmpty)
            {
                result.Add(chord);
                continue;
            }

            foreach (var chordNote in chord.ChordNotes)
            {
                if (Mask.Includes(chordNote.StringIndex))
                {
                    result.Add(chord);
                    break;
                }
            }
        }

        return result;
    }

    /// <summary>
    ///     The sound a "group" verb reads its next value from: the first masked
    ///     selected note, else the first masked <c>ChordNote</c> of the first
    ///     targeted chord. Notes come first because that is the order the
    ///     pre-0.35 <c>CreateGroup</c> used, so a note-only selection behaves
    ///     byte-identically.
    /// </summary>
    public IGuitarSound? FirstMaskedSound()
    {
        foreach (var note in SelectedNotes)
        {
            if (Mask.Includes(note.StringIndex))
                return note;
        }

        foreach (var chord in SelectedChords)
        {
            foreach (var chordNote in chord.ChordNotes)
            {
                if (Mask.Includes(chordNote.StringIndex))
                    return chordNote;
            }
        }

        return null;
    }
}
