using System.Globalization;
using System.Text;

namespace DotCharter.Services.Editing.FretAnchors;

/// <summary>
///     Gives the repeats of a measure the same FHP anchors. The generator's walk reads
///     context, so two measures that play the same notes can come out with different anchors:
///     one restates a position the other inherits, or carries an extra mid-measure anchor. Such
///     copies never link, because a link copies anchors and so demands they match.
///     <para>
///         Measures whose sounds and hand shapes are identical relative to their barline form a
///         group. Each member's anchor PATTERN is its anchors plus the position in effect at its
///         downbeat; the group takes the pattern most of its members already have (ties go to the
///         one with the fewest hand moves, then the earliest), so every copy states its downbeat
///         and does not depend on what came before it. A pattern that suits one member suits every member, since they play the same frets
///         at the same offsets. The measure after a rewritten one gets its old position restated
///         at its downbeat, so nothing outside the group moves. A member is left as it was when a
///         sound held across one of its barlines would land outside a changed position.
///     </para>
/// </summary>
public static class RepeatedMeasureAnchors
{
    /// <summary>The anchors with every group of repeated measures given one pattern.</summary>
    /// <param name="level">The level the anchors were generated for.</param>
    /// <param name="anchors">The generated anchors, ordered by position.</param>
    /// <param name="barlines">Every barline in ascending order, the end of the last measure included.</param>
    /// <param name="chordTemplates">The templates the level's chords and hand shapes point at.</param>
    /// <param name="capo">The arrangement's capo: a fret at or below it is open.</param>
    public static IReadOnlyList<FretAnchor> Harmonize(ChartLevel level, IReadOnlyList<FretAnchor> anchors,
        IReadOnlyList<FractionalPosition> barlines, IReadOnlyList<ChordTemplate> chordTemplates, int capo)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(anchors);
        ArgumentNullException.ThrowIfNull(barlines);
        ArgumentNullException.ThrowIfNull(chordTemplates);
        if (barlines.Count < 3 || anchors.Count == 0) return anchors;

        var original = anchors.OrderBy(a => a.Position).ToList();
        var templates = new Dictionary<int, ChordTemplate>();
        foreach (var template in chordTemplates) templates.TryAdd(template.Id, template);
        var sounds = Sounds(level);
        var targets = Targets(level, original, barlines, templates);
        if (targets.Count == 0) return anchors;

        var held = sounds.Where(s => s.Fret > Math.Max(0, capo) && s.End > s.Position).ToList();
        while (true)
        {
            var result = Build(original, barlines, targets);
            var broken = targets.Keys.Where(m => Breaks(held, original, result, barlines[m], barlines[m + 1])).ToList();
            if (broken.Count == 0) return result;
            foreach (var m in broken) targets.Remove(m);
            if (targets.Count == 0) return anchors;
        }
    }

    private readonly record struct Sound(FractionalPosition Position, FractionalPosition End, int Fret);

    private readonly record struct Step(FractionalPosition Offset, int Fret, int Width);

    private static List<Sound> Sounds(ChartLevel level) =>
    [
        .. level.Notes.Select(n => new Sound(n.Position, n.Position + n.Sustain, n.Fret)),
        .. level.Chords.SelectMany(c => c.ChordNotes.Select(n => new Sound(c.Position, c.Position + n.Sustain, n.Fret)))
    ];

    // The pattern every repeated measure should carry, by measure index.
    private static Dictionary<int, List<Step>> Targets(ChartLevel level, List<FretAnchor> anchors,
        IReadOnlyList<FractionalPosition> barlines, Dictionary<int, ChordTemplate> templates)
    {
        var groups = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var m = 0; m + 1 < barlines.Count; m++)
        {
            var key = ContentKey(level, templates, barlines[m], barlines[m + 1]);
            if (key == null) continue;
            if (!groups.TryGetValue(key, out var members)) groups[key] = members = [];
            members.Add(m);
        }

        var targets = new Dictionary<int, List<Step>>();
        foreach (var members in groups.Values.Where(g => g.Count > 1))
        {
            var patterns = members.Select(m => Pattern(anchors, barlines[m], barlines[m + 1])).ToList();
            var chosen = patterns.Select((p, i) => (Pattern: p, Index: i))
                .Where(p => p.Pattern != null)
                .GroupBy(p => Describe(p.Pattern!), StringComparer.Ordinal)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.First().Pattern!.Count).ThenBy(g => g.Min(p => p.Index))
                .Select(g => g.First().Pattern).FirstOrDefault();
            if (chosen == null) continue;
            foreach (var m in members) targets[m] = chosen;
        }

        return targets;
    }

    // A measure's anchors relative to its barline, led by the position in effect at its
    // downbeat; null when no position is in effect there yet (nothing to restate).
    private static List<Step>? Pattern(List<FretAnchor> anchors, FractionalPosition start, FractionalPosition end)
    {
        var inside = anchors.Where(a => a.Position >= start && a.Position < end).ToList();
        var steps = new List<Step>();
        if (inside.Count == 0 || inside[0].Position != start)
        {
            if (InEffect(anchors, start) is not { } inherited) return null;
            steps.Add(new Step(FractionalPosition.Zero, inherited.Fret, inherited.Width));
        }

        steps.AddRange(inside.Select(a => new Step(a.Position - start, a.Fret, a.Width)));
        return steps;
    }

    private static string Describe(List<Step> pattern) =>
        string.Join(";", pattern.Select(s => string.Create(CultureInfo.InvariantCulture, $"{s.Offset}:{s.Fret}/{s.Width}")));

    // Measures with the same key play the same frets at the same offsets; null for a measure
    // with no sound.
    private static string? ContentKey(ChartLevel level, Dictionary<int, ChordTemplate> templates,
        FractionalPosition start, FractionalPosition end)
    {
        var tokens = new List<string>();
        foreach (var note in level.Notes.Where(n => n.Position >= start && n.Position < end))
        {
            tokens.Add(string.Create(CultureInfo.InvariantCulture,
                $"N{NoteToken(note, note.Position - start)}:{note.LeftHandFinger}:{note.GraceFret}"));
        }

        foreach (var chord in level.Chords.Where(c => c.Position >= start && c.Position < end))
        {
            var sb = new StringBuilder("C").Append(chord.Position - start).Append(':');
            AppendTemplate(sb, templates, chord.TemplateId);
            foreach (var note in chord.ChordNotes.OrderBy(n => n.StringIndex))
                sb.Append('[').Append(NoteToken(note, FractionalPosition.Zero)).Append(']');
            tokens.Add(sb.ToString());
        }

        if (tokens.Count == 0) return null;
        foreach (var shape in level.HandShapes.Where(h => h.Position < end && h.EndPosition > start))
        {
            var sb = new StringBuilder("H")
                .Append(shape.Position >= start ? (shape.Position - start).ToString() : "<")
                .Append('-')
                .Append(shape.EndPosition <= end ? (shape.EndPosition - start).ToString() : ">")
                .Append(':');
            AppendTemplate(sb, templates, shape.TemplateId);
            tokens.Add(sb.ToString());
        }

        tokens.Sort(StringComparer.Ordinal);
        return (end - start) + "|" + string.Join("|", tokens);
    }

    private static string NoteToken(IGuitarSound sound, FractionalPosition offset) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{offset}:{sound.StringIndex}:{sound.Fret}:{sound.Sustain}:{sound.SlideTo}:{sound.UnpitchedSlide}:{sound.LinkNext}:{sound.BendValues.Count}");

    private static void AppendTemplate(StringBuilder sb, Dictionary<int, ChordTemplate> templates, int id)
    {
        if (!templates.TryGetValue(id, out var template))
        {
            sb.Append('?').Append(id.ToString(CultureInfo.InvariantCulture));
            return;
        }

        foreach (var (stringIndex, fret) in template.Frets.OrderBy(f => f.Key))
        {
            var finger = template.Fingers.TryGetValue(stringIndex, out var f) ? f : -1;
            sb.Append(CultureInfo.InvariantCulture, $"{stringIndex}={fret}/{finger},");
        }
    }

    private static FretAnchor? InEffect(IReadOnlyList<FretAnchor> anchors, FractionalPosition at)
    {
        FretAnchor? last = null;
        foreach (var anchor in anchors)
        {
            if (anchor.Position > at) break;
            last = anchor;
        }

        return last;
    }

    // Walks the measures left to right: a target measure gets its pattern (its downbeat only
    // where the position in effect differs), any other measure its own anchors, with its old
    // downbeat position restated when the rewrite before it changed what it inherits.
    private static List<FretAnchor> Build(List<FretAnchor> original, IReadOnlyList<FractionalPosition> barlines,
        Dictionary<int, List<Step>> targets)
    {
        var result = original.Where(a => a.Position < barlines[0]).Select(a => a.Clone()).ToList();
        var measures = barlines.Count - 1;
        for (var m = 0; m <= measures; m++)
        {
            var start = barlines[m];
            var end = m < measures ? barlines[m + 1] : (FractionalPosition?)null;
            var current = result.Count > 0 ? result[^1] : null;
            if (m < measures && targets.TryGetValue(m, out var pattern))
            {
                foreach (var step in pattern)
                {
                    if (step.Offset == FractionalPosition.Zero && current != null &&
                        current.Fret == step.Fret && current.Width == step.Width)
                    {
                        continue;
                    }

                    current = new FretAnchor(start + step.Offset, step.Fret, step.Width);
                    result.Add(current);
                }

                continue;
            }

            var own = original.Where(a => a.Position >= start && (end == null || a.Position < end)).ToList();
            if ((own.Count == 0 || own[0].Position != start) && InEffect(original, start) is { } inherited &&
                (current == null || current.Fret != inherited.Fret || current.Width != inherited.Width))
            {
                result.Add(new FretAnchor(start, inherited.Fret, inherited.Width));
            }

            result.AddRange(own.Select(a => a.Clone()));
        }

        return result;
    }

    // True when a sound held across the measure's start or end barline would be played under
    // a position that differs from before and does not reach its fret.
    private static bool Breaks(List<Sound> held, List<FretAnchor> original, List<FretAnchor> result,
        FractionalPosition start, FractionalPosition end)
    {
        foreach (var sound in held)
        {
            var crosses = sound.Position < start && sound.End > start || sound.Position < end && sound.End > end;
            if (!crosses) continue;
            var before = Positions(original, sound);
            foreach (var anchor in Positions(result, sound))
            {
                if (before.Contains((anchor.Fret, anchor.Width))) continue;
                if (sound.Fret < anchor.Fret || sound.Fret > anchor.Fret + anchor.Width - 1) return true;
            }
        }

        return false;
    }

    private static HashSet<(int Fret, int Width)> Positions(List<FretAnchor> anchors, Sound sound)
    {
        var positions = new HashSet<(int, int)>();
        if (InEffect(anchors, sound.Position) is { } first) positions.Add((first.Fret, first.Width));
        foreach (var anchor in anchors.Where(a => a.Position > sound.Position && a.Position < sound.End))
            positions.Add((anchor.Fret, anchor.Width));
        return positions;
    }
}
