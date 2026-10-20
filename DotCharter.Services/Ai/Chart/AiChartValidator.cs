using System.Globalization;
using DotCharter.Services.Editing.Anchors;

namespace DotCharter.Services.Ai.Chart;

/// <summary>
///     Turns a decoded reply into a plan for ONE chunk, or rejects the chunk whole.
///     <para>
/// <b>The model proposes, the app decides.</b> The reply may change only
///         anchors (fret, width) and chord-template fingers or names. Everything else in it
///         is an ECHO the validator compares with the chart: a chord's id must name a sound
///         of the chunk's core (never its context), its frets must be the chord's frets
///         string for string, its time the chord's time, and every chord the chunk offered
///         must come back exactly once. Any difference is a rejection with a code — the
///         chunk is never repaired, because a reply that moved one fret has shown it did
///         not read the chart, and nothing else it says can be trusted either.
///     </para>
///     <para>
///         This is the STRUCTURAL half. The inspector's own rules (L02 L03 L04 L05 L06 L07
///         L08 E04 I45 I46) run afterwards on a scratch copy, through
///         <see cref="AiChartRuleGate" />, so nothing here re-implements them; the checks
///         below that look like rules (a finger on an open string, an anchor under the
///         capo) are the ones a plan cannot even be BUILT past.
///     </para>
/// </summary>
public static class AiChartValidator
{
    /// <summary>The highest fret an anchor may sit on (the rules' own limit).</summary>
    public const int MaxFret = 24;

    /// <summary>The widest anchor accepted. Rocksmith charts use 4–6; anything past this
    /// is a misunderstanding, not a hand position.</summary>
    public const int MaxWidth = 12;

    /// <summary>Longest chord name accepted.</summary>
    public const int MaxNameLength = 32;

    /// <summary>Builds the plan for <paramref name="chunk" /> from <paramref name="reply" />.</summary>
    /// <param name="reply">The decoded reply.</param>
    /// <param name="chunk">The chunk the request was built from.</param>
    /// <param name="job">The job.</param>
    /// <param name="facts">The song (tuning, capo, templates).</param>
    /// <param name="level">The level, for the anchors the chunk holds now.</param>
    /// <param name="plan">The plan, when the answer is true.</param>
    /// <param name="rejection">Why the chunk was rejected, when the answer is false.</param>
    public static bool TryPlan(
        AiChartReply reply, AiChartChunk chunk, AiChartJob job, AiChartSongFacts facts, ChartLevel level,
        out AiChartChunkPlan? plan, out AiChartRejection? rejection)
    {
        ArgumentNullException.ThrowIfNull(reply);
        ArgumentNullException.ThrowIfNull(chunk);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(level);

        plan = null;
        rejection = null;
        var templates = new Dictionary<int, ChordTemplate>();
        foreach (var t in facts.Arrangement.ChordTemplates) templates.TryAdd(t.Id, t);

        var core = chunk.Core.ToDictionary(s => s.Id, StringComparer.Ordinal);
        var context = chunk.Before.Concat(chunk.After).Select(s => s.Id).ToHashSet(StringComparer.Ordinal);

        AiChartRejection Reject(string code, string reason) => new(chunk.Index, code, reason);

        // ---- chords ------------------------------------------------------------
        // FHP only lists no chords: the model is never asked about a fingering.
        var required = chunk.Core.Where(s => job.ListsChords() && AiChartPrompt.IsFixable(s, templates))
            .Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var changes = new List<AiChordChange>();
        var strings = facts.StringCount;
        var capo = facts.Arrangement.Capo;

        foreach (var rc in reply.Chords)
        {
            if (!core.TryGetValue(rc.Id, out var sound))
            {
                rejection = context.Contains(rc.Id)
                    ? Reject(AiChartRejectionCodes.ContextSound, $"chord {rc.Id} is a context sound")
                    : Reject(AiChartRejectionCodes.UnknownSound, $"chord {rc.Id} is not a sound of the chunk");
                return false;
            }

            if (!required.Contains(rc.Id) || !seen.Add(rc.Id))
            {
                rejection = Reject(AiChartRejectionCodes.ChordCount,
                    required.Contains(rc.Id) ? $"chord {rc.Id} is listed twice" : $"{rc.Id} is not a chord to fix");
                return false;
            }

            var chord = sound.Chord!;
            var template = templates[chord.TemplateId];
            var frets = AiChartPrompt.FretArray(template, strings);
            if (!rc.Frets.SequenceEqual(frets))
            {
                rejection = Reject(AiChartRejectionCodes.FretsChanged,
                    $"chord {rc.Id}: frets [{string.Join(",", rc.Frets)}] are not the chart's [{string.Join(",", frets)}]");
                return false;
            }

            if (Math.Abs(rc.Ms - sound.Ms) > 1)
            {
                rejection = Reject(AiChartRejectionCodes.TimingChanged,
                    $"chord {rc.Id}: time {rc.Ms} ms is not the chart's {sound.Ms} ms");
                return false;
            }

            if (job.ProposesFingers())
            {
                if (!TryFingers(rc, frets, capo, out var fingerRejection))
                {
                    rejection = Reject(fingerRejection!.Value.Code, fingerRejection.Value.Reason);
                    return false;
                }

                if (rc.Fingers!.SequenceEqual(AiChartPrompt.FingerArray(template, strings))) continue;
                var fingers = FingersFor(template, rc.Fingers!);

                // Fingering only: the anchors are INPUT. A fingering whose index finger is not
                // on the fixed anchor's fret is the disagreement inspector rule E04 reports
                // (the same IndexFingerPosition question), and it rejects the chunk.
                if (job == AiChartJob.FingeringOnly)
                {
                    var proposed = template.Clone();
                    proposed.Fingers = fingers;
                    var anchor = ArrangementValidationContext.FindActiveAnchor(level, sound.Position);
                    if (IndexFingerPosition.Disagrees(proposed, anchor, capo))
                    {
                        rejection = Reject(AiChartRejectionCodes.AnchorContradicted,
                            $"chord {rc.Id}: {IndexFingerPosition.Find(proposed, capo)!.Value.Describe()}, " +
                            $"but the fixed anchor is on fret {anchor!.Fret} (E04)");
                        return false;
                    }
                }

                changes.Add(new AiChordChange(sound.ChordIndex, sound.Position, Signature(template), template.Id,
                    fingers, template.Name));
            }
            else
            {
                var name = rc.Name ?? "";
                if (name.Length > MaxNameLength || name.Any(char.IsControl) || name != name.Trim())
                {
                    rejection = Reject(AiChartRejectionCodes.NameInvalid, $"chord {rc.Id}: \"{name}\" is not a usable name");
                    return false;
                }

                if (string.Equals(name, template.Name, StringComparison.Ordinal)) continue;
                changes.Add(new AiChordChange(sound.ChordIndex, sound.Position, Signature(template), template.Id,
                    new Dictionary<int, int>(template.Fingers), name));
            }
        }

        if (seen.Count != required.Count)
        {
            var missing = required.Where(id => !seen.Contains(id)).Take(5);
            rejection = Reject(AiChartRejectionCodes.ChordCount,
                $"{required.Count - seen.Count} chord(s) missing from the reply ({string.Join(", ", missing)})");
            return false;
        }

        // ---- anchors -----------------------------------------------------------
        List<AiAnchorPlacement>? anchors = null;
        if (job.ProposesAnchors())
        {
            if (!TryAnchors(reply.Anchors ?? [], chunk, core, context, capo, out anchors, out var anchorRejection))
            {
                rejection = Reject(anchorRejection!.Value.Code, anchorRejection.Value.Reason);
                return false;
            }

            if (SameAsLevel(anchors!, chunk, level)) anchors = null;
        }

        // FHP only: the follower re-fingers the core's chords from the new anchors, so the
        // plan depends on them being what the model saw.
        var guards = job == AiChartJob.FhpOnly
            ? chunk.Core.Where(s => s.Chord is { TemplateId: >= 0 } c && templates.ContainsKey(c.TemplateId))
                .Select(s => new AiChordGuard(s.ChordIndex, s.Position, Signature(templates[s.Chord!.TemplateId]),
                    s.Chord.TemplateId))
                .ToList()
            : null;
        // The hand positions the model saw: a proposal held for review is stale once
        // the charter moves one of them. The name job does not depend on them.
        var shown = job == AiChartJob.ChordNames
            ? null
            : level.Anchors.Where(a => chunk.Contains(a.Position)).OrderBy(a => a.Position)
                .Select(a => new AiAnchorPlacement(a.Position, a.Fret, a.Width)).ToList();
        plan = new AiChartChunkPlan(chunk.Index, chunk.Start, chunk.End, anchors, changes,
            FingersFollowAnchors: job == AiChartJob.FhpOnly, Guards: guards, SeenAnchors: shown);
        return true;
    }

    /// <summary>The fret signature an <see cref="AiChordChange" /> is re-checked against.</summary>
    /// <param name="template">The template.</param>
    public static string Signature(ChordTemplate template)
        => string.Join(",", template.Frets.OrderBy(kv => kv.Key)
            .Select(kv => string.Create(CultureInfo.InvariantCulture, $"{kv.Key}:{kv.Value}")));

    private static bool TryFingers(
        AiChartReplyChord rc, int[] frets, int capo, out (string Code, string Reason)? rejection)
    {
        rejection = null;
        var fingers = rc.Fingers!;
        if (fingers.Count != frets.Length || fingers.Any(f => f is < -1 or > 4))
        {
            rejection = (AiChartRejectionCodes.FingerInvalid,
                $"chord {rc.Id}: fingers [{string.Join(",", fingers)}] are not one of -1..4 per string");
            return false;
        }

        for (var s = 0; s < frets.Length; s++)
        {
            var fret = frets[s];
            var finger = fingers[s];
            if (fret < 0 && finger != -1)
            {
                rejection = (AiChartRejectionCodes.FingerInvalid, $"chord {rc.Id}: a finger on the unused string {s}");
                return false;
            }

            if (fret >= 0 && fret <= capo && finger != -1)
            {
                rejection = (AiChartRejectionCodes.FingerOnOpenString,
                    $"chord {rc.Id}: finger {finger} on the open string {s} (L06)");
                return false;
            }

            if (fret > capo && finger < 0)
            {
                rejection = (AiChartRejectionCodes.FingerMissing,
                    $"chord {rc.Id}: no finger on the fretted string {s} (L07)");
                return false;
            }
        }

        return true;
    }

    /// <summary>The template's fingers with the reply's applied to the strings it plays,
    /// keeping the template's own spelling of "no finger" (absent or −1) so an unchanged
    /// string compares equal.</summary>
    private static Dictionary<int, int> FingersFor(ChordTemplate template, IReadOnlyList<int> fingers)
    {
        var result = new Dictionary<int, int>(template.Fingers);
        foreach (var s in template.Frets.Keys)
        {
            if (s >= fingers.Count) continue;
            if (fingers[s] >= 0) result[s] = fingers[s];
            else if (result.ContainsKey(s)) result[s] = -1;
        }

        return result;
    }

    private static bool TryAnchors(
        IReadOnlyList<AiChartReplyAnchor> replyAnchors, AiChartChunk chunk,
        IReadOnlyDictionary<string, AiChartSound> core, IReadOnlySet<string> context, int capo,
        out List<AiAnchorPlacement>? anchors, out (string Code, string Reason)? rejection)
    {
        anchors = null;
        rejection = null;
        if (replyAnchors.Count == 0 || !string.Equals(replyAnchors[0].At, AiChartPrompt.StartToken, StringComparison.Ordinal))
        {
            rejection = (AiChartRejectionCodes.AnchorAtStart, "the first anchor is not at \"start\"");
            return false;
        }

        var floor = AnchorFretSuggester.MinFret(capo);
        var list = new List<AiAnchorPlacement>(replyAnchors.Count);
        for (var i = 0; i < replyAnchors.Count; i++)
        {
            var a = replyAnchors[i];
            var position = chunk.Start;
            if (i > 0 && core.TryGetValue(a.At, out var sound))
            {
                position = sound.Position;
            }
            else if (i > 0)
            {
                rejection = context.Contains(a.At)
                    ? (AiChartRejectionCodes.ContextSound, $"an anchor is placed on the context sound {a.At}")
                    : (AiChartRejectionCodes.AnchorPlacement, $"an anchor is placed at \"{a.At}\", which is not a chunk sound");
                return false;
            }

            if (list.Count > 0 && position <= list[^1].Position)
            {
                rejection = (AiChartRejectionCodes.AnchorPlacement,
                    $"the anchor at {a.At} is not after the one before it");
                return false;
            }

            if (a.Fret < floor)
            {
                rejection = (AiChartRejectionCodes.AnchorBelowCapo,
                    $"an anchor on fret {a.Fret} is below the lowest legal fret {floor} (capo {capo}, L02)");
                return false;
            }

            if (a.Fret > MaxFret || a.Width < 1 || a.Width > MaxWidth || a.Fret + Math.Max(a.Width, 4) - 1 > MaxFret)
            {
                rejection = (AiChartRejectionCodes.AnchorRange, $"an anchor fret {a.Fret} width {a.Width} is off the neck");
                return false;
            }

            list.Add(new AiAnchorPlacement(position, a.Fret, a.Width));
        }

        anchors = list;
        return true;
    }

    private static bool SameAsLevel(IReadOnlyList<AiAnchorPlacement> anchors, AiChartChunk chunk, ChartLevel level)
    {
        var existing = level.Anchors.Where(a => chunk.Contains(a.Position)).OrderBy(a => a.Position).ToList();
        if (existing.Count != anchors.Count) return false;
        for (var i = 0; i < anchors.Count; i++)
        {
            if (existing[i].Position != anchors[i].Position || existing[i].Fret != anchors[i].Fret ||
                existing[i].Width != anchors[i].Width)
            {
                return false;
            }
        }

        return true;
    }
}
