namespace DotCharter.Services.Editing.Structure;

/// <summary>Merges authored and imported markers while retaining the target difficulty ceilings.</summary>
internal static class StructureMarkerMerge
{
    public static int MergeInto(Arrangement source, Arrangement target)
    {
        var changed = new HashSet<FractionalPosition>();
        var markers = StructureMarkers(source);
        var byPosition = new Dictionary<FractionalPosition, EventPoint>();
        foreach (var existing in target.EventPoints) byPosition.TryAdd(existing.Position, existing);

        foreach (var marker in markers)
        {
            if (byPosition.TryGetValue(marker.Position, out var existing))
            {
                if (string.Equals(existing.Section, marker.Section, StringComparison.Ordinal) &&
                    string.Equals(existing.Phrase, marker.Phrase, StringComparison.Ordinal) &&
                    string.Equals(existing.Events, marker.Events, StringComparison.Ordinal) &&
                    existing.Status == marker.Status)
                {
                    continue;
                }

                existing.Section = marker.Section;
                existing.Phrase = marker.Phrase;
                existing.Events = marker.Events;
                existing.Status = marker.Status;
                changed.Add(marker.Position);
                continue;
            }

            var copy = new EventPoint
            {
                Position = marker.Position,
                Section = marker.Section,
                Phrase = marker.Phrase,
                Events = marker.Events,
                Status = marker.Status
            };
            target.EventPoints.Add(copy);
            byPosition[copy.Position] = copy;
            changed.Add(marker.Position);
        }

        if (changed.Count > 0)
        {
            var ordered = target.EventPoints.OrderBy(e => e.Position).ToList();
            target.EventPoints.Clear();
            foreach (var e in ordered) target.EventPoints.Add(e);
        }

        // The solo flag is keyed by phrase NAME, so it has to follow the phrases
        // that were copied or a solo phrase silently stops being one.
        //
        // ONLY the solo flag. MaxDifficulty is a fact about the TARGET's own
        // level stack, not about the phrase: a lead with eight generated levels
        // copied its structure onto a single-level rhythm and left every phrase
        // there claiming a ceiling of 7. LiveExpectedTimeline.BuildPhraseSpans and
        // LevelSquashService.ResolveSourceLevelId both read that number back —
        // only the matcher clamps it, and the value is what the RS export writes.
        var targetCeiling = TopDifficultyIndex(target);
        foreach (var marker in markers)
        {
            if (string.IsNullOrWhiteSpace(marker.Phrase)) continue;

            if (UpdateIterationPhrase(target, marker)) changed.Add(marker.Position);

            if (!source.PhraseInfos.TryGetValue(marker.Phrase, out var info)) continue;
            if (target.PhraseInfos.TryGetValue(marker.Phrase, out var existingInfo))
            {
                if (existingInfo.Solo == info.Solo) continue;
                existingInfo.Solo = info.Solo;
            }
            else
            {
                target.PhraseInfos[marker.Phrase] = new PhraseInfo(targetCeiling, info.Solo);
            }
            changed.Add(marker.Position);
        }

        return changed.Count;
    }

    private static List<EventPoint> StructureMarkers(Arrangement source)
    {
        var markers = source.EventPoints.Select(marker => new EventPoint
        {
            Position = marker.Position, Section = marker.Section, Phrase = marker.Phrase,
            Events = marker.Events, Status = marker.Status
        }).ToList();
        var byPosition = new Dictionary<FractionalPosition, EventPoint>();
        foreach (var marker in markers) byPosition.TryAdd(marker.Position, marker);
        foreach (var phrase in PhraseMarkers.All(source))
        {
            if (string.IsNullOrWhiteSpace(phrase.Name)) continue;
            if (byPosition.TryGetValue(phrase.Position, out var marker))
            {
                marker.Phrase = phrase.Name;
            }
            else
            {
                marker = new EventPoint { Position = phrase.Position, Phrase = phrase.Name };
                markers.Add(marker);
                byPosition.Add(phrase.Position, marker);
            }
        }

        return markers;
    }

    // Iterations() preserves explicit iterations at occupied positions. Keep them
    // in step with copied editor markers so exports see the copy too.
    private static bool UpdateIterationPhrase(Arrangement target, EventPoint marker)
    {
        var changed = false;
        foreach (var iteration in target.PhraseIterations.Where(i => i.Position == marker.Position))
        {
            var name = iteration.PhraseId >= 0 && iteration.PhraseId < target.Phrases.Count
                ? target.Phrases[iteration.PhraseId]
                : null;
            if (string.Equals(name, marker.Phrase, StringComparison.Ordinal)) continue;
            var id = target.Phrases.IndexOf(marker.Phrase);
            if (id < 0)
            {
                id = target.Phrases.Count;
                target.Phrases.Add(marker.Phrase);
            }

            iteration.PhraseId = id;
            changed = true;
        }

        return changed;
    }

    /// <summary>The highest difficulty an arrangement actually has — the same
    /// value <c>LiveExpectedTimeline.BuildPhraseSpans</c> falls back to for a phrase
    /// carrying no <see cref="PhraseInfo" />, so it is the neutral ceiling for a
    /// phrase this copy has to invent one for.</summary>
    private static int TopDifficultyIndex(Arrangement arrangement) =>
        arrangement.Levels.Count == 0 ? 0 : Math.Max(0, arrangement.Levels.Max(level => level.DifficultyIndex));
}
