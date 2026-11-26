// The pure half of tools/edit-replay: chart facts, the per-category diff, the edit counts with and
// without links, and the log parser. No file or import code lives here, so DotCharter.Tests compiles
// this file as it is (EditReplayCoreTests) and the counting rules are pinned in the default gate.
// The Core domain namespaces come from global usings (EditReplay.cs, and the test project's).
using System.Globalization;
using System.Text.RegularExpressions;

namespace DotCharter.Tools.EditReplay;

/// <summary>What one edit changes. <see cref="Fingering" /> is a chord whose fingering differs under no
/// shape whose grip is already being saved (a grip save re-fingers the chords it covers).</summary>
public enum EditCategory
{
    Anchor,
    Shape,
    Grip,
    Fingering,
    Sustain,
    Note
}

/// <summary>An FHP anchor at a beat position.</summary>
public sealed record AnchorFact(double Position, int Fret, int Width)
{
    public string Value => $"fret{Fret} w{Width}";
}

/// <summary>A hand shape; <see cref="Grip" /> is its template's frets and fingers.</summary>
public sealed record ShapeFact(double Position, double End, string Grip);

/// <summary>A note or a chord: its fretted strings, sustains and (chords) template fingering.</summary>
public sealed record SoundFact(double Position, string Strings, string Sustains, string Fingering);

/// <summary>One arrangement's facts, positions in beats (<see cref="FractionalPosition.DoubleValue" />).</summary>
public sealed record ChartFacts(
    IReadOnlyList<AnchorFact> Anchors,
    IReadOnlyList<ShapeFact> Shapes,
    IReadOnlyList<SoundFact> Sounds)
{
    /// <summary>Reads level 0 of <paramref name="arrangement" />.</summary>
    public static ChartFacts Of(Arrangement arrangement)
    {
        if (arrangement.Levels.Count == 0) return new ChartFacts([], [], []);
        var level = arrangement.Levels[0];
        var anchors = level.Anchors.Select(a => new AnchorFact(Beat(a.Position), a.Fret, a.Width)).ToList();
        var shapes = level.HandShapes
            .Select(h => new ShapeFact(Beat(h.Position), Beat(h.EndPosition), Grip(Template(arrangement, h.TemplateId))))
            .ToList();
        var sounds = level.Notes
            .Select(n => new SoundFact(Beat(n.Position), $"s{n.StringIndex}f{n.Fret}", Beats(n.Sustain), ""))
            .Concat(level.Chords.Select(c => Sound(arrangement, c)))
            .ToList();
        return new ChartFacts(anchors, shapes, sounds);
    }

    /// <summary>A template's grip, string by string: fret/finger, <c>-</c> for an unused string.</summary>
    public static string Grip(ChordTemplate? template)
    {
        if (template == null) return "?";
        var strings = template.Frets.Keys.Concat(template.Fingers.Keys).DefaultIfEmpty(-1).Max() + 1;
        return string.Join(" ", Enumerable.Range(0, strings).Select(s =>
            template.Frets.TryGetValue(s, out var fret) && fret >= 0
                ? $"{fret}/{(template.Fingers.TryGetValue(s, out var finger) && finger >= 0 ? finger.ToString(CultureInfo.InvariantCulture) : "-")}"
                : "-"));
    }

    private static SoundFact Sound(Arrangement arrangement, Chord chord)
    {
        var notes = chord.ChordNotes.OrderBy(n => n.StringIndex).ToList();
        return new SoundFact(Beat(chord.Position), string.Join(",", notes.Select(n => $"s{n.StringIndex}f{n.Fret}")),
            string.Join(",", notes.Select(n => Beats(n.Sustain))), Grip(Template(arrangement, chord.TemplateId)));
    }

    private static ChordTemplate? Template(Arrangement arrangement, int id) =>
        id >= 0 && id < arrangement.ChordTemplates.Count ? arrangement.ChordTemplates[id] : null;

    private static double Beat(FractionalPosition position) => Math.Round(position.DoubleValue, 4);

    private static string Beats(FractionalPosition length) => Math.Round(length.DoubleValue, 3).ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>
///     One edit that turns the fresh import into the charter's result. Positions are beats; the
///     fresh or the charter side is null when the edit adds or deletes. <see cref="Counted" /> is
///     false for a fingering diff a grip save already covers (listed, never counted).
/// </summary>
public sealed record ReplayEdit(
    string Arrangement,
    EditCategory Category,
    string Kind,
    int Bar,
    double Position,
    double? FreshEnd,
    double? CharterEnd,
    string Fresh,
    string Charter,
    bool Counted = true)
{
    /// <summary>The edit as seen from a placement starting at <paramref name="origin" />: two edits with
    /// equal signatures in two placements of one link group are the same edit.</summary>
    public string Signature(double origin) => string.Join("|", Category, Kind, Rel(Position, origin), Rel(FreshEnd, origin),
        Rel(CharterEnd, origin), Fresh, Charter);

    private static string Rel(double? position, double origin) =>
        position is { } p ? Math.Round(p - origin, 4).ToString(CultureInfo.InvariantCulture) : "";
}

/// <summary>A link-group placement, in beats, end exclusive.</summary>
public sealed record PlacementSpan(string Arrangement, double Start, double End)
{
    public bool Contains(string arrangement, double position) =>
        arrangement == Arrangement && position >= Start - 1e-6 && position < End - 1e-6;
}

/// <summary>Bar numbers (1-based) from the measure-start beat indices of a beat map.</summary>
public sealed class BarMap(IReadOnlyList<int> measureStarts)
{
    /// <summary>Four beats to the bar when the project has no measure starts.</summary>
    public static BarMap Of(IEnumerable<BeatMarker> beats) =>
        new([.. beats.Select((b, i) => (b, i)).Where(x => x.b.IsMeasure).Select(x => x.i)]);

    public int BarOf(double beat)
    {
        if (measureStarts.Count == 0) return (int)Math.Floor(beat / 4) + 1;
        var index = (int)Math.Floor(beat + 1e-6);
        var bar = 0;
        while (bar < measureStarts.Count && measureStarts[bar] <= index) bar++;
        return Math.Max(1, bar);
    }
}

/// <summary>The per-category diff of a fresh import against the charter's arrangement.</summary>
public static class ReplayDiff
{
    /// <summary>Every edit in bars <paramref name="firstBar" />..<paramref name="lastBar" />.</summary>
    public static List<ReplayEdit> Diff(string arrangement, ChartFacts fresh, ChartFacts charter, BarMap bars, int firstBar,
        int lastBar)
    {
        bool InRange(double p) => bars.BarOf(p) is var b && b >= firstBar && b <= lastBar;
        var edits = new List<ReplayEdit>();
        ReplayEdit Edit(EditCategory category, string kind, double position, double? freshEnd, double? charterEnd, string from,
            string to) => new(arrangement, category, kind, bars.BarOf(position), position, freshEnd, charterEnd, from, to);

        DiffAnchors(fresh.Anchors.Where(a => InRange(a.Position)).ToList(),
            charter.Anchors.Where(a => InRange(a.Position)).ToList(), bars, edits, Edit);
        DiffShapes(fresh.Shapes.Where(s => InRange(s.Position)).ToList(),
            charter.Shapes.Where(s => InRange(s.Position)).ToList(), edits, Edit);
        DiffSounds(fresh.Sounds.Where(s => InRange(s.Position)).ToList(),
            charter.Sounds.Where(s => InRange(s.Position)).ToList(), edits, Edit);
        return [.. edits.OrderBy(e => e.Position).ThenBy(e => e.Category)];
    }

    /// <summary>Counted edits per category, plus the total.</summary>
    public static Dictionary<EditCategory, int> CountByCategory(IEnumerable<ReplayEdit> edits) =>
        Enum.GetValues<EditCategory>().ToDictionary(c => c, c => edits.Count(e => e.Counted && e.Category == c));

    /// <summary>
    ///     Counted edits once links propagate: an edit made in one placement of a link group is free in
    ///     every other placement of that group where the same edit (same relative position, same before
    ///     and after) is needed. Groups chain (an edit shared through two groups is one edit).
    /// </summary>
    public static Dictionary<EditCategory, int> CountWithLinks(IReadOnlyList<ReplayEdit> edits,
        IEnumerable<IReadOnlyList<PlacementSpan>> groups)
    {
        var counted = edits.Where(e => e.Counted).ToList();
        var parent = Enumerable.Range(0, counted.Count).ToArray();
        int Find(int i) => parent[i] == i ? i : parent[i] = Find(parent[i]);
        foreach (var group in groups)
        {
            var first = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < counted.Count; i++)
            {
                foreach (var placement in group.Where(p => p.Contains(counted[i].Arrangement, counted[i].Position)))
                {
                    var signature = counted[i].Signature(placement.Start);
                    if (first.TryGetValue(signature, out var j)) parent[Find(i)] = Find(j);
                    else first[signature] = i;
                }
            }
        }

        var roots = Enumerable.Range(0, counted.Count).Where(i => Find(i) == i).Select(i => counted[i]).ToList();
        return CountByCategory(roots);
    }

    private static void DiffAnchors(List<AnchorFact> fresh, List<AnchorFact> charter, BarMap bars, List<ReplayEdit> edits,
        Func<EditCategory, string, double, double?, double?, string, string, ReplayEdit> edit)
    {
        RemoveExact(fresh, charter);
        // A fret or width change in place is one edit; so is a drag that keeps the fret, within its bar.
        foreach (var f in fresh.ToList())
        {
            var c = charter.FirstOrDefault(x => Same(x.Position, f.Position))
                    ?? charter.Where(x => x.Fret == f.Fret && x.Width == f.Width && bars.BarOf(x.Position) == bars.BarOf(f.Position))
                        .OrderBy(x => Math.Abs(x.Position - f.Position)).FirstOrDefault();
            if (c == null) continue;
            var kind = Same(c.Position, f.Position) ? "change" : "move";
            edits.Add(edit(EditCategory.Anchor, kind, f.Position, null, kind == "move" ? c.Position : null, f.Value, c.Value));
            fresh.Remove(f);
            charter.Remove(c);
        }

        edits.AddRange(fresh.Select(f => edit(EditCategory.Anchor, "delete", f.Position, null, null, f.Value, "")));
        edits.AddRange(charter.Select(c => edit(EditCategory.Anchor, "add", c.Position, null, null, "", c.Value)));
    }

    private static void DiffShapes(List<ShapeFact> fresh, List<ShapeFact> charter, List<ReplayEdit> edits,
        Func<EditCategory, string, double, double?, double?, string, string, ReplayEdit> edit)
    {
        RemoveExact(fresh, charter);
        foreach (var f in fresh.OrderBy(s => s.Position).ToList())
        {
            var c = charter.FirstOrDefault(x => Same(x.Position, f.Position))
                    ?? charter.Where(x => x.Position < f.End - 1e-6 && f.Position < x.End - 1e-6)
                        .OrderBy(x => x.Grip == f.Grip ? 0 : 1).ThenBy(x => Math.Abs(x.Position - f.Position)).FirstOrDefault();
            if (c == null) continue;
            if (!Same(c.Position, f.Position) || !Same(c.End, f.End))
                edits.Add(edit(EditCategory.Shape, "span", f.Position, f.End, c.End, Span(f, c.Position), Span(c, c.Position)));
            if (c.Grip != f.Grip) edits.Add(edit(EditCategory.Grip, "grip", c.Position, c.End, c.End, f.Grip, c.Grip));
            fresh.Remove(f);
            charter.Remove(c);
        }

        edits.AddRange(fresh.Select(f => edit(EditCategory.Shape, "delete", f.Position, f.End, null, f.Grip, "")));
        edits.AddRange(charter.Select(c => edit(EditCategory.Shape, "add", c.Position, null, c.End, "", c.Grip)));
    }

    private static void DiffSounds(List<SoundFact> fresh, List<SoundFact> charter, List<ReplayEdit> edits,
        Func<EditCategory, string, double, double?, double?, string, string, ReplayEdit> edit)
    {
        var grips = edits.Where(e => e.Category == EditCategory.Grip).ToList();
        foreach (var f in fresh.ToList())
        {
            var c = charter.FirstOrDefault(x => Same(x.Position, f.Position) && x.Strings == f.Strings);
            if (c == null) continue;
            if (c.Sustains != f.Sustains) edits.Add(edit(EditCategory.Sustain, "sustain", f.Position, null, null, f.Sustains, c.Sustains));
            if (c.Fingering != f.Fingering)
            {
                var covered = grips.Any(g => g.Position <= f.Position + 1e-6 && f.Position < (g.CharterEnd ?? g.Position) - 1e-6);
                edits.Add(edit(EditCategory.Fingering, "fingering", f.Position, null, null, f.Fingering, c.Fingering) with
                {
                    Counted = !covered
                });
            }

            fresh.Remove(f);
            charter.Remove(c);
        }

        // A note moved to another string or fret in place is one edit.
        foreach (var f in fresh.ToList())
        {
            var c = charter.FirstOrDefault(x => Same(x.Position, f.Position));
            if (c == null) continue;
            edits.Add(edit(EditCategory.Note, "change", f.Position, null, null, f.Strings, c.Strings));
            fresh.Remove(f);
            charter.Remove(c);
        }

        edits.AddRange(fresh.Select(f => edit(EditCategory.Note, "delete", f.Position, null, null, f.Strings, "")));
        edits.AddRange(charter.Select(c => edit(EditCategory.Note, "add", c.Position, null, null, "", c.Strings)));
    }

    private static void RemoveExact<T>(List<T> fresh, List<T> charter)
    {
        foreach (var f in fresh.ToList())
        {
            var index = charter.IndexOf(f);
            if (index < 0) continue;
            charter.RemoveAt(index);
            fresh.Remove(f);
        }
    }

    private static string Span(ShapeFact shape, double origin) =>
        $"{Math.Round(shape.Position - origin, 4).ToString(CultureInfo.InvariantCulture)}..{Math.Round(shape.End - origin, 4).ToString(CultureInfo.InvariantCulture)}";

    private static bool Same(double a, double b) => Math.Abs(a - b) < 1e-4;
}

/// <summary>What the app log says the charter did: chart edits per arrangement, undos, redos and
/// the beats their editing touched.</summary>
public sealed partial class LoggedSession
{
    private static readonly HashSet<string> NotChartEdits =
        new(StringComparer.Ordinal) { "ProjectSnapshotAction", "key:History_LinkSuggestions", "MutateBeatsAction" };

    public Dictionary<string, int> Edits { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, int> EditsByAction { get; } = new(StringComparer.Ordinal);

    public int Undos { get; private set; }

    public int Redos { get; private set; }

    public List<double> EditedBeats { get; } = [];

    /// <summary>Parses the app log's History and Editing lines.</summary>
    public static LoggedSession Parse(IEnumerable<string> lines)
    {
        var session = new LoggedSession();
        foreach (var line in lines)
        {
            if (line.Contains("[History]", StringComparison.Ordinal))
            {
                if (ExecutedLine().Match(line) is { Success: true } executed && !NotChartEdits.Contains(executed.Groups[1].Value))
                {
                    var target = executed.Groups[2].Value;
                    session.Edits[target] = session.Edits.GetValueOrDefault(target) + 1;
                    var action = executed.Groups[1].Value;
                    session.EditsByAction[action] = session.EditsByAction.GetValueOrDefault(action) + 1;
                }
                else
                {
                    session.Undos += line.Contains("] Undo ", StringComparison.Ordinal) ? 1 : 0;
                    session.Redos += line.Contains("] Redo ", StringComparison.Ordinal) ? 1 : 0;
                }
            }
            else if (line.Contains("[Editing]", StringComparison.Ordinal))
            {
                foreach (Match position in PositionText().Matches(line))
                {
                    var beat = int.Parse(position.Groups[1].Value, CultureInfo.InvariantCulture) +
                               double.Parse(position.Groups[2].Value, CultureInfo.InvariantCulture) /
                               Math.Max(1, double.Parse(position.Groups[3].Value, CultureInfo.InvariantCulture));
                    session.EditedBeats.Add(beat);
                }
            }
        }

        return session;
    }

    [GeneratedRegex(@"Executed (\S+) \(target=([^,]+),")]
    private static partial Regex ExecutedLine();

    [GeneratedRegex(@"(?:position=|at )(\d+) (\d+)/(\d+) \(")]
    private static partial Regex PositionText();
}
