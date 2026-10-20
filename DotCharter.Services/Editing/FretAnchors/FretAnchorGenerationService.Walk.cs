// The anchor walk: GenerateAnchors' per-position state and its three priority steps (P1, P2, P0/P3-P6).
namespace DotCharter.Services.Editing.FretAnchors;

public sealed partial class FretAnchorGenerationService
{
    /// <summary>The forward walk's state over one level's candidate positions.</summary>
    private sealed class AnchorWalk
    {
        /// <summary>
        ///     Slide destinations queued during the walk. Value is the signed
        ///     shift to apply against whatever the source anchor was at slide
        ///     emission time; we resolve it to an absolute root when we reach
        ///     the destination position.
        /// </summary>
        public Dictionary<FractionalPosition, int> PendingSlides { get; } = [];

        public Dictionary<FractionalPosition, AnchorHint> Hints { get; } = [];

        /// <summary>Slide landings (the slide MOVES the hand) and bass octave roots: no
        /// post-pass may drop or shift these anchors.</summary>
        public HashSet<FractionalPosition> Pinned { get; } = [];

        public AnchorHint? Current;

        /// <summary>
        ///     The OPEN GROUP the struck-note walk is accumulating: the position its
        ///     anchor was written to, and the highest fret actually PLAYED since it
        ///     opened (<see cref="GroupHigh" />). Both are needed to let the group's root
        ///     DESCEND retroactively (see TryDescendGroup). Null position = no descendable
        ///     group is open (a handshape lock or a slide destination owns the current anchor).
        /// </summary>
        public FractionalPosition? GroupPos;

        public int GroupHigh;

        /// <summary>The shape whose free double stop the hand plays from where it already
        /// is (CanHoldFreeShape); null when no shape is being held.</summary>
        public FractionalPosition? HeldShapeStart;
    }

    /// <summary>Walks every candidate position once, highest priority rule first.</summary>
    private AnchorWalk Walk(List<FractionalPosition> allPositions)
    {
        var walk = new AnchorWalk();
        foreach (var position in allPositions)
        {
            if (FindActiveHandShape(position) is { } shape)
                StepHandShape(walk, position, shape);
            else if (walk.PendingSlides.TryGetValue(position, out var forcedRoot))
                StepSlideDestination(walk, position, forcedRoot);
            else
                StepStruck(walk, position);
        }

        return walk;
    }

    /// <summary>P1: Handshape lock.</summary>
    private void StepHandShape(AnchorWalk walk, FractionalPosition position,
        (FractionalPosition Start, FractionalPosition End) shape)
    {
        var isStart = position == shape.Start;

        // If shape has no precomputed root (all-open-string chord,
        // for instance), inherit current anchor — don't force fret=1.
        var shapeTrace = _handShapeRootTrace != null &&
                         _handShapeRootTrace.TryGetValue(shape.Start, out var st)
            ? st
            : new AnchorRuleTrace(AnchorRule.P1HandShapeInferred, RootSource.Unknown);
        if (!_handShapeStartRoot.TryGetValue(shape.Start, out var shapeRoot))
        {
            var inferred = ComputeRequiredRoot(GetFrettedSoundsAt(position));
            if (inferred.HasValue)
            {
                shapeRoot = inferred.Value;
                shapeTrace = new AnchorRuleTrace(AnchorRule.P1HandShapeInferred, _lastRootSource);
            }
            else if (walk.Current.HasValue)
            {
                // Inherit — no emit, no change.
                if (isStart) SeedOutboundSlides(position, walk.Current.Value, walk.PendingSlides);
                return;
            }
            else
            {
                shapeRoot = MinAnchorFret;
                shapeTrace = new AnchorRuleTrace(AnchorRule.P1HandShapeInferred, RootSource.Floor);
            }
        }

        var shapeHint = new AnchorHint(Math.Clamp(shapeRoot, MinAnchorFret, MaxFret),
            ComputeHandShapeWidth(shape, shapeRoot));

        // A held shape keeps the anchor in force for its whole length.
        if (walk.HeldShapeStart == shape.Start && walk.Current.HasValue)
        {
            walk.GroupPos = null;
            SeedOutboundSlides(position, walk.Current.Value, walk.PendingSlides);
            return;
        }

        if (isStart && CanHoldFreeShape(shape, walk.Current, shapeHint))
        {
            walk.HeldShapeStart = shape.Start;
            RuleTrace?[position] = new AnchorRuleTrace(AnchorRule.P1HeldFreeChord, RootSource.Held);
            walk.GroupPos = null;
            SeedOutboundSlides(position, walk.Current!.Value, walk.PendingSlides);
            return;
        }

        if (isStart && walk.Current != shapeHint)
        {
            walk.Hints[position] = shapeHint;
            RuleTrace?[position] = shapeTrace;
        }

        walk.Current = shapeHint;

        // A locked shape owns its root; the walk may not lower it later.
        walk.GroupPos = null;

        // Slide sources inside a handshape still seed destinations.
        SeedOutboundSlides(position, shapeHint, walk.PendingSlides);
    }

    /// <summary>P2: Slide destination.</summary>
    private void StepSlideDestination(AnchorWalk walk, FractionalPosition position, int forcedRoot)
    {
        var destRoot = Math.Clamp(forcedRoot, MinAnchorFret, MaxFret);
        var destHint = new AnchorHint(destRoot, ComputeBaseWidth(destRoot, position));
        if (walk.Current != destHint)
        {
            walk.Hints[position] = destHint;
            walk.Pinned.Add(position);
            RuleTrace?[position] = new AnchorRuleTrace(AnchorRule.P2SlideDestination, RootSource.Unknown);
        }

        walk.Current = destHint;

        // The slide LANDS the hand here; the landing anchor opens its
        // own group so a following low note can still pull it down.
        walk.GroupPos = walk.Hints.ContainsKey(position) ? position : null;
        walk.GroupHigh = GroupHighOf(destHint, GetFrettedSoundsAt(position));

        SeedOutboundSlides(position, destHint, walk.PendingSlides);
    }

    /// <summary>P3–P5: Struck-note resolution, with the bass octave root (P0) and abandonment (P6).</summary>
    private void StepStruck(AnchorWalk walk, FractionalPosition position)
    {
        var struck = GetFrettedSoundsAt(position);
        if (struck.Count == 0)
            // Bare position (reached only via slide destination or user
            // range). Already covered by P2 when applicable.
            return;

        var need = ComputeAnchorForStruck(struck, position);
        var needSource = _lastRootSource;

        // P0 (bass): an octave figure's root takes the index finger,
        // so the anchor moves ONTO it here even when the hand in force
        // already reaches it — and it is planted, never a group descend.
        var octaveRoot = struck.Count == 1 && _octaveStarts.Contains(position) && need.AnchorFret == struck[0].Fret;

        // Hold the current anchor only if it covers this position AND
        // the required root matches. Exact-root match guards against
        // letting a [5..8] window absorb a finger-1-on-6 chord.
        if (!AnchorCovers(walk.Current, need, struck, position) ||
            octaveRoot && walk.Current!.Value.AnchorFret != need.AnchorFret)
        {
            // The hand may still be planted where it was — this note just
            // reaches BELOW the anchor's base. A charter answers that by
            // moving the anchor of the group ALREADY IN PROGRESS down, not
            // by planting a second one here (see TryDescendGroup).
            if (!octaveRoot && TryDescendGroup(walk.Hints, walk.GroupPos, position, ref walk.Current,
                    ref walk.GroupHigh, need, struck))
            {
                SeedOutboundSlides(position, walk.Current!.Value, walk.PendingSlides);
                return;
            }

            walk.Hints[position] = need;
            RuleTrace?[position] = octaveRoot
                ? new AnchorRuleTrace(AnchorRule.P0OctaveRoot, RootSource.LowestFret)
                : new AnchorRuleTrace(
                    needSource == RootSource.RunMin ? AnchorRule.P3RunMin : AnchorRule.P4P5StruckWalk,
                    needSource);
            walk.Current = need;
            // An octave root's anchor is final: no later note descends it,
            // and the post-passes may not drop or move it (pinned).
            walk.GroupPos = octaveRoot ? null : position;
            walk.GroupHigh = GroupHighOf(need, struck);
            if (octaveRoot) walk.Pinned.Add(position);
        }
        else if (ComputeHardRoot(struck) is null &&
                 AbandonmentRoot(position, walk.Current!.Value.AnchorFret) is { } abandonRoot)
        {
            // The window [root, root+3] still fits this note, so the plain
            // walk would HOLD — but the fret hand has demonstrably left the
            // low position (every one of the next N struck notes
            // plays above the current anchor), so re-plant the box upward
            // onto the frets actually in use. Without this the anchor sticks
            // at the low-water mark of a phrase that has climbed and
            // stabilised — the dominant bass FHP error, +18pp on the EOF
            // corpus. Guitar looks much further ahead (HandAbandonment.GuitarHold),
            // so an arpeggio that comes back down within a phrase keeps its
            // one anchor. An explicit finger-1/barre root (ComputeHardRoot)
            // still wins, so authored fingering is never overridden.
            var abandonHint = new AnchorHint(abandonRoot, BaseAnchorWidth);
            walk.Hints[position] = abandonHint;
            RuleTrace?[position] = new AnchorRuleTrace(AnchorRule.P6Abandonment, RootSource.LowestFret);
            walk.Current = abandonHint;
            walk.GroupPos = position;
            walk.GroupHigh = GroupHighOf(abandonHint, struck);
        }
        else
        {
            // The anchor held — this note joins the open group, so it raises
            // the group's played ceiling for any later descend.
            if (walk.GroupPos.HasValue)
                walk.GroupHigh = Math.Max(walk.GroupHigh, GroupHighOf(walk.Current!.Value, struck));
        }

        SeedOutboundSlides(position, walk.Current!.Value, walk.PendingSlides);
    }
}
