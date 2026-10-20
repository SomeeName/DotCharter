using System.Globalization;
using System.Text.RegularExpressions;

namespace DotCharter.Services.Export.DynamicDifficulty;

/// <summary>
///     Ported from Rocksmith2014.NET (src/Rocksmith2014.XML.Processing: ArrangementImprover.fs,
///     Utils.fs and Improvers/*.fs; MIT, Copyright (c) 2020 Tapio Malmberg — see
///     THIRD-PARTY-NOTICES.md § 2.1): the fixes DLC Builder applies to every arrangement it
///     builds (<c>ArrangementImprover.applyAll</c>, its default since <c>ApplyImprovements</c>
///     is on), in its order, step for step.
///     <para>
///         <b>One step is not run: <c>removeExtraBeats</c>, and the <c>removebeats</c> custom
///         event's beat removal.</b> Both move or delete BEATS, and in DotCharter the beat map
///         belongs to the project, shared by every arrangement of the package and by the audio
///         it was mapped to; the package writes it from the project (export-companions
///         <c>songLength</c> rule: the chart's length and the audio's are resolved at the
///         boundary, not by moving the grid). The <c>removebeats</c> event itself is still
///         consumed, so it does not ship as an event.
///     </para>
/// </summary>
internal static partial class DdImprover
{
    /// <summary><c>ArrangementImprover.applyAll</c> without <c>removeExtraBeats</c>.</summary>
    public static void ApplyAll(DdArrangement arr)
    {
        ValidatePhraseNames(arr);
        AddIgnores(arr);
        FixLinkNexts(arr);
        RemoveOverlappingBendValues(arr);
        RemoveMutedNotesFromChords(arr);
        // Do before phrase start anchors are fixed
        MoveAnchors(arr);
        MovePhrases(arr);
        EofFixes(arr);
        RemoveUnnecessaryNotes(arr);
        // Should be done after fixing the anchors at phrase start
        RemoveRedundantAnchors(arr);
        AddCrowdEvents(arr);
        ProcessChordNames(arr);
        ProcessCustomEvents(arr);
        LengthenHandShapes(arr);
        ShortenHandShapes(arr);
    }

    // ── BasicFixes ──────────────────────────────────────────────────────

    [GeneratedRegex("[^a-zA-Z0-9 _#]")]
    private static partial Regex IllegalPhraseCharacters();

    /// <summary>Only characters used in official files; a double quote can corrupt the save file.</summary>
    private static void ValidatePhraseNames(DdArrangement arr)
    {
        foreach (var phrase in arr.Phrases) phrase.Name = IllegalPhraseCharacters().Replace(phrase.Name, "");
    }

    /// <summary>Ignore on 23rd/24th fret notes and chords, and on sustained 7th fret harmonics.</summary>
    private static void AddIgnores(DdArrangement arr)
    {
        static bool IgnoreHarmonic(DdNote n) => n.Fret == 7 && n.Sustain > 0 && n.IsHarmonic;

        foreach (var level in arr.Levels)
        {
            foreach (var n in level.Notes)
            {
                if (n.Fret >= 23 || IgnoreHarmonic(n)) n.IsIgnore = true;
            }

            foreach (var c in level.Chords)
            {
                if (c.HasChordNotes && c.ChordNotes!.Exists(IgnoreHarmonic)) c.IsIgnore = true;
                if (Template(arr, c.ChordId) is { } template && template.Frets.Any(f => f >= 23)) c.IsIgnore = true;
            }
        }
    }

    private static void FixLinkNexts(DdArrangement arr)
    {
        foreach (var level in arr.Levels)
        {
            var notes = level.Notes;
            for (var i = 0; i < notes.Count; i++)
            {
                var note = notes[i];
                if (!note.IsLinkNext) continue;
                var nextNote = NextNoteOnSameString(notes, i, note);
                if (nextNote == null || nextNote.Time - (note.Time + note.Sustain) > 50)
                {
                    note.IsLinkNext = false;
                    continue;
                }

                nextNote.Fret = note.SlideTo > 0 ? note.SlideTo : note.SlideUnpitchTo > 0 ? note.SlideUnpitchTo : note.Fret;
                if (!note.IsBend) continue;
                var lastStep = note.BendValues![^1].Step;
                if (lastStep <= 0f) continue;
                if (!nextNote.IsBend)
                {
                    nextNote.MaxBend = lastStep;
                    nextNote.BendValues = [new DdBendValue(nextNote.Time, lastStep)];
                }
                else if (nextNote.BendValues![0].Time != nextNote.Time)
                {
                    if (nextNote.MaxBend < lastStep) nextNote.MaxBend = lastStep;
                    nextNote.BendValues.Insert(0, new DdBendValue(nextNote.Time, lastStep));
                }
            }
        }
    }

    private static void RemoveOverlappingBendValues(DdArrangement arr)
    {
        static void Filter(DdNote note)
        {
            if (note.IsBend) note.BendValues = [.. note.BendValues!.DistinctBy(b => b.Time)];
        }

        foreach (var level in arr.Levels)
        {
            level.Notes.ForEach(Filter);
            foreach (var c in level.Chords.Where(c => c.HasChordNotes)) c.ChordNotes!.ForEach(Filter);
        }
    }

    /// <summary>Removes fret-hand-muted notes from chords that also contain normal notes.</summary>
    private static void RemoveMutedNotesFromChords(DdArrangement arr)
    {
        var fixedTemplates = new HashSet<int>();
        foreach (var chord in arr.Levels.SelectMany(l => l.Chords))
        {
            if (fixedTemplates.Contains(chord.ChordId) || !chord.HasChordNotes || chord.IsFretHandMute) continue;
            var muted = chord.ChordNotes!.FindAll(n => n.IsFretHandMute);
            // Remove the mutes unless all notes are muted
            if (muted.Count == 0 || muted.Count == chord.ChordNotes.Count) continue;
            chord.ChordNotes.RemoveAll(n => n.IsFretHandMute);
            if (Template(arr, chord.ChordId) is { } template)
            {
                foreach (var n in muted)
                {
                    template.Frets[n.String] = -1;
                    template.Fingers[n.String] = -1;
                }
            }

            fixedTemplates.Add(chord.ChordId);
        }
    }

    /// <summary>Removes anchors identical (fret and width) to the previous one, except at a phrase start.</summary>
    private static void RemoveRedundantAnchors(DdArrangement arr)
    {
        var phraseTimes = arr.PhraseIterations.Select(pi => pi.Time).ToHashSet();
        foreach (var level in arr.Levels)
        {
            var anchors = level.Anchors;
            if (anchors.Count == 0) continue;
            var result = new List<DdAnchor>(anchors.Count) { anchors[0] };
            for (var i = 1; i < anchors.Count; i++)
            {
                var identical = anchors[i - 1].Fret == anchors[i].Fret && anchors[i - 1].Width == anchors[i].Width;
                if (!identical || phraseTimes.Contains(anchors[i].Time)) result.Add(anchors[i]);
            }

            anchors.Clear();
            anchors.AddRange(result);
        }
    }

    // ── AnchorMover ─────────────────────────────────────────────────────

    /// <summary>Moves anchors that are within 5 ms of a note or chord onto it (not onto a slide's end).</summary>
    private static void MoveAnchors(DdArrangement arr)
    {
        foreach (var level in arr.Levels)
        {
            var times = level.Notes.Select(n => n.Time).Concat(level.Chords.Select(c => c.Time)).Distinct().Order().ToArray();
            var slideEnds = level.Chords
                .Where(c => c.HasChordNotes && c.ChordNotes!.Exists(n => n.IsSlide))
                .Select(c => c.ChordNotes!.Find(n => n.IsSlide)!)
                .Select(n => n.Time + n.Sustain)
                .Concat(level.Notes.Where(n => n.IsSlide).Select(n => n.Time + n.Sustain))
                .ToHashSet();
            foreach (var anchor in level.Anchors)
            {
                if (Array.BinarySearch(times, anchor.Time) >= 0) continue;
                var near = times.Cast<int?>().FirstOrDefault(t => t != anchor.Time && Math.Abs(anchor.Time - t!.Value) <= 5);
                if (near is { } noteTime && !slideEnds.Contains(anchor.Time)) anchor.Time = noteTime;
            }
        }
    }

    // ── PhraseMover ─────────────────────────────────────────────────────

    /// <summary>Moves the iterations of a phrase named "moverN" right by N notes.</summary>
    private static void MovePhrases(DdArrangement arr)
    {
        foreach (var phrase in arr.Phrases.Where(p => p.Name.StartsWith("mover", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            var phraseId = arr.Phrases.IndexOf(phrase);
            foreach (var iteration in arr.PhraseIterations.Where(pi => pi.PhraseId == phraseId))
            {
                var phraseTime = iteration.Time;
                if (!int.TryParse(phrase.Name.AsSpan("mover".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var moveBy))
                    throw new InvalidOperationException($"Unable to parse value for 'moveR' phrase at {TimeToString(phraseTime)}");
                var moveToTime = TimeOfNthNoteFrom(arr.Levels[phrase.MaxDifficulty], phraseTime, moveBy);

                // Check if anchor(s) should be moved
                foreach (var level in arr.Levels.TakeWhile(l => l.Difficulty <= phrase.MaxDifficulty))
                {
                    var anchors = level.Anchors;
                    var originalIndex = FindIndexByTime(anchors, a => a.Time, phraseTime);
                    if (originalIndex == -1 || FindIndexByTime(anchors, a => a.Time, moveToTime) != -1) continue;
                    var original = anchors[originalIndex];
                    InsertByTime(anchors, a => a.Time, new DdAnchor(moveToTime, original.Fret, original.Width));

                    // Remove anchor at original phrase position if no note or chord present
                    if (FindIndexByTime(level.Notes, n => n.Time, phraseTime) == -1
                        && FindIndexByTime(level.Chords, c => c.Time, phraseTime) == -1)
                    {
                        anchors.RemoveAt(originalIndex);
                    }
                }

                iteration.Time = moveToTime;
                var section = FindIndexByTime(arr.Sections, s => s.Time, phraseTime);
                if (section != -1) arr.Sections[section].Time = moveToTime;
            }
        }
    }

    // ── EOFFixes ────────────────────────────────────────────────────────

    [GeneratedRegex("^E[0-2]$")]
    private static partial Regex UpperCaseCrowdSpeed();

    private static void EofFixes(DdArrangement arr)
    {
        // Fixes incorrect crowd events: E0, E1, E2.
        foreach (var e in arr.Events.Where(e => UpperCaseCrowdSpeed().IsMatch(e.Code))) e.Code = e.Code.ToLowerInvariant();

        // Linknext on chords whose chord notes have it; the same sustain on every chord note.
        foreach (var chord in arr.Levels.SelectMany(l => l.Chords).Where(c => c.HasChordNotes))
        {
            var sustain = chord.ChordNotes!.Max(cn => cn.Sustain);
            chord.ChordNotes!.ForEach(cn => cn.Sustain = sustain);
            if (!chord.IsLinkNext && chord.ChordNotes.Exists(cn => cn.IsLinkNext)) chord.IsLinkNext = true;
        }

        // Linknext off chord notes not immediately followed by a note on the same string.
        foreach (var level in arr.Levels)
        {
            foreach (var chord in level.Chords.Where(c => c.IsLinkNext && c.HasChordNotes))
            {
                foreach (var cn in chord.ChordNotes!.Where(cn => cn.IsLinkNext))
                {
                    var note = level.Notes.Find(n => n.Time > cn.Time && n.String == cn.String);
                    if (note == null || note.Time - cn.Time - cn.Sustain > 2) cn.IsLinkNext = false;
                }
            }
        }

        // Shortens handshapes of chords that include the slide-to notes.
        foreach (var level in arr.Levels)
        {
            foreach (var chord in level.Chords.Where(c => c.IsLinkNext && c.HasChordNotes && c.ChordNotes!.Exists(cn => cn.IsSlide)))
            {
                var hs = level.HandShapes.Find(h => h.StartTime == chord.Time);
                var sustain = chord.ChordNotes![0].Sustain;
                if (hs != null && hs.EndTime > hs.StartTime + sustain) hs.EndTime = hs.StartTime + sustain;
            }
        }

        FixPhraseStartAnchors(arr);
    }

    /// <summary>Ensures that there is an anchor at the start of each phrase (COUNT and END skipped).</summary>
    private static void FixPhraseStartAnchors(DdArrangement arr)
    {
        foreach (var level in arr.Levels)
        {
            var anchors = level.Anchors;
            for (var i = 1; i <= arr.PhraseIterations.Count - 2; i++)
            {
                var piTime = arr.PhraseIterations[i].Time;
                var active = anchors.FindLast(a => a.Time <= piTime);
                if (active != null && active.Time != piTime)
                {
                    InsertByTime(anchors, a => a.Time, new DdAnchor(piTime, active.Fret, active.Width));
                }
                else if (active == null && anchors.Find(a => a.Time > piTime) is { } next)
                {
                    // Copy the anchor if it is at the start of the next phrase, otherwise move it
                    if (arr.PhraseIterations.Exists(pi => pi.Time == next.Time))
                        InsertByTime(anchors, a => a.Time, new DdAnchor(piTime, next.Fret, next.Width));
                    else
                        next.Time = piTime;
                }
            }
        }
    }

    // ── UnnecessaryNoteRemover ──────────────────────────────────────────

    /// <summary>Removes notes that come after LinkNext, but have no sustain.</summary>
    private static void RemoveUnnecessaryNotes(DdArrangement arr)
    {
        foreach (var level in arr.Levels)
        {
            var notesToRemove = new List<DdNote>();
            var notes = level.Notes;
            for (var i = 0; i < notes.Count; i++)
            {
                var note = notes[i];
                if (!note.IsLinkNext) continue;
                if (NextNoteOnSameString(notes, i, note) is { Sustain: 0 } next)
                {
                    notesToRemove.Add(next);
                    note.IsLinkNext = false;
                }
            }

            foreach (var chord in level.Chords.Where(c => c.IsLinkNext && c.ChordNotes != null))
            {
                var noteIndex = notes.FindIndex(n => n.Time > chord.Time);
                if (noteIndex == -1) continue;
                foreach (var chordNote in chord.ChordNotes!.Where(cn => cn.IsLinkNext))
                {
                    if (NextNoteOnSameString(notes, noteIndex - 1, chordNote) is { Sustain: 0 } next)
                    {
                        chordNote.IsLinkNext = false;
                        notesToRemove.Add(next);
                    }
                }

                chord.IsLinkNext = chord.ChordNotes!.Exists(cn => cn.IsLinkNext);
            }

            foreach (var note in notesToRemove) notes.Remove(note);
        }
    }

    // ── CrowdEventAdder ─────────────────────────────────────────────────

    [GeneratedRegex("e[0-2]$")]
    private static partial Regex CrowdSpeedCode();

    /// <summary>Adds the crowd speed, intro and outro applause events when absent.</summary>
    private static void AddCrowdEvents(DdArrangement arr)
    {
        var events = arr.Events;
        if (!events.Exists(e => CrowdSpeedCode().IsMatch(e.Code)))
        {
            var speed = arr.AverageTempo < 90f ? "e0" : arr.AverageTempo < 170f ? "e1" : "e2";
            InsertByTime(events, e => e.Time, new DdEvent(speed, arr.StartBeat));
        }

        if (!events.Exists(e => e.Code == "E3"))
        {
            var start = FirstNoteTime(arr) is { } first ? first + 600 : arr.StartBeat;
            InsertByTime(events, e => e.Time, new DdEvent("E3", start));
            InsertByTime(events, e => e.Time, new DdEvent("E13", start + 2_500));
        }

        if (!events.Exists(e => e.Code == "D3"))
        {
            InsertByTime(events, e => e.Time, new DdEvent("D3", arr.SongLength - 5_000 - 4_000));
            InsertByTime(events, e => e.Time, new DdEvent("E13", arr.SongLength));
        }
    }

    private static int? FirstNoteTime(DdArrangement arr)
    {
        DdLevel? level;
        if (arr.Levels.Count == 1)
        {
            level = arr.Levels[0];
        }
        else if (arr.PhraseIterations.Find(pi => arr.Phrases[pi.PhraseId].MaxDifficulty > 0) is { } first)
        {
            level = arr.Levels[arr.Phrases[first.PhraseId].MaxDifficulty];
        }
        else
        {
            level = arr.Levels.Find(l => l.Notes.Count > 0 || l.Chords.Count > 0);
        }

        if (level == null) return null;
        int? note = level.Notes.Count > 0 ? level.Notes[0].Time : null;
        int? chord = level.Chords.Count > 0 ? level.Chords[0].Time : null;
        return MinOf(note, chord);
    }

    // ── ChordNameProcessor ──────────────────────────────────────────────

    private static void ProcessChordNames(DdArrangement arr)
    {
        foreach (var template in arr.ChordTemplates)
        {
            template.Name = string.IsNullOrWhiteSpace(template.Name)
                ? ""
                : template.Name.Replace("min", "m").Replace("CONV", "").Replace("-nop", "").Replace("-arp", "");
            template.DisplayName = string.IsNullOrWhiteSpace(template.DisplayName)
                ? ""
                : template.DisplayName.Replace("min", "m").Replace("CONV", "-arp");
        }
    }

    // ── CustomEvents ────────────────────────────────────────────────────

    private static void ProcessCustomEvents(DdArrangement arr)
    {
        var events = arr.Events;

        // Anchor width 3 events
        foreach (var e in events.Where(e => e.Code.StartsWith("w3", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            var parts = e.Code.Split("-");
            int? fret = parts.Length > 1 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : null;
            // Supports only arrangements with no DD levels
            if (arr.Levels[0].Anchors.Find(a => a.Time >= e.Time) is { } anchor)
            {
                anchor.Width = 3;
                if (fret is >= 1 and <= 22) anchor.Fret = fret.Value;
            }

            events.Remove(e);
        }

        // The "removebeats" event: consumed; the beats are the project's (see the type's summary).
        if (events.Find(e => string.Equals(e.Code, "removebeats", StringComparison.OrdinalIgnoreCase)) is { } removeBeats)
            events.Remove(removeBeats);

        // Slide-out events
        foreach (var slideEvent in events.Where(e => e.Code.StartsWith("so", StringComparison.OrdinalIgnoreCase)).ToList())
            SlideOut(arr, slideEvent);
    }

    private static void SlideOut(DdArrangement arr, DdEvent slideEvent)
    {
        // Find the max level for the phrase the slide is in
        var iteration = arr.PhraseIterations.FindLast(pi => pi.Time <= slideEvent.Time)!;
        var level = arr.Levels[arr.Phrases[iteration.PhraseId].MaxDifficulty];

        int slideTime;
        if (slideEvent.Code.Length > 2)
        {
            // The time of the chord or note right of the event by the number given
            if (!int.TryParse(slideEvent.Code.AsSpan(2), NumberStyles.Integer, CultureInfo.InvariantCulture, out var rightBy))
                return; // Some other event beginning with "so"
            slideTime = TimeOfNthNoteFrom(level, slideEvent.Time, rightBy);
        }
        else
        {
            slideTime = slideEvent.Time;
        }

        var noteIndex = FindIndexByTime(level.Notes, n => n.Time, slideTime);
        var chordIndex = FindIndexByTime(level.Chords, c => c.Time, slideTime);
        if (noteIndex == -1 && chordIndex == -1)
            throw new InvalidOperationException($"Could not find the notes or chord for slide-out event at {TimeToString(slideEvent.Time)}");

        DdNote[] notes;
        DdChordTemplate original;
        if (chordIndex == -1)
        {
            // These are notes that follow a LinkNext chord
            var linkNextChord = level.Chords.FindLast(c => c.Time < slideTime)!;
            var chordHs = level.HandShapes.Find(hs => hs.StartTime == linkNextChord.Time);
            // Shorten hand shapes that include the slide out notes
            var end = linkNextChord.Time + linkNextChord.ChordNotes![0].Sustain;
            if (chordHs != null && chordHs.EndTime > end) chordHs.EndTime = end;
            notes = [.. level.Notes.FindAll(n => n.Time == slideTime && n.IsUnpitchedSlide)];
            original = arr.ChordTemplates[linkNextChord.ChordId];
        }
        else
        {
            // A normal chord with an unpitched slide out
            var chord = level.Chords[chordIndex];
            if (chord.ChordNotes == null)
                throw new InvalidOperationException($"Chord missing chord notes for SlideOut event at {TimeToString(slideEvent.Time)}");
            // The length of the handshape likely needs to be shortened
            if (level.HandShapes.Find(hs => hs.StartTime == chord.Time) is { } chordHs)
                chordHs.EndTime = chordHs.StartTime + ((chordHs.EndTime - chordHs.StartTime) / 3);
            notes = [.. chord.ChordNotes.FindAll(cn => cn.IsUnpitchedSlide)];
            original = arr.ChordTemplates[chord.ChordId];
        }

        if (notes.Length == 0) throw new InvalidOperationException($"Invalid SlideOut event at {TimeToString(slideEvent.Time)}");

        // A new handshape at the slide end (1 ms more to include the notes' sustain)
        var endTime = notes[0].Time + notes[0].Sustain + 1;
        var startTime = endTime - (notes[0].Sustain / 3);
        InsertByTime(level.HandShapes, hs => hs.StartTime, new DdHandShape(arr.ChordTemplates.Count, startTime, endTime));

        // A new chord template for the handshape
        var frets = Enumerable.Repeat(-1, arr.StringSlots).ToArray();
        var fingers = Enumerable.Repeat(-1, arr.StringSlots).ToArray();
        foreach (var note in notes)
        {
            frets[note.String] = note.SlideUnpitchTo;
            fingers[note.String] = original.Fingers[note.String];
        }

        arr.ChordTemplates.Add(new DdChordTemplate("", "", fingers, frets));
        arr.Events.Remove(slideEvent);
    }

    // ── HandShapeAdjuster ───────────────────────────────────────────────

    private static (DdBeat Beat1, DdBeat Beat2) FindBeats(List<DdBeat> beats, int time)
    {
        var next = beats.FindIndex(b => b.Time > time);
        // For handshapes that come after the last beat (bad XML) use the last beat
        if (next == -1) next = beats.Count - 1;
        return (beats[next - 1], beats[next]);
    }

    /// <summary>Lengthens handshapes that end with a chord.</summary>
    private static void LengthenHandShapes(DdArrangement arr)
    {
        foreach (var level in arr.Levels)
        {
            var chordIndex = 0;
            var hsIndex = 0;
            while (hsIndex < level.HandShapes.Count && chordIndex < level.Chords.Count)
            {
                var hs = level.HandShapes[hsIndex];
                var lastChordIndex = LastChordMaybeInside(chordIndex, level.Chords, hs);
                if (lastChordIndex < 0) break;

                var time = level.Chords[lastChordIndex].Time;
                // Lengthen the handshape if the chord is within 10ms from the handshape end time
                if (time >= hs.StartTime && time <= hs.EndTime && hs.EndTime - time <= 10)
                {
                    var (beat1, beat2) = FindBeats(arr.Beats, time);
                    var note16th = (beat2.Time - beat1.Time) / 4;
                    var nextAnchor = level.Anchors.Find(a => a.Time > time)?.Time;
                    var nextPhrase = arr.PhraseIterations.Find(p => p.Time > time)?.Time;
                    // Add 1ms to skip this chord
                    var nextContent = NextContentTime(level, time + 1);
                    var newEndTime = hs.EndTime + note16th;
                    var nextTime = MinOf(MinOf(nextAnchor, nextPhrase), nextContent);
                    hs.EndTime = nextTime is { } t && newEndTime >= t
                        // Overlaps the next note, chord or handshape: half the distance to it
                        ? hs.EndTime + ((t - hs.EndTime) / 2)
                        : newEndTime;
                }

                chordIndex = lastChordIndex;
                hsIndex++;
            }
        }

        static int LastChordMaybeInside(int index, List<DdChord> chords, DdHandShape hs)
        {
            while (true)
            {
                // Reached the last chord in the arrangement (assume that it is within a handshape)
                if (index >= chords.Count) return index - 1;
                // Handshape end passed, return the index of the previous chord
                if (chords[index].Time > hs.EndTime) return index - 1;
                index++;
            }
        }
    }

    /// <summary>Shortens the lengths of handshapes that are too close to the next one.</summary>
    private static void ShortenHandShapes(DdArrangement arr)
    {
        foreach (var level in arr.Levels)
        {
            var handShapes = level.HandShapes;
            for (var i = 1; i < handShapes.Count; i++)
            {
                var followingStart = handShapes[i].StartTime;
                var followingEnd = handShapes[i].EndTime;
                var preceding = handShapes[i - 1];
                var precedingStart = preceding.StartTime;
                var precedingEnd = preceding.EndTime;

                // Ignore nested handshapes
                if (precedingEnd >= followingEnd) continue;
                var (beat1, beat2) = FindBeats(arr.Beats, precedingEnd);
                var note32nd = (beat2.Time - beat1.Time) / 8;
                // If it is a chord slide and the handshape length is an 8th note or longer
                var shortenBy16th = IsChordSlideAt(level, precedingStart) && precedingEnd - precedingStart > note32nd * 4;
                var minDistance = precedingEnd - precedingStart <= note32nd
                    ? (beat2.Time - beat1.Time) / 12
                    : shortenBy16th ? note32nd * 2 : note32nd;

                if (followingStart - precedingEnd >= minDistance) continue;
                var time = followingStart - minDistance;
                // For very small note values
                preceding.EndTime = time <= precedingStart ? precedingStart + ((precedingEnd - precedingStart) / 2) : time;
            }
        }

        static bool IsChordSlideAt(DdLevel level, int time)
        {
            var index = FindIndexByTime(level.Chords, c => c.Time, time);
            if (index == -1) return false;
            var chord = level.Chords[index];
            return chord.IsLinkNext && chord.HasChordNotes && chord.ChordNotes!.Exists(cn => cn.IsSlide);
        }
    }

    // ── Utils ───────────────────────────────────────────────────────────

    private static DdChordTemplate? Template(DdArrangement arr, int id) =>
        id >= 0 && id < arr.ChordTemplates.Count ? arr.ChordTemplates[id] : null;

    private static int? MinOf(int? a, int? b) => a is { } x ? b is { } y ? Math.Min(x, y) : x : b;

    /// <summary><c>tryFindNextNoteOnSameString</c>.</summary>
    private static DdNote? NextNoteOnSameString(List<DdNote> notes, int currentIndex, DdNote note)
    {
        var next = currentIndex == -1
            ? notes.FindIndex(n => n.Time > note.Time && n.String == note.String)
            : notes.FindIndex(currentIndex + 1, n => n.String == note.String);
        return next == -1 ? null : notes[next];
    }

    /// <summary><c>tryFindNextContentTime</c>: the next note, chord or handshape at or after <paramref name="time" />.</summary>
    private static int? NextContentTime(DdLevel level, int time) =>
        MinOf(MinOf(level.Notes.Find(n => n.Time >= time)?.Time, level.Chords.Find(c => c.Time >= time)?.Time),
            level.HandShapes.Find(h => h.StartTime >= time)?.StartTime);

    /// <summary><c>findTimeOfNthNoteFrom</c>: notes on one time (split chords) count once.</summary>
    private static int TimeOfNthNoteFrom(DdLevel level, int startTime, int nthNote)
    {
        var notes = level.Notes.Select(n => n.Time).Where(t => t >= startTime).Distinct().Take(nthNote);
        var chords = level.Chords.Select(c => c.Time).Where(t => t >= startTime).Distinct().Take(nthNote);
        return chords.Concat(notes).Order().Skip(nthNote - 1).First();
    }

    /// <summary>The library's <c>FindIndexByTime</c>: a linear search that stops past the time.</summary>
    private static int FindIndexByTime<T>(List<T> items, Func<T, int> time, int timeToFind)
    {
        for (var i = 0; i < items.Count; i++)
        {
            var t = time(items[i]);
            if (t == timeToFind) return i;
            if (t > timeToFind) return -1;
        }

        return -1;
    }

    /// <summary>The library's <c>InsertByTime</c>: before the first element that comes later.</summary>
    private static void InsertByTime<T>(List<T> items, Func<T, int> time, T item)
    {
        var index = items.FindIndex(x => time(x) > time(item));
        if (index != -1) items.Insert(index, item);
        else items.Add(item);
    }

    private static string TimeToString(int time)
    {
        var minutes = time / 1000 / 60;
        var seconds = (time / 1000) - (minutes * 60);
        var ms = time - (minutes * 60 * 1000) - (seconds * 1000);
        return $"{minutes:D2}:{seconds:D2}.{ms:D3}";
    }
}
