using DotCharter.Services.State;

namespace DotCharter.Services.Editing.Collisions;

public static class ChartSpacingEnforcer
{
    private const double AnchorSnapToleranceMs = 5.0;

    /// <summary>RS display suffix for a template forced to render as an arpeggio.</summary>
    private const string ArpeggioSuffix = "-arp";

    /// <summary>Legacy RS-tooling marker for a converted chord name; carries no meaning here.</summary>
    private const string ConvertedMarker = "CONV";

    public static void EnforceProject(ChartProject? project, AppConfig config)
    {
        if (project == null) return;

        foreach (var arrangement in project.Arrangements)
            EnforceArrangement(arrangement, project.Beats, config);
    }

    public static void EnforceActiveLevel(ProjectState projectState, AppConfig config) => EnforceLevel(projectState.ActiveLevel, projectState.Beats, config);

    public static void EnforceLevel(ChartLevel? level, IList<BeatMarker> beats, AppConfig config)
    {
        if (level == null) return;
        EnforceLevel(level, null, beats, config, false);
    }

    public static void EnforceArrangement(Arrangement? arrangement, IList<BeatMarker> beats, AppConfig config)
    {
        if (arrangement == null) return;

        NormalizeArrangementMetadata(arrangement);
        foreach (var level in arrangement.Levels)
            EnforceLevel(level, arrangement, beats, config, true);
    }

    private static void EnforceLevel(ChartLevel level, Arrangement? arrangement, IList<BeatMarker> beats,
        AppConfig config, bool runCompatibilityCleanup)
    {
        if (runCompatibilityCleanup && arrangement != null)
            EnforceRocksmithCompatibility(level, arrangement, beats);

        EnforceSpacing(level, beats, config);

        if (runCompatibilityCleanup && arrangement != null)
        {
            // AFTER the two sustain passes on purpose — see DeriveIgnoreFlags.
            DeriveIgnoreFlags(level, arrangement);
            SortByPosition(level.Anchors, a => a.Position);
            RemoveRedundantAnchors(level, arrangement);
            // No second anchor sort: RemoveRedundantAnchors only ever REMOVES,
            // which cannot un-sort what the line above just sorted. The repeat
            // was a provable no-op that still paid a full scan of the anchors.
            SortByPosition(level.Notes, n => n.Position);
            SortByPosition(level.Chords, c => c.Position);
            SortByPosition(level.HandShapes, h => h.Position);
        }
    }

    /// <summary>
    ///     Strips the RS display decorations from chord template names.
    ///     <para>
    ///         Phrase names are deliberately NOT touched. Rewriting
    ///         them to Rocksmith's <c>[a-zA-Z0-9 _#]</c> on the live project, on
    ///         every save and load would destroy the charter's names
    ///         (<c>Solo (fast)</c> → <c>Solo fast</c>, <c>サビ</c> → <c>""</c>),
    ///         merge distinct phrases, and orphan every
    ///         <c>Arrangement.PhraseInfos</c> entry (solo flag, max difficulty),
    ///         which is keyed by the name. Rocksmith's spelling is made at the
    ///         export boundary by <c>Export/Rocksmith/RsPhraseName</c>, and the
    ///         inspector says which names it will rewrite (<c>PhraseRule</c>, D10).
    ///     </para>
    /// </summary>
    private static void NormalizeArrangementMetadata(Arrangement arrangement)
    {
        foreach (var template in arrangement.ChordTemplates)
        {
            if (string.IsNullOrWhiteSpace(template.Name)) continue;
            StripRsDisplaySuffixes(template);
        }
    }

    /// <summary>
    ///     Removes the RS DISPLAY-name decorations a chord's stored
    ///     <see cref="ChordTemplate.Name" /> must not carry, and only those.
    ///     <para>
    ///     Four unanchored <c>String.Replace</c>
    ///     calls over the whole name would each be wrong:
    ///     </para>
    ///     <para>
    ///     <c>"min" -> "m"</c> is GONE, not anchored. It contradicted the app's own
    ///     chord namer: <see cref="ChordNameSuggester" /> deliberately spells minor
    ///     chords <c>min</c> (the Editor on Fire / RS2014 convention the ground
    ///     truth uses — measured, that spelling lifted chord-name exact agreement
    ///     against the hand-fingered EOF corpus from ~47% to ~86%), so every
    ///     auto-named minor template was rewritten <c>Amin -> Am</c> on the first
    ///     save and every EOF/Log-Charter chart was mangled the moment it was
    ///     OPENED. As a substring replace it also corrupted names that merely
    ///     contain the letters: <c>Dominant7 -> Domant7</c>,
    ///     <c>Diminished -> Dimished</c>. And because
    ///     <c>RefretService.FindOrCreateTemplate</c> matches on the NAME, a
    ///     project holding both freshly-imported <c>Amin</c> and already-saved
    ///     <c>Am</c> templates would mint duplicates for one shape.
    ///     </para>
    ///     <para>
    ///     <c>"-nop"</c> is LOAD-BEARING: it is RS <c>ChordMask.Nop</c>. A plain
    ///     strip once deleted the only signal export had, so it is FOLDED into
    /// <see cref="ChordTemplate.NopInRs" /> instead, which every
    ///     exporter reads and spells back. Unlike <c>-arp</c> it may leave the
    ///     name empty: Rocksmith's own Nop double stops are unnamed.
    ///     </para>
    ///     <para>
    ///     <c>"-arp"</c> and <c>"CONV"</c> stay, but SUFFIX-anchored — they are
    ///     display conventions, not name text, and an unanchored replace would eat
    ///     them out of the middle of an arbitrary charter-typed name. <c>-arp</c>
    ///     additionally carries meaning, so stripping it sets
    ///     <see cref="ChordTemplate.ForceArpeggioInRs" /> rather than silently
    ///     downgrading an arpeggio to a plain chord — the same read
    ///     <c>RsXmlSongReader</c> does, and <c>RsXmlSongWriter</c> re-appends the
    ///     suffix from that flag on export.
    ///     </para>
    ///     Loops because a name can carry both (<c>"Cmaj-arpCONV"</c>), and never
    ///     strips a name down to nothing.
    /// </summary>
    private static void StripRsDisplaySuffixes(ChordTemplate template)
    {
        var name = template.Name;
        bool stripped;
        do
        {
            stripped = false;

            if (name.EndsWith(RsChordDisplaySuffix.Nop, StringComparison.Ordinal))
            {
                name = name[..^RsChordDisplaySuffix.Nop.Length];
                template.NopInRs = true;
                stripped = true;
            }

            if (name.Length > ArpeggioSuffix.Length && name.EndsWith(ArpeggioSuffix, StringComparison.Ordinal))
            {
                name = name[..^ArpeggioSuffix.Length];
                template.ForceArpeggioInRs = true;
                stripped = true;
            }

            if (name.Length > ConvertedMarker.Length && name.EndsWith(ConvertedMarker, StringComparison.Ordinal))
            {
                name = name[..^ConvertedMarker.Length];
                stripped = true;
            }
        } while (stripped);

        template.Name = name;
    }

    private static void EnforceRocksmithCompatibility(ChartLevel level, Arrangement arrangement,
        IList<BeatMarker> beats)
    {
        RemoveOverlappingBendValues(level);
        RemoveMutedNotesFromPartiallyMutedChords(level, arrangement);
        LinkNextRepair.FixLinkNexts(level);
        MoveAnchorsNearSounds(level, beats);
    }

    /// <summary>
    ///     Marks the sounds Rocksmith cannot SCORE: fret 23+ is off the end of its
    ///     neck, and a 7th-fret harmonic that RINGS is unplayable there — both are
    ///     written <c>ignore</c> and left unjudged in game.
    ///     <para>
    ///     This is a DERIVATION, not an edit: the flag is a pure function of the
    ///     sound's current fret / harmonic / sustain (and, for a chord, of its
    ///     template's frets). It writes both ways; writing only <c>true</c> would make it
    ///     a one-way ratchet — a note authored at fret 24 and later dragged to fret
    ///     12 would stay <c>ignore="1"</c> forever, unscored in game and invisible in
    ///     the editor unless the inspector happened to be open on it. The pass runs
    ///     on every save AND every load, so the ratchet would be monotone over the life
    ///     of the file.
    ///     </para>
    ///     <para>
    ///     <c>Ignore</c> is ALSO user-authorable (the inspector chip and the highway
    ///     toggle command), so the derivation only ever retracts a flag it set
    ///     itself: <c>IgnoreDerived</c> on the note/chord records that, and a
    ///     hand-ignored ordinary note keeps its flag across saves — see
    ///     <see cref="ApplyDerivedIgnore(GuitarNote, bool)" />.
    ///     </para>
    ///     <para>
    ///     Runs AFTER <see cref="EnforceSoundSpacing" /> / <see cref="LinkNextRepair.FixLinkNexts" />,
    ///     not before them like the other compatibility passes.
    ///     <see cref="RequiresIgnore" /> reads <c>Sustain</c>, and both of those
    ///     passes can zero the very sustain that made a 7th-fret harmonic
    ///     ignore-worthy — deriving first produced a flag that disagreed with the
    ///     chart the same enforce had just written, and the next enforce (i.e. the
    ///     next save) would then quietly change it. Deriving last is what makes the
    ///     whole pass idempotent. It also means a partially-muted chord is read
    ///     through the REDUCED template <see cref="RemoveMutedNotesFromPartiallyMutedChords" />
    ///     repointed it at, i.e. the shape it actually plays.
    ///     </para>
    /// </summary>
    private static void DeriveIgnoreFlags(ChartLevel level, Arrangement arrangement)
    {
        for (var i = 0; i < level.Notes.Count; i++)
        {
            var note = level.Notes[i];
            ApplyDerivedIgnore(note, RequiresIgnore(note.Fret, note.Harmonic, note.Sustain));
        }

        if (level.Chords.Count == 0) return;

        // One id→template map for the whole level instead of a FirstOrDefault
        // over ChordTemplates PER CHORD: that was O(chords × templates) with a
        // captured closure, a delegate and a boxed enumerator allocated every
        // time round. First-wins, because FirstOrDefault returns the first
        // match. Safe to build here because this pass never adds a template —
        // RemoveMutedNotesFromPartiallyMutedChords, which does, ran earlier and
        // keeps its own (rare, per-partially-muted-chord) linear lookup.
        Dictionary<int, ChordTemplate>? templatesById = null;

        for (var i = 0; i < level.Chords.Count; i++)
        {
            var chord = level.Chords[i];

            var required = false;
            for (var n = 0; n < chord.ChordNotes.Count && !required; n++)
            {
                var chordNote = chord.ChordNotes[n];
                required = RequiresIgnore(chordNote.Fret, chordNote.Harmonic, chordNote.Sustain);
            }

            if (!required)
            {
                if (templatesById == null)
                {
                    templatesById = new Dictionary<int, ChordTemplate>(arrangement.ChordTemplates.Count);
                    foreach (var template in arrangement.ChordTemplates)
                        templatesById.TryAdd(template.Id, template);
                }

                if (templatesById.TryGetValue(chord.TemplateId, out var resolved))
                {
                    foreach (var fret in resolved.Frets.Values)
                    {
                        if (fret >= 23)
                        {
                            required = true;
                            break;
                        }
                    }
                }
            }

            ApplyDerivedIgnore(chord, required);
        }
    }

    /// <summary>
    ///     Writes a derived <c>Ignore</c> without trampling a user-authored one.
    ///     <para>
    ///         The flag has to be two-way — a note dragged off fret 24 kept an
    ///         invisible <c>Ignore</c> and stayed unscored forever — but it is
    ///         ALSO user-authorable (the inspector chip, the highway toggle), so
    ///         an unconditional re-derivation on every save silently discarded
    ///         that intent. <c>IgnoreDerived</c> is the discriminator: we set
    ///         both when the structural reason holds, clear both when a reason
    ///         WE recorded has gone, and never touch a flag the user set.
    ///     </para>
    ///     <para>
    ///         Overloaded on the concrete type rather than taking four accessor
    ///         delegates. The delegate form cost a closure display-class plus
    ///         FOUR delegate objects per note and per chord — ~22,000
    ///         allocations on one pass over a dense level, for a body that is
    ///         two assignments. <c>GuitarNote</c> and <c>Chord</c> share no
    ///         interface carrying <c>Ignore</c>/<c>IgnoreDerived</c>, so the
    ///         body is stated twice; keep the two IDENTICAL.
    ///     </para>
    /// </summary>
    private static void ApplyDerivedIgnore(GuitarNote sound, bool required)
    {
        if (required)
        {
            if (!sound.Ignore) sound.Ignore = true;
            sound.IgnoreDerived = true;
            return;
        }

        // Only retract what this pass is responsible for.
        if (!sound.IgnoreDerived) return;
        sound.Ignore = false;
        sound.IgnoreDerived = false;
    }

    /// <inheritdoc cref="ApplyDerivedIgnore(GuitarNote, bool)" />
    private static void ApplyDerivedIgnore(Chord sound, bool required)
    {
        if (required)
        {
            if (!sound.Ignore) sound.Ignore = true;
            sound.IgnoreDerived = true;
            return;
        }

        if (!sound.IgnoreDerived) return;
        sound.Ignore = false;
        sound.IgnoreDerived = false;
    }

    /// <summary>The derivation's rule — also read by <c>RsXmlSplitChordMerge</c>, which
    /// may only merge notes whose derived ignore a chord would carry the same way.</summary>
    internal static bool RequiresIgnore(int fret, HarmonicType harmonic, FractionalPosition sustain) => fret >= 23 || (fret == 7 && sustain > FractionalPosition.Zero && harmonic != HarmonicType.None);

    private static void RemoveOverlappingBendValues(ChartLevel level)
    {
        foreach (var note in level.Notes)
            RemoveDuplicateBendValues(note.BendValues);

        foreach (var chordNote in level.Chords.SelectMany(c => c.ChordNotes))
            RemoveDuplicateBendValues(chordNote.BendValues);
    }

    internal static void RemoveDuplicateBendValues(IList<BendValue> bendValues)
    {
        if (bendValues.Count <= 1) return;

        var distinct = bendValues
            .GroupBy(b => b.Position)
            .Select(g => g.First())
            .OrderBy(b => b.Position)
            .ToList();

        if (distinct.Count == bendValues.Count &&
            bendValues.Zip(distinct).All(pair => pair.First.Position == pair.Second.Position))
        {
            return;
        }

        bendValues.Clear();
        foreach (var bendValue in distinct)
            bendValues.Add(bendValue);
    }

    /// <summary>
    ///     Rocksmith compatibility: a chord that mixes sounding and fret-hand-muted
    ///     (<see cref="MuteType.Full" />) notes drops the muted ones, and its chord
    ///     template must describe the REDUCED shape that is left.
    ///     <para>
    ///     Chord templates are SHARED — <see cref="ChordTemplate" />s are
    ///     find-or-create/deduped by shape, so one instance backs every chord AND
    ///     hand shape with that shape. Editing the resolved template in place
    ///     would therefore silently delete strings
    ///     from every OTHER chord and hand shape using it, and the enforcer runs on
    ///     every save — so one partially-muted chord would progressively eat the shared
    ///     shape. Clone-and-reassign instead: resolve (or create) a template that
    ///     already carries the reduced shape and point only THIS chord at it. The
    ///     original is never touched. The chord's hand shape deliberately keeps its
    ///     own (full-shape) template — the RS hand-shape bar describes the fingering
    ///     region, not the individual strum.
    ///     </para>
    ///     Idempotent: on the next pass the muted notes are already gone, so the
    ///     chord is skipped and no further template is minted.
    /// </summary>
    private static void RemoveMutedNotesFromPartiallyMutedChords(ChartLevel level, Arrangement arrangement)
    {
        foreach (var chord in level.Chords)
        {
            // COUNT before materializing: the overwhelming majority of chords
            // have no fully-muted note at all, and the Where().ToList() form
            // allocated an iterator, a List and a boxed enumerator for every one
            // of them only for the next line to throw the list away.
            var mutedCount = 0;
            for (var n = 0; n < chord.ChordNotes.Count; n++)
            {
                if (chord.ChordNotes[n].Mute == MuteType.Full)
                    mutedCount++;
            }

            if (mutedCount == 0 || mutedCount == chord.ChordNotes.Count) continue;

            var mutedNotes = chord.ChordNotes.Where(n => n.Mute == MuteType.Full).ToList();

            foreach (var mutedNote in mutedNotes)
                chord.ChordNotes.Remove(mutedNote);

            var template = FindTemplate(arrangement, chord.TemplateId);
            if (template == null) continue;

            var reduced = FindOrCreateReducedTemplate(arrangement, template,
                mutedNotes.Select(n => n.StringIndex));
            chord.TemplateId = reduced.Id;
        }
    }

    /// <summary>
    ///     Returns a template equal to <paramref name="source" /> minus
    ///     <paramref name="removedStrings" />, reusing an existing arrangement
    ///     template with that exact shape + fingering when one is present and
    ///     otherwise adding a fresh clone. <paramref name="source" /> is never
    ///     mutated. Returns <paramref name="source" /> itself when the removal is
    ///     a no-op (it already lacks those strings), so an unchanged shape does
    ///     not mint a duplicate.
    /// </summary>
    private static ChordTemplate FindOrCreateReducedTemplate(Arrangement arrangement, ChordTemplate source,
        IEnumerable<int> removedStrings)
    {
        var reduced = source.Clone();
        var changed = false;
        foreach (var stringIndex in removedStrings)
        {
            changed |= reduced.Frets.Remove(stringIndex);
            changed |= reduced.Fingers.Remove(stringIndex);
        }

        if (!changed) return source;

        // BOTH arpeggio flags, not just `Arpeggio`: an RS-XML / EOF import
        // spells an arpeggio `ForceArpeggioInRs`, so comparing one flag let this
        // pass repoint a partially-muted ARPEGGIO chord at a plain template of
        // the same reduced shape — on every save, silently dropping the `-arp`
        // export name.
        var flavour = ArpeggioFlavour.Of(source);
        foreach (var candidate in arrangement.ChordTemplates)
        {
            if (flavour.Matches(candidate) && candidate.HasSameShapeAs(reduced))
                return candidate;
        }

        reduced.Id = arrangement.ChordTemplates.Count == 0
            ? 0
            : arrangement.ChordTemplates.Max(t => t.Id) + 1;
        arrangement.ChordTemplates.Add(reduced);
        return reduced;
    }

    private static void MoveAnchorsNearSounds(ChartLevel level, IList<BeatMarker> beats)
    {
        if (beats.Count == 0 || level.Anchors.Count == 0) return;

        var seenPositions = new HashSet<FractionalPosition>(level.Notes.Count + level.Chords.Count);
        var soundTimes = new List<SoundTime>(level.Notes.Count + level.Chords.Count);
        foreach (var note in level.Notes)
        {
            if (seenPositions.Add(note.Position))
                soundTimes.Add(new SoundTime(note.Position, note.Position.GetPositionMs(beats)));
        }

        foreach (var chord in level.Chords)
        {
            if (seenPositions.Add(chord.Position))
                soundTimes.Add(new SoundTime(chord.Position, chord.Position.GetPositionMs(beats)));
        }

        if (soundTimes.Count == 0) return;
        // Stable sort (OrderBy) so equal-time sounds keep enumeration order —
        // tie resolution below depends on it, matching the old behaviour.
        soundTimes = soundTimes.OrderBy(static s => s.TimeMs).ToList();

        var slideEndTimes = BuildSlideEndTimeSet(level, beats);

        foreach (var anchor in level.Anchors)
        {
            var anchorTimeMs = anchor.Position.GetPositionMs(beats);
            if (slideEndTimes.Contains(Math.Round(anchorTimeMs, 3))) continue;

            // Binary-search the sorted sound list instead of a full LINQ scan
            // per anchor — the previous O(anchors × sounds) pass with per-sound
            // allocations dominated the enforce cost on dense charts (and the
            // enforcer runs on the UI thread during every save / auto-backup).
            var nearestIndex = FindNearestSoundIndex(soundTimes, anchorTimeMs);
            if (nearestIndex >= 0)
                anchor.Position = soundTimes[nearestIndex].Position;
        }

        RemoveAnchorsCollapsedOntoOnePosition(level.Anchors);
    }

    /// <summary>
    ///     Drops anchors that now SHARE a position, keeping the LAST of each
    ///     collapsed run.
    ///     <para>
    ///         The snap above moves every anchor within
    ///         <see cref="AnchorSnapToleranceMs" /> of a sound ONTO that sound, so
    ///         two anchors a few ms apart around one note both land on it.
    ///         <see cref="RemoveRedundantAnchors" /> does not clean that up: it only
    ///         removes a consecutive pair with the SAME fret and width, so two
    ///         anchors with different frets survived at one
    ///         <see cref="FractionalPosition" /> — a chart state nothing downstream
    ///         is defined for (the SNG mapper's anchor/note pairing and the 3D
    ///         camera both mishandle it, and <see cref="RemoveRedundantAnchors" />'s
    ///         consecutive comparison then deletes the wrong neighbour).
    ///     </para>
    ///     <para>
    ///         The LAST wins because every anchor consumer resolves "the anchor in
    ///         effect at position p" as the last one at or before p, so keeping the
    ///         later anchor preserves the fret hand position from the snap point
    ///         onward. The one it replaces governed a span shorter than the 5 ms
    ///         snap tolerance.
    ///     </para>
    /// </summary>
    private static void RemoveAnchorsCollapsedOntoOnePosition(IList<FretAnchor> anchors)
    {
        if (anchors.Count <= 1) return;

        var seen = new HashSet<FractionalPosition>(anchors.Count);
        for (var i = anchors.Count - 1; i >= 0; i--)
        {
            if (!seen.Add(anchors[i].Position))
                anchors.RemoveAt(i);
        }
    }

    /// <summary>
    ///     Returns the index of the sound nearest to <paramref name="timeMs"/> with
    ///     0 &lt; distance &lt;= <see cref="AnchorSnapToleranceMs"/>, or -1. Ties between
    ///     an earlier and a later sound resolve to the earlier one, matching the
    ///     stable ordering of the previous LINQ implementation.
    /// </summary>
    private static int FindNearestSoundIndex(List<SoundTime> sortedSounds, double timeMs)
    {
        var lo = 0;
        var hi = sortedSounds.Count;
        while (lo < hi)
        {
            var mid = lo + ((hi - lo) >> 1);
            if (sortedSounds[mid].TimeMs < timeMs) lo = mid + 1;
            else hi = mid;
        }

        // lo = first index with TimeMs >= timeMs. Candidates: the run of equal
        // times below, and the first strictly-greater / equal time at lo.
        var bestIndex = -1;
        var bestDistance = double.MaxValue;

        // Below candidate: last sound strictly earlier than timeMs. Skip any
        // exact matches (distance 0 is excluded by the original predicate).
        var below = lo - 1;
        if (below >= 0)
        {
            var distance = timeMs - sortedSounds[below].TimeMs;
            if (distance is > 0 and <= AnchorSnapToleranceMs)
            {
                // Walk to the first sound of an equal-time run so ties keep the
                // original stable-sort winner.
                while (below > 0 && sortedSounds[below - 1].TimeMs == sortedSounds[below].TimeMs)
                    below--;
                bestIndex = below;
                bestDistance = distance;
            }
        }

        // Above candidate: first sound at/after timeMs; skip exact matches.
        var above = lo;
        while (above < sortedSounds.Count && sortedSounds[above].TimeMs == timeMs)
            above++;
        if (above < sortedSounds.Count)
        {
            var distance = sortedSounds[above].TimeMs - timeMs;
            if (distance > 0 && distance <= AnchorSnapToleranceMs && distance < bestDistance)
                bestIndex = above;
        }

        return bestIndex;
    }

    private static HashSet<double> BuildSlideEndTimeSet(ChartLevel level, IList<BeatMarker> beats)
    {
        var slideEndTimes = new HashSet<double>();

        foreach (var note in level.Notes.Where(n => n.SlideTo.HasValue))
            slideEndTimes.Add(Math.Round(note.EndPosition.GetPositionMs(beats), 3));

        foreach (var chord in level.Chords)
        {
            foreach (var chordNote in chord.ChordNotes.Where(n => n.SlideTo.HasValue))
                slideEndTimes.Add(Math.Round((chord.Position + chordNote.Sustain).GetPositionMs(beats), 3));
        }

        return slideEndTimes;
    }

    /// <summary>
    ///     Drops an anchor that merely restates the one before it — unless it sits
    ///     on a phrase or section boundary, where Rocksmith REQUIRES one
    ///     (inspector rule E06).
    ///     <para>
    ///         <b>BOTH phrase stores.</b> Protecting only <c>EventPoint</c>
    ///         positions protected NOTHING on an imported chart (0 markers via
    ///         <c>EventPoint</c> against 2,596 via <c>PhraseIterations</c> over the
    ///         89 hand-finished RS2 exports in <c>TestData/EOF</c>) — and since a
    ///         phrase-start anchor restates the position in force by construction,
    ///         it is always "identical", so this pass DELETED every anchor
    ///         <c>ArrangementAutoFixService.AddMissingAnchorsAtPhraseStarts</c> and
    ///         <c>ChartImprovementService.EnsurePhraseStartAnchors</c> had just
    ///         added, on the very next save. Two fixers and their undoer, all
    ///         reading one store.
    ///     </para>
    /// </summary>
    private static void RemoveRedundantAnchors(ChartLevel level, Arrangement arrangement)
    {
        if (level.Anchors.Count <= 1) return;

        var protectedPositions = PhraseMarkers.All(arrangement)
            .Select(m => m.Position)
            .Concat(arrangement.EventPoints
                .Where(e => !string.IsNullOrWhiteSpace(e.Section))
                .Select(e => e.Position))
            .ToHashSet();

        var anchorsToRemove = new List<FretAnchor>();
        var previousAnchor = level.Anchors[0];

        for (var i = 1; i < level.Anchors.Count; i++)
        {
            var anchor = level.Anchors[i];
            var isIdentical = anchor.Fret == previousAnchor.Fret && anchor.Width == previousAnchor.Width;

            if (isIdentical && !protectedPositions.Contains(anchor.Position))
            {
                anchorsToRemove.Add(anchor);
                continue;
            }

            previousAnchor = anchor;
        }

        foreach (var anchor in anchorsToRemove)
            level.Anchors.Remove(anchor);
    }

    /// <summary>
    ///     Resolves a chord's template by ID, or null.
    ///     <para>
    ///     There is deliberately NO fallback to <c>ChordTemplates[templateId]</c>.
    ///     The ID lookup runs first, so such a fallback only ever fired on an
    ///     ALREADY dangling id — and it answered with a completely unrelated
    ///     template picked by list POSITION.
    ///     <see cref="RemoveMutedNotesFromPartiallyMutedChords" /> would then clone
    ///     that stranger, strip the muted strings from it and permanently repoint
    ///     the chord at a reduction of a shape it never played, straight into the
    ///     .rscp; <see cref="DeriveIgnoreFlags" /> would flag a chord from another
    ///     chord's frets. It is the same list-index-as-id confusion the inspector
    ///     avoids.
    ///     </para>
    ///     Latent rather than live today (every template creator keeps ids dense
    ///     and append-only, and nothing removes templates), which is exactly why it
    ///     is worth deleting before something makes ids sparse. Both callers
    ///     null-check, and the documented untemplated id is <c>-1</c>, so null is
    ///     the honest answer. Compare <c>RefretPlanner.ResolveTemplate</c>, which
    ///     uses the index ONLY after confirming <c>list[templateId].Id ==
    ///     templateId</c>.
    /// </summary>
    private static ChordTemplate? FindTemplate(Arrangement arrangement, int templateId)
    {
        // Plain loop, not FirstOrDefault: the predicate captures templateId, so
        // the LINQ form allocated a display class, a delegate and a boxed
        // ObservableCollection enumerator on every call. Same first-match answer.
        var templates = arrangement.ChordTemplates;
        for (var i = 0; i < templates.Count; i++)
        {
            if (templates[i].Id == templateId)
                return templates[i];
        }

        return null;
    }

    /// <summary>
    ///     Re-sorts a chart collection in place, writing ONLY the slots that
    ///     actually move.
    ///     <para>
    ///         These are live <see cref="System.Collections.ObjectModel.ObservableCollection{T}" />s
    ///         bound to every view and read by the 2D/3D snapshot builders, and the
    ///         indexer raises one <c>CollectionChanged</c> (Replace) per write. The
    ///         old form assigned every slot unconditionally, so
    ///         <see cref="EnforceArrangement" /> fired ~4 × N notifications per level
    ///         on the UI thread during every save and auto-backup — about 20,000 on a
    ///         5,000-note level — even though the collections are ALREADY sorted in
    ///         the overwhelming majority of cases (adds go through
    ///         <c>InsertSorted</c>, mutations through <c>SortCollection</c>, importers
    ///         emit sorted). The sorted-check short-circuit makes that case free, and
    ///         the reference comparison bounds the rest by the number of genuinely
    ///         displaced elements.
    ///     </para>
    ///     <para>
    ///         Deliberately NOT <c>BulkObservableCollection.ResetWith</c>: the four
    ///         collections this is called on (<c>ChartLevel.Notes/Chords/Anchors/
    ///         HandShapes</c>) are plain <c>ObservableCollection</c>s, so that branch
    ///         would be unreachable, and a single <c>Reset</c> is also strictly worse
    ///         for the common no-op case than raising nothing at all.
    ///     </para>
    /// </summary>
    private static void SortByPosition<T>(IList<T> items, Func<T, FractionalPosition> getPosition)
        where T : class
    {
        if (items.Count <= 1) return;

        // Carry the previous position forward instead of re-reading it: the
        // check runs over every note and chord of every level on every save, and
        // the delegate does not inline, so evaluating it twice per element was
        // literally double the cost of the common (already sorted) case.
        var alreadySorted = true;
        var previous = getPosition(items[0]);
        for (var i = 1; i < items.Count; i++)
        {
            var current = getPosition(items[i]);
            if (previous.CompareTo(current) > 0)
            {
                alreadySorted = false;
                break;
            }

            previous = current;
        }

        if (alreadySorted) return;

        var sorted = items.OrderBy(getPosition).ToList();
        for (var i = 0; i < sorted.Count; i++)
        {
            if (!ReferenceEquals(items[i], sorted[i]))
                items[i] = sorted[i];
        }
    }

    /// <summary>
    ///     Sustain-clamping pass. Runs after EVERY committed highway edit (the
    ///     link-next updater) on the whole active level, so it must not churn
    ///     the GC: the historical shape (SoundSpan objects + LINQ sorts +
    ///     three dictionaries per call) allocated ~1 MB per keystroke on a
    ///     dense chart — a measured top contributor to the "GC kicks in way
    ///     too often while editing" report (v0.32.5). This form is a pooled
    ///     struct array + one sort + a single backward sweep that derives the
    ///     same "next distinct position" / "next distinct same-string
    ///     position" values a dictionary would hold. Semantics are
    ///     pinned by ChartSpacingEnforcerTests.
    /// </summary>
    /// <summary>
    ///     Owns the ONE pooled sound array both spacing passes read. The sound
    ///     pass fills and sorts it; the hand-shape pass then binary-searches the
    ///     very same onsets instead of collecting and sorting them again.
    /// </summary>
    private static void EnforceSpacing(ChartLevel level, IList<BeatMarker> beats, AppConfig config)
    {
        var notes = level.Notes;
        var chords = level.Chords;

        var total = notes.Count;
        for (var i = 0; i < chords.Count; i++) total += chords[i].ChordNotes.Count;
        if (total == 0)
        {
            // Nothing to space, but a level can still hold hand shapes and
            // chords with no chord notes at all.
            EnforceHandShapeSpacing(level, [], beats, config);
            return;
        }

        var pool = System.Buffers.ArrayPool<SoundEntry>.Shared;
        var entries = pool.Rent(total);
        try
        {
            EnforceSoundSpacing(entries, total, notes, chords, beats, config);

            // The hand-shape pass needs the same onsets, already sorted — so it
            // reads THIS array instead of building and sorting a second one. The
            // pass above only ever writes sustains and bend curves; no position
            // moved, so the ordering still holds.
            EnforceHandShapeSpacing(level, entries.AsSpan(0, total), beats, config);
        }
        finally
        {
            pool.Return(entries, clearArray: true);
        }
    }

    private static void EnforceSoundSpacing(SoundEntry[] entries, int total,
        IList<GuitarNote> notes, IList<Chord> chords, IList<BeatMarker> beats, AppConfig config)
    {
        {
            var n = 0;
            var minString = int.MaxValue;
            var maxString = int.MinValue;
            // Indexed, not foreach: these are IList<T>-typed ObservableCollections,
            // whose enumerator is only reachable through IEnumerable<T> and is
            // therefore boxed — and every MoveNext/Current on it is an interface
            // dispatch, 20,000 times per committed edit. Same rule the frame
            // snapshot's walks carry.
            for (var i = 0; i < notes.Count; i++)
            {
                var note = notes[i];
                entries[n++] = new SoundEntry(note.StringIndex, note.Position, note, note.PassOtherNotes);
                if (note.StringIndex < minString) minString = note.StringIndex;
                if (note.StringIndex > maxString) maxString = note.StringIndex;
            }

            for (var c = 0; c < chords.Count; c++)
            {
                var chord = chords[c];
                var chordNotes = chord.ChordNotes;
                for (var j = 0; j < chordNotes.Count; j++)
                {
                    var chordNote = chordNotes[j];
                    entries[n++] = new SoundEntry(chordNote.StringIndex, chord.Position, chordNote,
                        chord.PassOtherNotes);
                    if (chordNote.StringIndex < minString) minString = chordNote.StringIndex;
                    if (chordNote.StringIndex > maxString) maxString = chordNote.StringIndex;
                }
            }

            Array.Sort(entries, 0, total, SoundEntryComparer.Instance);

            // Backward sweep. For each entry the pass needs (a) the next
            // DISTINCT position over all sounds and (b) the next DISTINCT
            // position on the same string. Both are derivable walking from
            // the end with one (position, nextDistinct) slot globally and one
            // per string: when the stored position equals the current one we
            // are inside an equal-position run and inherit its nextDistinct.
            var stringSlots = maxString - minString + 1;
            var stringPool = System.Buffers.ArrayPool<PositionSlot>.Shared;
            var perString = stringPool.Rent(stringSlots);
            try
            {
                for (var i = 0; i < stringSlots; i++) perString[i] = default;
                var global = default(PositionSlot);

                // Non-null only while an undoable action is recording what this
                // pass changes on sounds it does not own — see SpacingCollateral.
                var recorder = SpacingCollateralRecorder.Current;
                var minDistance = new MinDistanceMemo(beats, config);

                for (var i = total - 1; i >= 0; i--)
                {
                    ref var entry = ref entries[i];
                    var position = entry.Position;

                    var stringSlot = entry.StringIndex - minString;
                    var nextSameString = Advance(ref perString[stringSlot], position,
                        out var hasNextSameString);
                    var nextPosition = Advance(ref global, position, out var hasNextPosition);

                    if (entry.Sound.LinkNext)
                    {
                        var linkSustain = hasNextSameString
                            ? FractionalPosition.Max(FractionalPosition.Zero, nextSameString - position)
                            : FractionalPosition.Zero;
                        if (entry.Sound.Sustain != linkSustain) recorder?.CaptureSound(entry.Sound);
                        entry.Sound.Sustain = linkSustain;
                    }
                    // `maxSustain` is Max(Zero, …), so it is never negative and a
                    // sound with no sustain can never exceed it. Testing that
                    // FIRST is free and skips the whole computation — two
                    // FractionalPosition subtractions plus GetMinDistance, each
                    // of which is several Fraction constructions and therefore
                    // several GCD loops — for the majority of sounds on most
                    // charts. Identical output by construction, not by measurement.
                    else if (!entry.PassOtherNotes && hasNextPosition &&
                             entry.Sound.Sustain > FractionalPosition.Zero)
                    {
                        var maxSustain = nextPosition - position - minDistance.For(position);
                        maxSustain = FractionalPosition.Max(FractionalPosition.Zero, maxSustain);

                        if (entry.Sound.Sustain > maxSustain)
                        {
                            recorder?.CaptureSound(entry.Sound);
                            entry.Sound.Sustain = maxSustain;
                        }
                    }

                    // The clamp+dedupe below rewrite the curve in place, so the
                    // pre-clamp curve has to be captured before them. Bent sounds
                    // are a small minority, so this stays cheap.
                    if (recorder != null && entry.Sound.BendValues.Count > 0)
                        recorder.CaptureSound(entry.Sound);

                    // A bend point can never lie outside the (possibly just
                    // shortened) sustain. Shortening
                    // sustains but leaving the bends where they are would render a bend
                    // authored near the end of a long sustain PAST the
                    // shortened note. Clamp
                    // UNCONDITIONALLY — the invariant must hold for every sound
                    // after the pass, whichever branch (or none) set the sustain.
                    // The clamp COLLAPSES, so it can mint duplicate positions —
                    // re-dedupe right here, never later (see the helper).
                    BendGeometry.ClampBendsToSustain(entry.Sound, position);
                    RemoveBendPointsCollapsedOntoOnePosition(entry.Sound.BendValues);
                }
            }
            finally
            {
                stringPool.Return(perString);
            }
        }
    }

    /// <summary>
    ///     Drops bend points that now SHARE a position, keeping the first of each
    ///     collapsed run.
    ///     <para>
    ///     Runs immediately after <see cref="BendGeometry.ClampBendsToSustain" />
    ///     because that clamp COLLAPSES rather than scales: every point past the
    ///     (just-shortened) end becomes exactly <c>position + Sustain</c>. A
    ///     rise/hold/release curve whose last two points fall outside the new
    ///     sustain therefore lands two <see cref="BendValue" />s on ONE position —
    ///     precisely the RS-invalid state <see cref="RemoveOverlappingBendValues" />
    ///     had removed three passes earlier, now re-created behind its back and
    ///     written verbatim by every writer (rscp, RS XML, SNG). Because the
    ///     enforcer runs on every save, the duplicates accumulated per save.
    ///     </para>
    ///     <para>
    ///     The dedupe cannot simply move BEFORE the clamp: sustains are only final
    ///     after <see cref="EnforceSoundSpacing" />, so the clamp is what mints the
    ///     duplicates and the dedupe has to follow it. Keeping the FIRST of a
    ///     collapsed run is what keeps the curve meaningful — it is the value the
    ///     bend was travelling toward when the note was cut short, whereas keeping
    ///     the last would stamp the now-unreachable tail and a released bend would
    ///     read as never bent at all. That also matches
    ///     <see cref="RemoveDuplicateBendValues" />'s <c>g.First()</c>, so the two
    ///     dedupes can't disagree about which point survives.
    ///     </para>
    ///     Hand-rolled instead of calling <see cref="RemoveDuplicateBendValues" />
    ///     because this sits inside the pooled, allocation-free sweep that runs on
    ///     every committed highway edit; bend lists are a handful of points, so the
    ///     pairwise scan costs nothing where the LINQ group-by/order-by would
    ///     allocate per bent sound per keystroke.
    /// </summary>
    private static void RemoveBendPointsCollapsedOntoOnePosition(IList<BendValue> bendValues)
    {
        if (bendValues.Count <= 1) return;

        for (var i = bendValues.Count - 1; i > 0; i--)
        {
            for (var j = 0; j < i; j++)
            {
                if (bendValues[j].Position.CompareTo(bendValues[i].Position) == 0)
                {
                    bendValues.RemoveAt(i);
                    break;
                }
            }
        }
    }

    /// <summary>Resolves "next distinct position after <paramref name="position" />"
    /// from the backward-sweep slot, then records this position in it.</summary>
    private static FractionalPosition Advance(ref PositionSlot slot, FractionalPosition position, out bool hasNext)
    {
        FractionalPosition next = default;
        if (!slot.HasPosition)
        {
            hasNext = false;
        }
        else if (slot.Position.CompareTo(position) == 0)
        {
            hasNext = slot.HasNext;
            next = slot.Next;
        }
        else
        {
            hasNext = true;
            next = slot.Position;
        }

        if (!slot.HasPosition || slot.Position.CompareTo(position) != 0)
        {
            slot.Next = next;
            slot.HasNext = hasNext;
            slot.Position = position;
            slot.HasPosition = true;
        }

        return next;
    }

    private struct PositionSlot
    {
        public FractionalPosition Position;
        public FractionalPosition Next;
        public bool HasPosition;
        public bool HasNext;
    }

    private readonly struct SoundEntry(
        int stringIndex,
        FractionalPosition position,
        IGuitarSound sound,
        bool passOtherNotes)
    {
        public readonly int StringIndex = stringIndex;
        public readonly FractionalPosition Position = position;
        public readonly IGuitarSound Sound = sound;
        public readonly bool PassOtherNotes = passOtherNotes;
    }

    private sealed class SoundEntryComparer : IComparer<SoundEntry>
    {
        public static readonly SoundEntryComparer Instance = new();

        public int Compare(SoundEntry x, SoundEntry y)
        {
            var byPosition = x.Position.CompareTo(y.Position);
            return byPosition != 0 ? byPosition : x.StringIndex.CompareTo(y.StringIndex);
        }
    }

    /// <summary>
    ///     A hand shape must leave the min-note-distance gap before the next
    ///     PLAYED sound too, not only before the next hand shape (owner report:
    ///     with min distance set, the note sustains around a shape shortened but
    ///     the shape itself ran right up to / over the following note). Same rule
    ///     the note-sustain pass applies — a shape "held until the next sound"
    ///     now stops a gap short of it.
    ///     <para>
    ///         <paramref name="sortedSounds" /> is <see cref="EnforceSpacing" />'s
    ///         POOLED, already-sorted array. It must not build a
    ///         <c>List&lt;FractionalPosition&gt;</c> of the same onsets and sort
    ///         them a second time, right beside the pass that deliberately
    ///         uses <c>ArrayPool</c> because "the historical shape
    ///         allocated ~1 MB per keystroke on a dense chart".
    ///         <see cref="FractionalPosition" /> is 12 bytes, so that list's
    ///         backing array would cross the 85,000-byte LOH threshold at 7,084
    ///         sounds — 120 KB on the LOH per committed edit on a 10,000-sound
    ///         chart, and the enforcer runs after EVERY one.
    ///     </para>
    /// </summary>
    private static void EnforceHandShapeSpacing(ChartLevel level, ReadOnlySpan<SoundEntry> sortedSounds,
        IList<BeatMarker> beats, AppConfig config)
    {
        var handShapes = level.HandShapes;
        if (handShapes.Count == 0) return;

        // A chord with NO chord notes contributes no SoundEntry, but it DID
        // contribute an onset to the list this span replaces. It is a degenerate
        // shape (a strum that plays no string) and a chart holding one is rare,
        // so it is carried on the side rather than paid for by everything else.
        List<FractionalPosition>? soundlessChords = null;
        foreach (var chord in level.Chords)
        {
            if (chord.ChordNotes.Count == 0)
                (soundlessChords ??= []).Add(chord.Position);
        }

        // An index permutation instead of `handShapes.OrderBy(...).ToList()`.
        // The comparer breaks ties on the index, which makes an otherwise
        // unstable Array.Sort stable — and stability is load-bearing here: with
        // two shapes at one position it decides which of them is treated as
        // "last" and so escapes the next-shape clamp entirely.
        var orderPool = System.Buffers.ArrayPool<int>.Shared;
        var order = orderPool.Rent(handShapes.Count);
        try
        {
            for (var i = 0; i < handShapes.Count; i++) order[i] = i;
            Array.Sort(order, 0, handShapes.Count, new HandShapePositionOrder(handShapes));

            EnforceHandShapeSpacingCore(handShapes, order, sortedSounds, soundlessChords, beats, config);
        }
        finally
        {
            orderPool.Return(order);
        }
    }

    private static void EnforceHandShapeSpacingCore(
        IList<HandShape> handShapes, int[] order, ReadOnlySpan<SoundEntry> sortedSounds,
        List<FractionalPosition>? soundlessChords, IList<BeatMarker> beats, AppConfig config)
    {
        var shapeDistance = new MinDistanceMemo(beats, config);
        for (var i = 0; i < handShapes.Count; i++)
        {
            var handShape = handShapes[order[i]];
            var minDistance = shapeDistance.For(handShape.Position);
            var maxEnd = handShape.EndPosition;

            // Gap before the next hand shape.
            if (i + 1 < handShapes.Count)
            {
                var vsNextShape = handShapes[order[i + 1]].Position - minDistance;
                if (vsNextShape.CompareTo(maxEnd) < 0) maxEnd = vsNextShape;
            }

            // Gap before the next played sound at or after the shape's end (the
            // sound the "hold until next sound" clip parked the shape on). A shape
            // ending in empty space finds a far-off sound and is unaffected.
            //
            // The shape is deliberately NOT extended to cover a held/link-next
            // sustain that rings past it: a slide (or other link-next) sound rings
            // INTO the next note, so its sustain legitimately extends beyond the
            // hand-shape bar — owner-confirmed as the correct look. Only the
            // renderer's own trailing inset was ever making a NON-linked chord's
            // sustain appear to poke past its bar, and that inset is gone.
            var nextSound = FirstOnsetAtOrAfter(sortedSounds, soundlessChords, handShape.EndPosition);
            if (nextSound is { } ns)
            {
                var vsSound = ns - minDistance;
                if (vsSound.CompareTo(maxEnd) < 0) maxEnd = vsSound;
            }

            if (maxEnd.CompareTo(handShape.Position) < 0) maxEnd = handShape.Position;
            if (handShape.EndPosition.CompareTo(maxEnd) > 0)
            {
                SpacingCollateralRecorder.Current?.CaptureHandShapeEnd(handShape);
                handShape.EndPosition = maxEnd;
            }
        }
    }

    /// <summary>First onset at or after <paramref name="position" />, or null when
    /// none — the sound a hand shape leaves its gap before. The span is sorted by
    /// position (repeats and all, which a binary search does not care about); the
    /// side list is the handful of chords that carry no chord notes.</summary>
    private static FractionalPosition? FirstOnsetAtOrAfter(ReadOnlySpan<SoundEntry> sortedSounds,
        List<FractionalPosition>? soundlessChords, FractionalPosition position)
    {
        FractionalPosition? best = null;

        int lo = 0, hi = sortedSounds.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) >> 1;
            if (sortedSounds[mid].Position.CompareTo(position) < 0) lo = mid + 1;
            else hi = mid;
        }

        if (lo < sortedSounds.Length) best = sortedSounds[lo].Position;

        if (soundlessChords != null)
        {
            foreach (var onset in soundlessChords)
            {
                if (onset.CompareTo(position) >= 0 && (best == null || onset.CompareTo(best.Value) < 0))
                    best = onset;
            }
        }

        return best;
    }

    /// <summary>Orders hand-shape INDICES by position, ties broken on the index so
    /// an unstable sort behaves like the stable <c>OrderBy</c> it replaces.</summary>
    private sealed class HandShapePositionOrder(IList<HandShape> shapes) : IComparer<int>
    {
        public int Compare(int x, int y)
        {
            var byPosition = shapes[x].Position.CompareTo(shapes[y].Position);
            return byPosition != 0 ? byPosition : x.CompareTo(y);
        }
    }

    /// <summary>
    ///     <see cref="GetMinDistance" /> with a one-slot cache, for the sweeps that
    ///     ask for it once per sound.
    ///     <para>
    ///         It is a pure function of the position — and in the two FRACTION modes
    ///         only of which MEASURE the position sits in, because the numerator is
    ///         that measure's beat count (or 1) and the denominator is a setting.
    ///         So on a 4/4 chart it is ONE value for the whole level, and the sound
    ///         sweep was deriving it 20,000 times per committed edit: a backwards
    ///         walk to the nearest downbeat, a <c>Fraction</c> construction (which
    ///         reduces by GCD) and a <c>FractionalPosition</c> construction, each
    ///         time. The sweep visits positions in order, so keying on
    ///         <see cref="FractionalPosition.BeatId" /> collapses it to one
    ///         derivation per beat.
    ///     </para>
    ///     <para>
    ///         <see cref="MinNoteDistanceMode.Milliseconds" /> genuinely varies
    ///         inside a beat — it converts a ms offset back through the grid at
    ///         that exact point — so that mode keys on the whole position and only
    ///         collapses a chord's worth of notes. Same answer either way; this
    ///         type may never do anything but remember one.
    ///     </para>
    /// </summary>
    private struct MinDistanceMemo(IList<BeatMarker> beats, AppConfig config)
    {
        private readonly bool _perBeat = config.MinNoteDistanceMode != MinNoteDistanceMode.Milliseconds;
        private bool _hasKey;
        private FractionalPosition _key;
        private FractionalPosition _value;

        public FractionalPosition For(FractionalPosition position)
        {
            if (_hasKey && (_perBeat ? _key.BeatId == position.BeatId : _key.Equals(position)))
                return _value;

            _value = GetMinDistance(position, beats, config);
            _key = position;
            _hasKey = true;
            return _value;
        }
    }

    public static FractionalPosition GetMinDistance(FractionalPosition position, IList<BeatMarker> beats,
        AppConfig config)
    {
        var denominator = config.MinNoteDistanceDivisor > 0 ? config.MinNoteDistanceDivisor : 64;

        return config.MinNoteDistanceMode switch
        {
            MinNoteDistanceMode.Milliseconds => GetMillisecondsDistance(position, beats, config.MinNoteDistanceMs),
            MinNoteDistanceMode.BeatFraction => new FractionalPosition(new Fraction(1, denominator)),
            _ => new FractionalPosition(new Fraction(GetBeatsInMeasure(position, beats), denominator))
        };
    }

    private static FractionalPosition GetMillisecondsDistance(FractionalPosition position, IList<BeatMarker> beats,
        double milliseconds)
    {
        if (milliseconds <= 0 || beats.Count == 0) return FractionalPosition.Zero;

        var startMs = position.GetPositionMs(beats);
        var endPosition = TimelineMath.PositionFromMs(startMs + milliseconds, beats);
        return FractionalPosition.Max(FractionalPosition.Zero, endPosition - position);
    }

    private static int GetBeatsInMeasure(FractionalPosition position, IList<BeatMarker> beats)
    {
        if (beats.Count == 0) return 4;

        var beatIndex = Math.Clamp(position.BeatId, 0, beats.Count - 1);
        for (var i = beatIndex; i >= 0; i--)
        {
            if (beats[i].IsMeasure)
                return Math.Max(1, beats[i].BeatsInMeasure);
        }

        return Math.Max(1, beats[beatIndex].BeatsInMeasure);
    }

    private readonly record struct SoundTime(FractionalPosition Position, double TimeMs);
}
