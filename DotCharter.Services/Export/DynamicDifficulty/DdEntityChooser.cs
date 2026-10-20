namespace DotCharter.Services.Export.DynamicDifficulty;

/// <summary>
///     Ported from Rocksmith2014.NET (src/Rocksmith2014.DD/EntityChooser.fs; MIT, Copyright
///     (c) 2020 Tapio Malmberg — see THIRD-PARTY-NOTICES.md § 2.1): the notes and chords one
///     difficulty level keeps, and how each is simplified.
///     <para>
///         The F# folds into an immutable list whose head is the entity added last; that
///         list is <c>_level</c> here, appended to, and every "search the list" walks it from
///         the end. The per-string state is <see cref="DdArrangement.StringSlots" /> wide
///         instead of six.
///     </para>
/// </summary>
internal sealed class DdEntityChooser
{
    private const DdNoteMask AlwaysEnabledTechs =
        DdNoteMask.Ignore | DdNoteMask.FretHandMute | DdNoteMask.PalmMute | DdNoteMask.Harmonic;

    private readonly double _diffPercent;
    private readonly IReadOnlyDictionary<int, DdDifficultyRange> _divisionMap;
    private readonly IReadOnlyDictionary<int, int> _noteTimeToScore;
    private readonly IReadOnlyDictionary<int, int> _notesWithScore;
    private readonly List<DdChordTemplate> _templates;
    private readonly IReadOnlyList<DdHandShape> _handShapes;
    private readonly int _allowedChordNotes;

    // Strings from which a linknext note has been removed
    private readonly bool[] _removedLinkNexts;
    private readonly DdNote?[] _pendingLinkNexts;
    private readonly Dictionary<int, int> _currentNotesWithScore = [];
    private readonly List<(DdEntity Entity, DdTemplateRequest? Request)> _level = [];

    private DdEntityChooser(double diffPercent, IReadOnlyDictionary<int, DdDifficultyRange> divisionMap,
        IReadOnlyDictionary<int, int> noteTimeToScore, IReadOnlyDictionary<int, int> notesWithScore,
        List<DdChordTemplate> templates, IReadOnlyList<DdHandShape> handShapes, int maxChordNotes, int stringSlots)
    {
        _diffPercent = diffPercent;
        _divisionMap = divisionMap;
        _noteTimeToScore = noteTimeToScore;
        _notesWithScore = notesWithScore;
        _templates = templates;
        _handShapes = handShapes;
        _allowedChordNotes = DdTemplates.AllowedChordNotes(diffPercent, maxChordNotes);
        _removedLinkNexts = new bool[stringSlots];
        _pendingLinkNexts = new DdNote?[stringSlots];
    }

    public static List<(DdEntity Entity, DdTemplateRequest? Request)> Choose(double diffPercent,
        IReadOnlyDictionary<int, DdDifficultyRange> divisionMap, IReadOnlyDictionary<int, int> noteTimeToScore,
        IReadOnlyDictionary<int, int> notesWithScore, List<DdChordTemplate> templates,
        IReadOnlyList<DdHandShape> handShapes, int maxChordNotes, int stringSlots, DdEntity[] entities)
    {
        var chooser = new DdEntityChooser(diffPercent, divisionMap, noteTimeToScore, notesWithScore, templates,
            handShapes, maxChordNotes, stringSlots);
        foreach (var e in entities) chooser.Visit(e, entities);
        return chooser._level;
    }

    private void Visit(DdEntity e, DdEntity[] entities)
    {
        var score = _noteTimeToScore[e.Time];
        var range = _divisionMap[score];

        // Always include notes without techniques that are linked into
        var includeAlways = e.Note is { } n
                            && (n.Mask & ~(DdNoteMask.Ignore | DdNoteMask.LinkNext)) == DdNoteMask.None
                            && _pendingLinkNexts[n.String] != null
                            && !(n.IsSlide || n.IsUnpitchedSlide || n.IsBend || n.IsVibrato);

        if (!includeAlways && ShouldExclude(score, range))
        {
            Exclude(e);
            return;
        }

        if (e.Note != null) IncludeNote(e.Note, entities);
        else IncludeChord(e.Chord!);
    }

    private void Exclude(DdEntity e)
    {
        RemovePreviousLinkNext(e);

        // Update removedLinkNexts (when not a slide)
        if (e.Note is { } xn)
        {
            _removedLinkNexts[xn.String] = xn.IsLinkNext && !xn.IsSlide;
        }
        else if (e.Chord is { IsLinkNext: true, ChordNotes: { } chordNotes })
        {
            foreach (var cn in chordNotes)
            {
                if (cn.IsLinkNext && !cn.IsSlide) _removedLinkNexts[cn.String] = true;
            }
        }
    }

    private void IncludeNote(DdNote oNote, DdEntity[] entities)
    {
        if (_removedLinkNexts[oNote.String])
        {
            // Previous note on the same string had linknext and was removed, remove this note also
            // Reset removed linknext bit for this string unless this note also has linknext
            if (!oNote.IsLinkNext) _removedLinkNexts[oNote.String] = false;
            return;
        }

        IncrementCount(_noteTimeToScore[oNote.Time]);
        var note = new DdNote(oNote);
        PruneTechniques(note);

        _pendingLinkNexts[note.String] = null;
        if (note.IsLinkNext) _pendingLinkNexts[note.String] = note;

        if (note.IsHopo && !KeepsHopo(note, entities))
        {
            note.IsHammerOn = false;
            note.IsPullOff = false;
        }

        _level.Add((DdEntity.Of(note), null));
    }

    private bool KeepsHopo(DdNote note, DdEntity[] entities)
    {
        var prevLevelEntity = FindEntityWithString(note.String);
        var prevAllEntity = FindPrevEntityAll(entities, note.String, note.Time);

        // Likely a "hammer-on from nowhere"
        if (prevAllEntity == null) return true;
        if (prevLevelEntity is not { } prev) return false;
        if (prev.Note is { } n)
        {
            // Leave the HOPO if this is a tapping phrase
            if (n.IsTap) return true;
            // Leave the HOPO if the previous note is the actual one before this
            if (prevAllEntity.Value.Note is { } nn && n.Time == nn.Time) return true;
            // Leave the HOPO if the previous note on the same string is an appropriate one and comes right before this one
            return ReferenceEquals(_level[^1].Entity.Note, n)
                   && !(n.IsFretHandMute || n.IsHarmonic)
                   && ((note.IsPullOff && n.Fret > note.Fret) || (note.IsHammerOn && n.Fret < note.Fret));
        }

        // Leave the HOPO if the previous chord is the actual one before this
        return prevAllEntity.Value.Chord is { } cc && prev.Chord!.Time == cc.Time;
    }

    private void IncludeChord(DdChord chord)
    {
        IncrementCount(_noteTimeToScore[chord.Time]);

        var template = _templates[chord.ChordId];
        var noteCount = DdTemplates.NoteCount(template);

        if (_allowedChordNotes <= 1)
        {
            // Convert the chord into a note
            var note = NoteFromChord(noteCount, template, chord);
            if (note.IsLinkNext) _pendingLinkNexts[note.String] = note;
            _level.Add((DdEntity.Of(note), null));
            return;
        }

        var copy = new DdChord(chord);

        // Create chord notes if this is the first chord in the handshape
        if (copy.ChordNotes == null && IsFirstChordInHs(copy)) copy.ChordNotes = ChordNotesFromTemplate(template, copy);

        if (_allowedChordNotes >= noteCount)
        {
            if (copy.HasChordNotes)
            {
                foreach (var cn in copy.ChordNotes!)
                {
                    PruneTechniques(cn);
                    if (cn.IsLinkNext) _pendingLinkNexts[cn.String] = cn;
                }
            }

            _level.Add((DdEntity.Of(copy), null));
            return;
        }

        if (copy.HasChordNotes) PruneChordNotes(copy);
        _level.Add((DdEntity.Of(copy), DdTemplates.Request(chord.ChordId, _allowedChordNotes, noteCount, template, copy, null)));
    }

    private void PruneTechniques(DdNote note)
    {
        if (_diffPercent <= 0.2)
        {
            if (!note.IsBend) note.Sustain = 0;
            if (note.IsLinkNext)
            {
                _removedLinkNexts[note.String] = true;
                note.IsLinkNext = false;
            }

            note.Mask &= AlwaysEnabledTechs;
            note.Vibrato = 0;
            note.SlideTo = -1;
            note.SlideUnpitchTo = -1;
        }
        else if (_diffPercent <= 0.35 && note.IsTremolo && !note.IsTap)
        {
            note.IsTremolo = false;
        }
        else if (_diffPercent <= 0.45 && note.IsVibrato && !note.IsTap)
        {
            note.Vibrato = 0;
        }

        if (_diffPercent <= 0.60 && note.IsPinchHarmonic) note.IsPinchHarmonic = false;
    }

    /// <summary>Keeps the FIRST <see cref="_allowedChordNotes" /> chord notes, in chord-note order.</summary>
    private void PruneChordNotes(DdChord chord)
    {
        var cn = chord.ChordNotes!;
        var notesToRemove = cn.Count - _allowedChordNotes;
        if (notesToRemove < 0)
            throw new InvalidOperationException($"Chord at time {chord.Time / 1000.0} has less chord notes than its chord template.");

        for (var i = cn.Count - notesToRemove; i < cn.Count; i++)
        {
            if (cn[i].IsLinkNext) _removedLinkNexts[cn[i].String] = true;
        }

        cn.RemoveRange(cn.Count - notesToRemove, notesToRemove);
        foreach (var n in cn)
        {
            PruneTechniques(n);
            if (n.IsLinkNext) _pendingLinkNexts[n.String] = n;
        }
    }

    private bool ShouldExclude(int score, DdDifficultyRange range)
    {
        // The entity is outside of the difficulty range -> Exclude
        if (_diffPercent < range.Low) return true;
        // The current difficulty is greater than the upper limit of the range -> Include
        if (_diffPercent >= range.High) return false;

        // The entity is within the difficulty range -> Check the number of allowed notes
        var notes = _notesWithScore[score];
        var currentCount = _currentNotesWithScore.GetValueOrDefault(score);
        var allowedPercent = (_diffPercent - range.Low) / (range.High - range.Low);
        var allowedNotes = Math.Round(notes * allowedPercent, MidpointRounding.AwayFromZero);
        var minNotes = range.Low == 0.0 ? 1 : 0;
        var allowedCount = Math.Max(minNotes, (int)allowedNotes);
        return currentCount + 1 > allowedCount;
    }

    private void IncrementCount(int score) =>
        _currentNotesWithScore[score] = _currentNotesWithScore.GetValueOrDefault(score) + 1;

    private void RemovePreviousLinkNext(DdEntity e)
    {
        if (e.Note is not { } note) return;
        var lnNote = _pendingLinkNexts[note.String];
        _pendingLinkNexts[note.String] = null;
        if (lnNote == null) return;
        if (lnNote.IsSlide)
        {
            lnNote.SlideTo = -1;
            lnNote.Sustain = 0;
        }

        lnNote.IsLinkNext = false;
    }

    /// <summary>The entity added last to this level that sounds on <paramref name="str" />.</summary>
    private DdEntity? FindEntityWithString(int str)
    {
        for (var i = _level.Count - 1; i >= 0; i--)
        {
            var e = _level[i].Entity;
            if (e.Note is { } n ? n.String == str : e.Chord!.HasChordNotes && e.Chord.ChordNotes!.Exists(cn => cn.String == str))
                return e;
        }

        return null;
    }

    private static DdEntity? FindPrevEntityAll(DdEntity[] allEntities, int str, int time)
    {
        for (var i = allEntities.Length - 1; i >= 0; i--)
        {
            var e = allEntities[i];
            var hit = e.Note is { } n
                ? n.String == str && n.Time < time
                : e.Chord!.Time < time && e.Chord.HasChordNotes && e.Chord.ChordNotes!.Exists(cn => cn.String == str);
            if (hit) return e;
        }

        return null;
    }

    private bool IsFirstChordInHs(DdChord chord)
    {
        var hs = _handShapes.FirstOrDefault(x => chord.Time >= x.StartTime && chord.Time < x.EndTime);
        // The hand shape might not be found if the start of the phrase is placed poorly
        if (hs == null) return true;
        return !_level.Any(x => x.Entity.Chord is { HasChordNotes: true } c
                                && c.ChordId == chord.ChordId
                                && c.Time >= hs.StartTime && c.Time < hs.EndTime);
    }

    /// <summary>Creates a list of chord notes from a chord template.</summary>
    private static List<DdNote> ChordNotesFromTemplate(DdChordTemplate template, DdChord chord)
    {
        var cn = new List<DdNote>();
        for (var i = 0; i < template.Frets.Length; i++)
        {
            if (template.Frets[i] == -1) continue;
            cn.Add(new DdNote
            {
                Time = chord.Time,
                String = i,
                Fret = template.Frets[i],
                LeftHand = template.Fingers[i],
                Mask = ChordFlags(chord, includeIgnore: false),
            });
        }

        return cn;
    }

    /// <summary>Creates a note from a chord.</summary>
    private DdNote NoteFromChord(int chordTotalNotes, DdChordTemplate template, DdChord chord)
    {
        var fromHighest = DdTemplates.StartFromHighestNote(chordTotalNotes, template);
        if (chord.HasChordNotes)
        {
            var cns = chord.ChordNotes!;
            var noteIndex = fromHighest ? cns.Count - 1 : 0;
            // Create the note from a chord note
            for (var i = 0; i < cns.Count; i++)
            {
                if (i != noteIndex && cns[i].IsLinkNext) _removedLinkNexts[cns[i].String] = true;
            }

            var note = new DdNote(cns[noteIndex]) { LeftHand = -1 };
            PruneTechniques(note);
            return note;
        }

        // Create the note from the chord template
        var str = fromHighest
            ? Array.FindLastIndex(template.Frets, f => f != -1)
            : Array.FindIndex(template.Frets, f => f != -1);
        if (str < 0) throw new InvalidOperationException($"Chord template {chord.ChordId} has no notes.");
        return new DdNote
        {
            Time = chord.Time,
            String = str,
            Fret = template.Frets[str],
            Mask = ChordFlags(chord, includeIgnore: true),
        };
    }

    private static DdNoteMask ChordFlags(DdChord chord, bool includeIgnore)
    {
        var mask = DdNoteMask.None;
        if (chord.IsFretHandMute) mask |= DdNoteMask.FretHandMute;
        if (chord.IsPalmMute) mask |= DdNoteMask.PalmMute;
        if (chord.IsAccent) mask |= DdNoteMask.Accent;
        if (includeIgnore && chord.IsIgnore) mask |= DdNoteMask.Ignore;
        return mask;
    }
}
