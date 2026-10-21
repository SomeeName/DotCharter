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
/// <param name="ContinueThroughGripTones">Let a repeated-strum run continue across
/// single notes the grip itself plays: every note between the two strums is a
/// fretted (string, fret) of the run's template, or an open string the template does
/// not fret, and none carries a technique drawn along its sustain. The hand never
/// leaves the grip, so the charters keep one bar over strum, arpeggio, strum. Any
/// other note still breaks the run, and a rest is measured from the last grip tone's
/// end. Off everywhere: on the tab-vs-chart corpus it moved neither FHP nor measure-link
/// recall, so no caller turns it on yet.</param>
public readonly record struct ChordHandShapePlanOptions(
    bool SplitRunsOnRests = false,
    bool CoverEveryChord = false,
    bool KeepAnchorsOutside = false,
    bool ContinueThroughGripTones = false);

/// <summary>Why a repeated-strum run ended before the next chord: the telemetry
/// <see cref="ChordHandShapePlanner.Plan" /> counts when asked.</summary>
public enum HandShapeRunBreak
{
    /// <summary>The next chord is played with another grip.</summary>
    OtherGrip,

    /// <summary>A chord of the pair carries a technique drawn along its sustain.</summary>
    Technique,

    /// <summary>A rest separates the two (<see cref="ChordHandShapePlanOptions.SplitRunsOnRests" />).</summary>
    Rest,

    /// <summary>The next chord starts more than <see cref="ChordHandShapePlanner.MaxMergeGap" /> after the sound before it ends.</summary>
    Gap,

    /// <summary>A note the grip does not hold sounds in between.</summary>
    ForeignSound,

    /// <summary>An existing hand shape or anchor in between, or a next chord already covered or split into notes.</summary>
    Existing
}

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
    /// <param name="breaks">When given, counts why each run ended before a following
    /// chord, one per boundary.</param>
    public static List<PlannedHandShape> Plan(
        ChartLevel level,
        IHandShapeGripSource grips,
        IList<ChordTemplate> minted,
        ChordHandShapePlanOptions options = default,
        IDictionary<HandShapeRunBreak, int>? breaks = null)
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
        var sortedNotes = level.Notes.OrderBy(n => n.Position).ToList();
        var notePositions = sortedNotes.Select(n => n.Position).ToList();
        // A level's anchors are not kept sorted mid-session, hence the sorted copy.
        var anchorPositions = options.KeepAnchorsOutside
            ? level.Anchors.Select(a => a.Position).OrderBy(p => p).ToList()
            : [];
        var slideLandings = SlideLandings(level);
        var context = new RunContext(grips, coverage, sortedNotes, notePositions, existingStarts, anchorPositions, options);

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

            HandShapeRunBreak? reason = null;
            if (HasSustainVisibleTechnique(first))
            {
                reason = HandShapeRunBreak.Technique;
            }
            else
            {
                while (i + 1 < chords.Count)
                {
                    reason = NextChordBreak(run[^1], chords[i + 1], template, context);
                    if (reason != null) break;
                    run.Add(chords[i + 1]);
                    i++;
                }
            }

            if (breaks != null && reason is { } why && i + 1 < chords.Count)
                breaks[why] = breaks.TryGetValue(why, out var count) ? count + 1 : 1;

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
            var clip = Earliest(Earliest(Earliest(Earliest(end, nextChord), nextNote), nextBar), nextAnchor);
            if (clip.CompareTo(first.Position) > 0) end = clip;

            if (options.CoverEveryChord && end.CompareTo(last.Position) <= 0)
                end = Earliest(last.Position + OneBeat, FirstAfterAll(last.Position, nextChord, nextNote, nextBar, nextAnchor));

            plan.Add(new PlannedHandShape(first.Position, end, template, run, StrikeChords(run, slideLandings)));
            coverage.Add(first.Position, end);
        }

        return plan;
    }

    // Why the run on `grip` cannot take `next` after `prev`; null when it can.
    // Repeated identical chords share ONE bar (the RS convention: inside it the 3D
    // highway renders repeats as small strums) while the next chord plays the same
    // grip, starts within the merge gap, nothing else sounds in between and neither
    // side carries a technique drawn along its sustain.
    private static HandShapeRunBreak? NextChordBreak(Chord prev, Chord next, ChordTemplate grip, RunContext context)
    {
        if (next.ChordNotes.Count == 0 || next.SplitIntoNotes) return HandShapeRunBreak.Existing;
        if (context.Coverage.Covers(next.Position)) return HandShapeRunBreak.Existing;
        if (HasSustainVisibleTechnique(next)) return HandShapeRunBreak.Technique;
        if (!context.Grips.Holds(grip, next)) return HandShapeRunBreak.OtherGrip;

        var prevEnd = prev.Position + AuthoredSpan(prev);
        if (FirstAfter(context.NotePositions, prev.Position) is { } note && note.CompareTo(next.Position) < 0)
        {
            if (!context.Options.ContinueThroughGripTones ||
                GripTonesEnd(context.SortedNotes, context.NotePositions, prev.Position, next.Position, grip) is not { } tonesEnd)
            {
                return HandShapeRunBreak.ForeignSound;
            }

            if (tonesEnd.CompareTo(prevEnd) > 0) prevEnd = tonesEnd;
        }

        // A REST between two strums ends the run: the bar carries duration, so
        // drawing it across silence tells the player to hold through a rest the tab
        // explicitly wrote.
        if (context.Options.SplitRunsOnRests && next.Position.CompareTo(prevEnd) > 0) return HandShapeRunBreak.Rest;
        if (next.Position.CompareTo(prevEnd + MaxMergeGap) > 0) return HandShapeRunBreak.Gap;
        // A bar someone already placed between the two strums (over nothing, or over
        // notes this level does not have) must not end up inside the merged one...
        if (FirstAfter(context.ExistingStarts, prev.Position) is { } bar && bar.CompareTo(next.Position) <= 0)
            return HandShapeRunBreak.Existing;
        // ...nor an anchor (I20): the next strum opens a bar of its own.
        if (FirstAfter(context.AnchorPositions, prev.Position) is { } anchor && anchor.CompareTo(next.Position) <= 0)
            return HandShapeRunBreak.Existing;
        return null;
    }

    /// <summary>What <see cref="NextChordBreak" /> reads of one level's plan.</summary>
    private sealed record RunContext(
        IHandShapeGripSource Grips,
        HandShapeCoverageIndex Coverage,
        List<GuitarNote> SortedNotes,
        List<FractionalPosition> NotePositions,
        List<FractionalPosition> ExistingStarts,
        List<FractionalPosition> AnchorPositions,
        ChordHandShapePlanOptions Options);

    /// <summary>
    ///     When every note strictly between <paramref name="after" /> and
    ///     <paramref name="before" /> is a GRIP TONE of <paramref name="grip" /> (a
    ///     fretted string and fret of the grip, or an open string the grip does not
    ///     fret, with no technique drawn along its sustain), the latest end of those
    ///     notes: onset plus notated length, else sustain. Otherwise null.
    /// </summary>
    private static FractionalPosition? GripTonesEnd(List<GuitarNote> sortedNotes, List<FractionalPosition> positions,
        FractionalPosition after, FractionalPosition before, ChordTemplate grip)
    {
        int lo = 0, hi = positions.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) >> 1;
            if (positions[mid].CompareTo(after) <= 0) lo = mid + 1;
            else hi = mid;
        }

        FractionalPosition? end = null;
        for (var k = lo; k < sortedNotes.Count && sortedNotes[k].Position.CompareTo(before) < 0; k++)
        {
            var note = sortedNotes[k];
            if (note.BendValues.Count > 0 || note.Tremolo || note.Vibrato || note.SlideTo.HasValue || note.UnpitchedSlide)
                return null;
            var held = grip.Frets.TryGetValue(note.StringIndex, out var fret) ? fret == note.Fret : note.Fret == 0;
            if (!held) return null;

            var span = note.NotationDuration is { Numerator: > 0 } nd ? new FractionalPosition(0, nd) : note.Sustain;
            var noteEnd = note.Position + span;
            if (end is not { } e || noteEnd.CompareTo(e) > 0) end = noteEnd;
        }

        return end;
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

    // The run's chords the importer trims to a short strike: all but a technique
    // chord or a slide's landing (PlannedHandShape.StrikeChords).
    private static List<Chord> StrikeChords(List<Chord> run, HashSet<(FractionalPosition Pos, int Str, int Fret)> slideLandings)
    {
        var strikes = new List<Chord>(run.Count);
        foreach (var chord in run)
        {
            if (!HasSustainVisibleTechnique(chord) && !IsSlideLanding(slideLandings, chord))
                strikes.Add(chord);
        }

        return strikes;
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
