using System.Buffers;
using GuitarNote = DotCharter.Core.Domain.Guitar.GuitarNote;

namespace DotCharter.Services.Editing.HandShapes;

/// <summary>
///     The level's sounds in time order, for <see cref="HandShapeSuggester" />: notes sharing a
///     position are ONE sound (a chord spelled as notes), a chord with no chord notes (Rocksmith's
///     repeat box) plays its template, and a fret-hand-muted note contributes no fret.
///     <para>
///         <c>level.Notes</c> and <c>level.Chords</c> are position-sorted, so the sounds come from ONE
///         merge of the two lists into pooled arrays of structs — no keyed collection, no dictionary
///         per sound. A list that is NOT sorted (an import mid-repair) is stably sorted first, so the
///         answer is the one a position-keyed map gave: at a shared position the notes come first, then
///         the chords, each in list order, and a string struck twice keeps its first slot with its
///         last fret.
///     </para>
/// </summary>
internal sealed class HandShapeSounds : IDisposable
{
    private SoundRow[] _rows;
    private FretEntry[] _entries;
    private int _entryCount;

    private HandShapeSounds(int rows, int entries)
    {
        _rows = ArrayPool<SoundRow>.Shared.Rent(Math.Max(rows, 1));
        _entries = ArrayPool<FretEntry>.Shared.Rent(Math.Max(entries, 4));
    }

    public int Count { get; private set; }

    public FractionalPosition Position(int sound) => _rows[sound].Position;

    public FractionalPosition End(int sound) => _rows[sound].End;

    public bool IsChord(int sound) => _rows[sound].IsChord;

    public bool Slides(int sound) => _rows[sound].Slides;

    /// <summary>How many strings the sound holds a fret on.</summary>
    public int FretCount(int sound) => _rows[sound].FretCount;

    public int StringAt(int sound, int slot) => _entries[_rows[sound].First + slot].StringIndex;

    public int FretAt(int sound, int slot) => _entries[_rows[sound].First + slot].Fret;

    /// <summary>The fret the sound holds on <paramref name="stringIndex" />, or -1.</summary>
    public int FretOn(int sound, int stringIndex)
    {
        ref readonly var row = ref _rows[sound];
        for (var k = row.First; k < row.First + row.FretCount; k++)
        {
            if (_entries[k].StringIndex == stringIndex) return _entries[k].Fret;
        }

        return -1;
    }

    public void Dispose()
    {
        ArrayPool<SoundRow>.Shared.Return(_rows);
        ArrayPool<FretEntry>.Shared.Return(_entries);
        _rows = [];
        _entries = [];
    }

    public static HandShapeSounds Build(ChartLevel level, Dictionary<int, ChordTemplate> templates)
    {
        var notes = Sorted(level.Notes);
        var chords = Sorted(level.Chords);
        var sounds = new HandShapeSounds(notes.Count + chords.Count, notes.Count + chords.Count * 4);
        int n = 0, c = 0;
        while (n < notes.Count || c < chords.Count)
        {
            var at = FirstPosition(notes, n, chords, c);
            sounds.Begin(at);
            for (; n < notes.Count && notes[n].Position.CompareTo(at) == 0; n++)
                sounds.AbsorbNote(notes[n]);

            for (; c < chords.Count && chords[c].Position.CompareTo(at) == 0; c++)
                sounds.AbsorbChord(chords[c], templates);
        }

        return sounds;
    }

    private static FractionalPosition FirstPosition(IList<GuitarNote> notes, int n, IList<Chord> chords, int c)
    {
        if (c >= chords.Count) return notes[n].Position;
        if (n >= notes.Count) return chords[c].Position;
        return notes[n].Position.CompareTo(chords[c].Position) <= 0 ? notes[n].Position : chords[c].Position;
    }

    private static IList<GuitarNote> Sorted(IList<GuitarNote> notes)
    {
        for (var i = 1; i < notes.Count; i++)
        {
            if (notes[i - 1].Position.CompareTo(notes[i].Position) > 0) return [.. notes.OrderBy(x => x.Position)];
        }

        return notes;
    }

    private static IList<Chord> Sorted(IList<Chord> chords)
    {
        for (var i = 1; i < chords.Count; i++)
        {
            if (chords[i - 1].Position.CompareTo(chords[i].Position) > 0) return [.. chords.OrderBy(x => x.Position)];
        }

        return chords;
    }

    private void Begin(FractionalPosition position)
    {
        if (Count == _rows.Length) Grow(ref _rows);
        _rows[Count++] = new SoundRow { Position = position, End = position, First = _entryCount };
    }

    private void AbsorbNote(GuitarNote note) =>
        Absorb(note.StringIndex, note.Fret, note.Mute, note.EndPosition, note.SlideTo.HasValue || note.UnpitchedSlide);

    private void AbsorbChord(Chord chord, Dictionary<int, ChordTemplate> templates)
    {
        _rows[Count - 1].IsChord = true;
        if (chord.ChordNotes.Count > 0)
        {
            for (var k = 0; k < chord.ChordNotes.Count; k++)
            {
                var cn = chord.ChordNotes[k];
                Absorb(cn.StringIndex, cn.Fret, cn.Mute, chord.Position + cn.Sustain,
                    cn.SlideTo.HasValue || cn.UnpitchedSlide);
            }
        }
        else if (templates.TryGetValue(chord.TemplateId, out var template))
        {
            foreach (var (stringIndex, fret) in template.Frets)
                Absorb(stringIndex, fret, MuteType.None, chord.Position, false);
        }
    }

    private void Absorb(int stringIndex, int fret, MuteType mute, FractionalPosition end, bool slides)
    {
        ref var row = ref _rows[Count - 1];
        if (end.CompareTo(row.End) > 0) row.End = end;
        row.Slides |= slides;
        if (mute == MuteType.Full || fret < 0) return;

        for (var k = row.First; k < row.First + row.FretCount; k++)
        {
            if (_entries[k].StringIndex != stringIndex) continue;
            _entries[k].Fret = fret;
            return;
        }

        if (_entryCount == _entries.Length) Grow(ref _entries);
        _entries[_entryCount++] = new FretEntry { StringIndex = stringIndex, Fret = fret };
        row.FretCount++;
    }

    private static void Grow<T>(ref T[] array)
    {
        var bigger = ArrayPool<T>.Shared.Rent(array.Length * 2);
        Array.Copy(array, bigger, array.Length);
        ArrayPool<T>.Shared.Return(array);
        array = bigger;
    }

    private struct SoundRow
    {
        public FractionalPosition Position;
        public FractionalPosition End;
        public int First;
        public int FretCount;
        public bool IsChord;
        public bool Slides;
    }

    private struct FretEntry
    {
        public int StringIndex;
        public int Fret;
    }
}
