using System.Xml.Linq;
using DotCharter.Services.Parsing.MoverPhrases;

namespace DotCharter.Services.Parsing.Xml.Song;

/// <summary>One moveR iteration: where it was written and where it really starts.</summary>
public readonly record struct MoverMove(string Phrase, int FromMs, int ToMs);

/// <summary>
///     The moveR&lt;N&gt; phrase on a Rocksmith XML <c>&lt;song&gt;</c>, both ways.
///     <para>
///         <see cref="Apply" /> is DLC Builder's <c>PhraseMover.improve</c>
///         (<c>Rocksmith2014.XML.Processing/Improvers/PhraseMover.fs</c>) step for step, on
///         the sparse levels the file holds: every iteration of a phrase whose name starts
///         with "mover" moves to the time of the N-th note from it in the level at the
///         phrase's <c>maxDifficulty</c>; the section at the old time moves with it; in each
///         level up to that difficulty an anchor at the old time is copied to the new time
///         when none is there, and the old one is removed when no note or chord is left on
///         it.
///     </para>
///     <para>
///         <see cref="ResolveForImport" /> runs it on the way in, but only for an iteration
///         that still sits on a beat — an XML converted from a package DLC Builder already
///         built has the iteration at the note under the same name, and moving it again
///         would push it a further N notes. Then the moved phrase gets a real name.
///         <see cref="ToMoverForm" /> is the inverse, for tools that want the moveR form.
///     </para>
/// </summary>
public static class RsXmlPhraseMover
{
    /// <summary>
    ///     DLC Builder's <c>PhraseMover.improve</c>. <paramref name="shouldMove" /> sees each
    ///     moveR iteration's time; null moves them all, as DLC Builder does. A name whose
    ///     number does not parse, or too few notes after it, is where DLC Builder fails the
    ///     build: that iteration is left in place and listed in <paramref name="unresolved" />.
    /// </summary>
    public static List<MoverMove> Apply(XDocument doc, Func<int, bool>? shouldMove = null,
        List<MoverMove>? unresolved = null)
    {
        var moves = new List<MoverMove>();
        var song = doc.Root;
        if (song is null) return moves;

        var phrases = song.Element(E.Phrases)?.Elements(E.Phrase).ToList() ?? [];
        var iterations = song.Element(E.PhraseIterations)?.Elements(E.PhraseIteration).ToList() ?? [];
        var levels = song.Element(E.Levels)?.Elements(E.Level).ToList() ?? [];

        for (var phraseId = 0; phraseId < phrases.Count; phraseId++)
        {
            var phrase = phrases[phraseId];
            var name = Fmt.AttrStr(phrase, E.Name);
            if (!MoverPhraseName.IsDlcBuilderMover(name)) continue;

            var maxDifficulty = Fmt.AttrInt(phrase, E.MaxDifficulty);
            foreach (var iteration in iterations.Where(pi => Fmt.AttrInt(pi, E.PhraseId) == phraseId))
            {
                var phraseTime = Fmt.ParseTime(iteration.Attribute(E.Time)?.Value);
                if (shouldMove is not null && !shouldMove(phraseTime)) continue;

                int? movetoTime = null;
                if (MoverPhraseName.TryParseDlcBuilder(name, out var moveBy) && maxDifficulty < levels.Count)
                {
                    var level = levels[maxDifficulty];
                    movetoTime = MoverNoteCount.FindTimeOfNthNoteFrom(Times(level, E.Notes, E.Note),
                        Times(level, E.Chords, E.Chord), phraseTime, moveBy);
                }

                if (movetoTime is not { } moveto)
                {
                    unresolved?.Add(new MoverMove(name, phraseTime, phraseTime));
                    continue;
                }

                foreach (var level in levels.TakeWhile(l => Fmt.AttrInt(l, E.Difficulty) <= maxDifficulty))
                    MoveAnchor(level, phraseTime, moveto);

                iteration.SetAttributeValue(E.Time, Fmt.Time(moveto));
                var section = SectionAt(song, phraseTime);
                section?.SetAttributeValue(E.StartTime, Fmt.Time(moveto));
                moves.Add(new MoverMove(name, phraseTime, moveto));
            }
        }

        return moves;
    }

    /// <summary>
    ///     The import half: <see cref="Apply" /> for every moveR iteration still on a beat,
    ///     then every moveR phrase none of whose iterations is left on a beat is renamed —
    ///     DLC Builder keeps "moveR2", and a chart that kept it would move the phrase again on
    ///     its next trip through DLC Builder. The new name is the section its first
    ///     iteration starts, else "phrase", made unique. A moveR that could not move keeps
    ///     its name, so the validation still flags it.
    /// </summary>
    public static List<MoverMove> ResolveForImport(XDocument doc, List<MoverMove>? unresolved = null)
    {
        var song = doc.Root;
        if (song is null) return [];

        var beatTimes = song.Element(E.Ebeats)?.Elements(E.Ebeat)
            .Select(b => Fmt.ParseTime(b.Attribute(E.Time)?.Value)).ToHashSet() ?? [];
        var moves = Apply(doc, beatTimes.Contains, unresolved);

        var phrases = song.Element(E.Phrases)?.Elements(E.Phrase).ToList() ?? [];
        var iterations = song.Element(E.PhraseIterations)?.Elements(E.PhraseIteration).ToList() ?? [];
        var taken = phrases.Select(p => Fmt.AttrStr(p, E.Name)).ToList();
        for (var phraseId = 0; phraseId < phrases.Count; phraseId++)
        {
            if (!MoverPhraseName.IsDlcBuilderMover(Fmt.AttrStr(phrases[phraseId], E.Name))) continue;

            var times = iterations.Where(pi => Fmt.AttrInt(pi, E.PhraseId) == phraseId)
                .Select(pi => Fmt.ParseTime(pi.Attribute(E.Time)?.Value)).ToList();
            if (times.Count == 0 || times.Any(beatTimes.Contains)) continue;

            var section = SectionAt(song, times[0]);
            var newName = MoverPhraseName.FallbackName(section is null ? null : Fmt.AttrStr(section, E.Name), taken);
            taken.Add(newName);
            phrases[phraseId].SetAttributeValue(E.Name, newName);
        }

        return moves;
    }

    /// <summary>
    ///     The export half, for tools that expect moveR: each phrase iteration between two
    ///     beats that starts on a note is written on the beat before it as moveR&lt;N&gt;,
    ///     and the section that starts with it goes to the beat too — so <see cref="Apply" />
    ///     puts both back where they were. An iteration is left at its real time when it is
    ///     not on a note, or when another phrase iteration or section sits between that beat
    ///     and it (DLC Builder would move the wrong section), or when a level up to the
    ///     phrase's difficulty has an anchor on the beat and none on the note (DLC Builder
    ///     would move that anchor, so the trip would not come back unchanged).
    /// </summary>
    public static List<MoverMove> ToMoverForm(XDocument doc)
    {
        var moves = new List<MoverMove>();
        var song = doc.Root;
        var phrasesEl = song?.Element(E.Phrases);
        if (song is null || phrasesEl is null) return moves;

        var beatTimes = (song.Element(E.Ebeats)?.Elements(E.Ebeat) ?? [])
            .Select(b => Fmt.ParseTime(b.Attribute(E.Time)?.Value)).Order().ToList();
        var phrases = phrasesEl.Elements(E.Phrase).ToList();
        var iterations = song.Element(E.PhraseIterations)?.Elements(E.PhraseIteration).ToList() ?? [];
        var sections = song.Element(E.Sections)?.Elements(E.Section).ToList() ?? [];
        var levels = song.Element(E.Levels)?.Elements(E.Level).ToList() ?? [];
        var iterationTimes = iterations.Select(pi => Fmt.ParseTime(pi.Attribute(E.Time)?.Value)).ToList();
        var sectionTimes = sections.Select(s => Fmt.ParseTime(s.Attribute(E.StartTime)?.Value)).ToList();

        var planned = new List<(XElement Iteration, int PhraseId, int Beat, int Time, int MoveBy)>();
        for (var i = 0; i < iterations.Count; i++)
        {
            var time = iterationTimes[i];
            var phraseId = Fmt.AttrInt(iterations[i], E.PhraseId);
            if (phraseId < 0 || phraseId >= phrases.Count) continue;
            if (MoverPhraseName.IsDlcBuilderMover(Fmt.AttrStr(phrases[phraseId], E.Name))) continue;

            var beatIndex = beatTimes.FindLastIndex(b => b <= time);
            if (beatIndex < 0 || beatTimes[beatIndex] == time) continue;
            var beat = beatTimes[beatIndex];
            if (iterationTimes.Any(t => t >= beat && t < time)) continue;
            if (sectionTimes.Any(t => t >= beat && t < time)) continue;

            var maxDifficulty = Fmt.AttrInt(phrases[phraseId], E.MaxDifficulty);
            if (maxDifficulty >= levels.Count) continue;
            var reached = levels.TakeWhile(l => Fmt.AttrInt(l, E.Difficulty) <= maxDifficulty);
            if (reached.Any(l => WouldMoveAnchor(l, beat, time))) continue;
            var level = levels[maxDifficulty];
            var moveBy = MoverNoteCount.CountTo(Times(level, E.Notes, E.Note), Times(level, E.Chords, E.Chord), beat, time);
            if (moveBy is null) continue;

            planned.Add((iterations[i], phraseId, beat, time, moveBy.Value));
        }

        foreach (var (iteration, phraseId, beat, time, _) in planned)
        {
            SectionAt(song, time)?.SetAttributeValue(E.StartTime, Fmt.Time(beat));
            iteration.SetAttributeValue(E.Time, Fmt.Time(beat));
            moves.Add(new MoverMove(Fmt.AttrStr(phrases[phraseId], E.Name), time, beat));
        }

        RenameToMover(phrasesEl, phrases, iterations, planned.Select(p => (p.Iteration, p.PhraseId, p.MoveBy)).ToList());
        return moves;
    }

    /// <summary>
    ///     A phrase every one of whose iterations moved by the same N is renamed in place;
    ///     otherwise each N gets its own copy of the phrase. Names are kept unique — DLC
    ///     Builder finds a phrase's index by <c>IndexOf</c> — by a leading zero, which both
    ///     tools still read as the same number.
    /// </summary>
    private static void RenameToMover(XElement phrasesEl, List<XElement> phrases, List<XElement> iterations,
        List<(XElement Iteration, int PhraseId, int MoveBy)> planned)
    {
        var taken = phrases.Select(p => Fmt.AttrStr(p, E.Name)).ToList();
        foreach (var group in planned.GroupBy(p => p.PhraseId))
        {
            var phrase = phrases[group.Key];
            var allIterations = iterations.Count(pi => Fmt.AttrInt(pi, E.PhraseId) == group.Key);
            var byMove = group.GroupBy(p => p.MoveBy).ToList();
            if (byMove.Count == 1 && byMove[0].Count() == allIterations)
            {
                phrase.SetAttributeValue(E.Name, UniqueMoverName(byMove[0].Key, taken));
                continue;
            }

            foreach (var sameMove in byMove)
            {
                var copy = new XElement(phrase);
                copy.SetAttributeValue(E.Name, UniqueMoverName(sameMove.Key, taken));
                var newId = phrasesEl.Elements(E.Phrase).Count();
                phrasesEl.Add(copy);
                foreach (var (iteration, _, _) in sameMove) iteration.SetAttributeValue(E.PhraseId, newId);
            }
        }

        phrasesEl.SetAttributeValue(E.Count, phrasesEl.Elements(E.Phrase).Count());
    }

    private static string UniqueMoverName(int moveBy, List<string> taken)
    {
        var name = MoverPhraseName.Format(moveBy);
        while (taken.Any(t => string.Equals(t, name, StringComparison.OrdinalIgnoreCase)))
            name = "moveR0" + name["moveR".Length..];
        taken.Add(name);
        return name;
    }

    private static void MoveAnchor(XElement level, int phraseTime, int movetoTime)
    {
        var anchorsEl = level.Element(E.Anchors);
        if (anchorsEl is null) return;

        var original = AnchorAt(anchorsEl, phraseTime);
        if (original is null || AnchorAt(anchorsEl, movetoTime) is not null) return;

        InsertAnchor(anchorsEl, original, movetoTime);
        if (!Times(level, E.Notes, E.Note).Contains(phraseTime) && !Times(level, E.Chords, E.Chord).Contains(phraseTime))
            original.Remove();
        anchorsEl.SetAttributeValue(E.Count, anchorsEl.Elements(E.Anchor).Count());
    }

    private static bool WouldMoveAnchor(XElement level, int beat, int time)
    {
        var anchorsEl = level.Element(E.Anchors);
        return anchorsEl is not null && AnchorAt(anchorsEl, beat) is not null && AnchorAt(anchorsEl, time) is null;
    }

    private static void InsertAnchor(XElement anchorsEl, XElement template, int time)
    {
        var copy = new XElement(template);
        copy.SetAttributeValue(E.Time, Fmt.Time(time));
        var after = anchorsEl.Elements(E.Anchor).LastOrDefault(a => Fmt.ParseTime(a.Attribute(E.Time)?.Value) < time);
        if (after is null) anchorsEl.AddFirst(copy);
        else after.AddAfterSelf(copy);
    }

    private static XElement? AnchorAt(XElement anchorsEl, int time) =>
        anchorsEl.Elements(E.Anchor).FirstOrDefault(a => Fmt.ParseTime(a.Attribute(E.Time)?.Value) == time);

    private static XElement? SectionAt(XElement song, int time) =>
        song.Element(E.Sections)?.Elements(E.Section)
            .FirstOrDefault(s => Fmt.ParseTime(s.Attribute(E.StartTime)?.Value) == time);

    private static List<int> Times(XElement level, string listName, string itemName) =>
        level.Element(listName)?.Elements(itemName).Select(n => Fmt.ParseTime(n.Attribute(E.Time)?.Value)).ToList()
        ?? [];
}
