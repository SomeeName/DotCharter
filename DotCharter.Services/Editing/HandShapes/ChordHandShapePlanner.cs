using Chord = DotCharter.Core.Domain.Guitar.Chord;
using HandShape = DotCharter.Core.Domain.Guitar.HandShape;

namespace DotCharter.Services.Editing.HandShapes;

/// <summary>
///     Decides which grip a chord is played with, and whether a following chord is
///     still played with the same one. The planner owns the GROUPING; the source
///     owns the TEMPLATES, because the two callers resolve them differently: the
///     tab importer reuses any plain template with the chord's frets and prefers
///     the file's authored fingering and name, while the editor goes through
///     <see cref="ChordTemplateResolver" /> with the chord's own arpeggio flavour.
/// </summary>
public interface IHandShapeGripSource
{
    /// <summary>
    ///     The template a bar opening on <paramref name="chord" /> carries. A template
    ///     that has to be MINTED is appended to <paramref name="minted" /> and NOT added
    ///     to the arrangement — the caller does that, inside its own undo node (editor)
    ///     or once the plan is applied (importer). The source must look in
    ///     <paramref name="minted" /> before minting, so one shape mints once.
    /// </summary>
    ChordTemplate GripFor(Chord chord, IList<ChordTemplate> minted);

    /// <summary>True when <paramref name="chord" /> is played with
    /// <paramref name="grip" />, so a repeated strum may stay under the same bar.</summary>
    bool Holds(ChordTemplate grip, Chord chord);
}

/// <summary>
///     One bar the planner proposes: its half-open span, the grip it carries and the
///     sounds it was built for (<see cref="Chords" />, in time order). A bar a later
/// detector proposes over single notes is the same record with no chords.
///     <para>
///         <see cref="StrikeChords" /> is the subset the Rocksmith convention trims to
///         a short strike once the bar carries the duration: every chord except one
///         whose technique is drawn along its sustain or that a slide lands on. Only
///         the tab importer applies it; the editor keeps authored sustains.
///     </para>
/// </summary>
public sealed record PlannedHandShape(
    FractionalPosition Position,
    FractionalPosition EndPosition,
    ChordTemplate Template,
    IReadOnlyList<Chord> Chords,
    IReadOnlyList<Chord> StrikeChords)
{
    public HandShape ToHandShape() => new()
    {
        Position = Position,
        EndPosition = EndPosition,
        TemplateId = Template.Id,
        Name = string.Empty
    };
}

/// <summary>Planner switches. The defaults are the tab importer's historical
/// behaviour.</summary>
/// <param name="SplitRunsOnRests">Break a repeated-strum run wherever a REST separates
/// two strums, instead of letting the 2-beat merge gap span it — see
/// <c>GpImportSettings.PreserveShortRests</c>.</param>
/// <param name="CoverEveryChord">Never plan a bar that leaves its own last chord
/// outside it. A chord with no sustain and no notated duration has an authored span of
/// zero, so its bar would end ON its onset and — the span being half-open — cover
/// nothing. The importer never meets one (every tab chord has a notated duration) and
/// keeps its exact output with this off; the editor turns it on, because a chord
/// drawn in the editor usually has no tail at all.</param>
/// <param name="KeepAnchorsOutside">Never put an existing FHP anchor strictly INSIDE a
/// planned bar: a run breaks at an anchor and a bar ends on the next one. An anchor
/// inside a hand shape is I20 (<c>AnchorRule</c>), so on a chart that already has
/// anchors a bar drawn across one would invent a finding. The importer plans before
/// it generates anchors and keeps this off.</param>
public readonly record struct ChordHandShapePlanOptions(
    bool SplitRunsOnRests = false,
    bool CoverEveryChord = false,
    bool KeepAnchorsOutside = false);

/// <summary>
///     THE hand-shape grouping rule, as a pure planner: walks a level's chords and
///     proposes one bar per run of the same grip, for every chord no hand shape covers
///     yet. It reads the level and never writes it — the caller applies the plan. The
///     tab importer (<c>Parsing/GuitarPro/ChordHandShapeBuilder</c>), the editor's
///     "Generate hand shapes (arrangement)" (<see cref="ArrangementHandShapeGenerator" />)
///     and the auto-fix for D11 (<c>ArrangementAutoFixService</c>) all call it, so the
///     three can never drift apart.
///     <list type="bullet">
///         <item>Each run starts on an uncovered chord and gets a bar over its AUTHORED
///         span (<see cref="AuthoredSpan" />).</item>
///         <item>Repeated strums of one grip share ONE bar while the next strum starts
///         within <see cref="MaxMergeGap" /> of the previous one's end, nothing else
///         sounds in between, and neither carries a technique drawn along its sustain
///         (Log Charter's rule — a slide changes pitch mid-shape and keeps its own bar).
///         A different grip starts a new bar.</item>
///         <item>A bar is clipped at the next SOUND after its run, and at the next hand
///         shape that already exists, so it never swallows a following strike and never
///         overlaps a bar someone placed.</item>
///     </list>
/// </summary>
public static class ChordHandShapePlanner
{
    /// <summary>Maximum gap (quarter-note beats) between one chord's authored end and
    /// the next chord's start for the two to share a hand shape — the Rocksmith
    /// charting convention for repeated strums (Log Charter uses the same 2-beat
    /// rule).</summary>
    public static readonly FractionalPosition MaxMergeGap = new(2);

    /// <summary>How far a <see cref="ChordHandShapePlanOptions.CoverEveryChord" /> bar
    /// reaches past a tail-less final chord when no later sound bounds it.</summary>
    private static readonly FractionalPosition OneBeat = new(1);

    /// <summary>
    ///     Plans the bars for every chord of <paramref name="level" /> that no hand shape
    ///     covers. Coverage is <see cref="HandShapeCoverageIndex" />'s predicate (the
    ///     half-open span, plus a zero-width shape covering its own start), so a chord
    ///     sitting ON a bar's end is uncovered and gets a bar of its own.
    /// </summary>
    /// <param name="level">The level to plan; read, never written.</param>
    /// <param name="grips">Which template a chord is played with.</param>
    /// <param name="minted">Receives every template the grip source mints. Pass the
    /// SAME list for every level of an arrangement, so a shape mints once.</param>
    /// <param name="options">The importer's behaviour by default.</param>
    public static List<PlannedHandShape> Plan(
        ChartLevel level,
        IHandShapeGripSource grips,
        IList<ChordTemplate> minted,
        ChordHandShapePlanOptions options = default)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(grips);
        ArgumentNullException.ThrowIfNull(minted);

        var plan = new List<PlannedHandShape>();
        var chords = new List<Chord>(level.Chords);
        chords.Sort((l, r) => l.Position.CompareTo(r.Position));

        // The idempotency question is asked once per chord and again per run
        // candidate, against a set this pass GROWS — a linear scan made one import
        // O(C^2). Same predicate, answered in O(log n).
        var coverage = new HandShapeCoverageIndex(level.HandShapes);
        var existingStarts = level.HandShapes.Select(h => h.Position).OrderBy(p => p).ToList();
        var notePositions = level.Notes.Select(n => n.Position).OrderBy(p => p).ToList();
        // A level's anchors are not kept sorted mid-session, hence the sorted copy.
        var anchorPositions = options.KeepAnchorsOutside
            ? level.Anchors.Select(a => a.Position).OrderBy(p => p).ToList()
            : [];
        var slideLandings = SlideLandings(level);

        for (var i = 0; i < chords.Count; i++)
        {
            var first = chords[i];
            if (first.ChordNotes.Count == 0) continue;
            // A chord split into notes ships as single notes, with no chord frame
            // (LinkLandingBreakup): a bar over it would give it a grip in the game,
            // and a pick scrape a chord box it must not have.
            if (first.SplitIntoNotes) continue;

            // Never a second bar for a chord already covered — re-import, import into
            // an existing chart and a second press of the editor verb must all add
            // nothing.
            if (coverage.Covers(first.Position)) continue;

            var template = grips.GripFor(first, minted);
            var run = new List<Chord> { first };

            // Repeated identical chords share ONE bar (the RS convention — inside it
            // the 3D highway renders repeats as small strums). The run extends while
            // the next chord plays the same grip, starts within the merge gap,
            // nothing else sounds in between, and neither side carries a technique
            // drawn along its sustain.
            if (!HasSustainVisibleTechnique(first))
            {
                while (i + 1 < chords.Count)
                {
                    var prev = run[^1];
                    var next = chords[i + 1];
                    if (next.ChordNotes.Count == 0 || next.SplitIntoNotes) break;
                    if (coverage.Covers(next.Position)) break;
                    if (HasSustainVisibleTechnique(next)) break;
                    if (!grips.Holds(template, next)) break;

                    var prevEnd = prev.Position + AuthoredSpan(prev);
                    // A REST between two strums ends the run: the bar carries
                    // duration, so drawing it across silence tells the player to hold
                    // through a rest the tab explicitly wrote.
                    if (options.SplitRunsOnRests && next.Position.CompareTo(prevEnd) > 0) break;
                    if (next.Position.CompareTo(prevEnd + MaxMergeGap) > 0) break;
                    if (FirstAfter(notePositions, prev.Position) is { } note && note.CompareTo(next.Position) < 0)
                        break;
                    // A bar someone already placed between the two strums (over
                    // nothing, or over notes this level does not have) must not end up
                    // inside the merged one.
                    if (FirstAfter(existingStarts, prev.Position) is { } bar && bar.CompareTo(next.Position) <= 0)
                        break;
                    // …nor an anchor (I20): the next strum opens a bar of its own.
                    if (FirstAfter(anchorPositions, prev.Position) is { } anchor && anchor.CompareTo(next.Position) <= 0)
                        break;

                    run.Add(next);
                    i++;
                }
            }

            var last = run[^1];
            var end = last.Position + AuthoredSpan(last);
            var nextChord = i + 1 < chords.Count ? chords[i + 1].Position : (FractionalPosition?)null;
            var nextNote = FirstAfter(notePositions, last.Position);
            var nextBar = FirstAfter(existingStarts, first.Position);
            var nextAnchor = FirstAfter(anchorPositions, first.Position);

            // Clip so the bar never extends past the next SOUND that starts after this
            // run — a following chord OR a melodic note. A technique chord keeps its
            // long authored sustain, so without this its bar swallows whatever strikes
            // during that sustain: a following chord is then suppressed and inherits
            // THIS shape's template (a bend chord {12,12,14} made the
            // plain {12,12} double-stops after it render and edit as the 3-note shape).
            // …and never past a bar that already exists: this pass only ADDS.
            var clip = end;
            if (nextChord is { } c && c.CompareTo(clip) < 0) clip = c;
            if (nextNote is { } n && n.CompareTo(clip) < 0) clip = n;
            if (nextBar is { } b && b.CompareTo(clip) < 0) clip = b;
            if (nextAnchor is { } a && a.CompareTo(clip) < 0) clip = a;
            if (clip.CompareTo(first.Position) > 0) end = clip;

            if (options.CoverEveryChord && end.CompareTo(last.Position) <= 0)
                end = Earliest(last.Position + OneBeat, FirstAfterAll(last.Position, nextChord, nextNote, nextBar, nextAnchor));

            var strikes = new List<Chord>(run.Count);
            foreach (var chord in run)
            {
                if (!HasSustainVisibleTechnique(chord) && !IsSlideLanding(slideLandings, chord))
                    strikes.Add(chord);
            }

            plan.Add(new PlannedHandShape(first.Position, end, template, run, strikes));
            coverage.Add(first.Position, end);
        }

        return plan;
    }

    /// <summary>
    ///     THE one place the trim exemption lives (chart-semantics — owners tune it). A
    ///     chord keeps its sustain when any of its notes carries a technique whose glyph
    ///     is only visible ACROSS the sustain: a bend, a tremolo, vibrato, or a slide
    ///     (pitched or unpitched / whammy). Hammer-on / pull-off / tap are visible
    ///     without a sustain and do NOT exempt. An exempt chord still gets its bar, but
    ///     never shares one.
    /// </summary>
    public static bool HasSustainVisibleTechnique(Chord chord)
    {
        foreach (var cn in chord.ChordNotes)
        {
            if (cn.BendValues.Count > 0 || cn.Tremolo || cn.Vibrato ||
                cn.SlideTo.HasValue || cn.UnpitchedSlide)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     The chord's authored length: its notated duration (quarter-note units) when
    ///     the source had one, else its longest sustain. On the import path this runs
    ///     before the sustains are trimmed, so both still say what was written.
    /// </summary>
    public static FractionalPosition AuthoredSpan(Chord chord)
    {
        if (chord.NotationDuration is { Numerator: > 0 } nd)
            return new FractionalPosition(0, nd);
        return chord.GetMaxSustain();
    }

    /// <summary>
    ///     A chord that a slide genuinely LANDS ON keeps ringing after the slide, so —
    ///     like a technique chord — it keeps its sustain instead of collapsing to a
    ///     strike. The landing is keyed by (position, string, target fret): matching by
    ///     position alone let a downward "slide out" that merely ENDS where an unrelated
    ///     chord begins preserve that chord's sustain (owner report on Test - Test.gp).
    /// </summary>
    private static HashSet<(FractionalPosition Pos, int Str, int Fret)> SlideLandings(ChartLevel level)
    {
        var landings = new HashSet<(FractionalPosition Pos, int Str, int Fret)>();
        foreach (var ch in level.Chords)
        {
            foreach (var cn in ch.ChordNotes)
            {
                if (cn.SlideTo is > 0)
                    landings.Add((ch.Position + cn.Sustain, cn.StringIndex, cn.SlideTo.Value));
            }
        }

        foreach (var n in level.Notes)
        {
            if (n.SlideTo is > 0)
                landings.Add((n.Position + n.Sustain, n.StringIndex, n.SlideTo.Value));
        }

        return landings;
    }

    private static bool IsSlideLanding(HashSet<(FractionalPosition Pos, int Str, int Fret)> landings, Chord chord)
    {
        foreach (var cn in chord.ChordNotes)
        {
            if (landings.Contains((chord.Position, cn.StringIndex, cn.Fret)))
                return true;
        }

        return false;
    }

    /// <summary>First position strictly after <paramref name="after" /> in a sorted
    /// list, or null when none.</summary>
    private static FractionalPosition? FirstAfter(List<FractionalPosition> sorted, FractionalPosition after)
    {
        int lo = 0, hi = sorted.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) >> 1;
            if (sorted[mid].CompareTo(after) <= 0) lo = mid + 1;
            else hi = mid;
        }

        return lo < sorted.Count ? sorted[lo] : null;
    }

    private static FractionalPosition? FirstAfterAll(FractionalPosition after, params FractionalPosition?[] candidates)
    {
        FractionalPosition? best = null;
        foreach (var candidate in candidates)
        {
            if (candidate is not { } p || p.CompareTo(after) <= 0) continue;
            if (best is not { } b || p.CompareTo(b) < 0) best = p;
        }

        return best;
    }

    private static FractionalPosition Earliest(FractionalPosition a, FractionalPosition? b) =>
        b is { } p && p.CompareTo(a) < 0 ? p : a;
}
