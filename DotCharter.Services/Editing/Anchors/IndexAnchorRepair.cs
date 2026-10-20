using DotCharter.Services.State;

namespace DotCharter.Services.Editing.Anchors;

/// <summary>
///     The two ways to end a disagreement between a grip's index finger and the
///     FHP anchor under it (rules E04 and I46): RE-FINGER the sound to a grip of
///     the same frets that agrees with the anchor, or MOVE the anchor to the fret
///     the fingering states (<see cref="IndexFret.Nearest" />).
///     <para>
/// <b>Re-fingering is the default when the arrangement attests it:</b>
///         the same frets fingered by another template whose index the anchor
///         allows, on a chord played under an anchor on that very fret. On the
///         verified corpus most E04 findings with such evidence were a chord
///         fingered 2-4 where the passage plays the same double stop 1-3 under
///         the same anchor, so the anchor was right and moving it would re-finger
///         every neighbour instead. With no evidence, moving the anchor is the
///         default; when both exist the other one is the alternative.
///     </para>
///     <para>
///         <b>Re-fingering never edits a template.</b> It rebinds the sounds onto
///         the attested template, which exists by construction and plays the same
///         frets, so no note moves and no other user of either template changes.
///         Each fix is ONE action, so one Ctrl+Z undoes it.
///     </para>
/// </summary>
public sealed class IndexAnchorRepair
{
    private IndexAnchorRepair(FretAnchor anchor, ChordTemplate template, int? moveAnchorTo, ChordTemplate? refinger,
        int evidence, List<Chord> chords, List<HandShape> handShapes)
    {
        Anchor = anchor;
        Template = template;
        MoveAnchorTo = moveAnchorTo;
        Refinger = refinger;
        Evidence = evidence;
        Chords = chords;
        HandShapes = handShapes;
    }

    /// <summary>The anchor the grip disagrees with.</summary>
    public FretAnchor Anchor { get; }

    /// <summary>The grip that disagrees.</summary>
    public ChordTemplate Template { get; }

    /// <summary>The nearest fret the fingering allows, or null when it states no index.</summary>
    public int? MoveAnchorTo { get; }

    /// <summary>The attested grip of the same frets that agrees with the anchor, or null.</summary>
    public ChordTemplate? Refinger { get; }

    /// <summary>How many chords play <see cref="Refinger" /> under an anchor on the same fret.</summary>
    public int Evidence { get; }

    /// <summary>The chords a re-fingering rebinds: the finding's chord, or every chord under the
    /// finding's hand shape that plays its grip.</summary>
    public IReadOnlyList<Chord> Chords { get; }

    /// <summary>The hand shapes over those chords that declare the same grip — a chord and its hand
    /// shape are one decision, so they are rebound together.</summary>
    public IReadOnlyList<HandShape> HandShapes { get; }

    /// <summary>True when re-fingering is the default fix.</summary>
    public bool PrefersRefinger => Refinger != null;

    /// <summary>
    ///     The repair for a finding on <paramref name="chord" /> or <paramref name="handShape" /> (the
    ///     finding's element) on <paramref name="level" /> under <paramref name="anchor" />, or null when
    ///     the grip agrees with the anchor or neither fix exists.
    /// </summary>
    public static IndexAnchorRepair? For(Arrangement arrangement, ChartLevel level, Chord? chord, HandShape? handShape,
        FretAnchor anchor)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(anchor);

        var templateId = chord?.TemplateId ?? handShape?.TemplateId ?? -1;
        var template = ChordTemplateResolver.FindById(arrangement, templateId);
        if (template == null) return null;

        var index = IndexFingerPosition.Find(template, arrangement.Capo);
        if (index is { } stated && stated.Allows(anchor.Fret)) return null;

        var (grip, evidence) = AgreeingGrip(arrangement, template, anchor.Fret);
        var move = index?.Nearest(anchor.Fret);
        if (grip == null && move == null) return null;

        var (chords, handShapes) = Sounds(level, chord, handShape, templateId);
        return new IndexAnchorRepair(anchor, template, move, grip, evidence, chords, handShapes);
    }

    /// <summary>
    ///     The template of <paramref name="shape" />'s frets (same arpeggio flavour, another id) whose
    ///     index an anchor on <paramref name="anchorFret" /> allows, played most often by a chord under an
    ///     anchor on that fret, with that count; ties go to the lowest id. (null, 0) when no chord attests one.
    /// </summary>
    public static (ChordTemplate? Grip, int Evidence) AgreeingGrip(Arrangement arrangement, ChordTemplate shape,
        int anchorFret)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        ArgumentNullException.ThrowIfNull(shape);

        var flavour = ArpeggioFlavour.Of(shape);
        Dictionary<int, ChordTemplate>? candidates = null;
        foreach (var t in arrangement.ChordTemplates)
        {
            if (t.Id < 0 || t.Id == shape.Id || !flavour.Matches(t) || !t.HasFrets(shape.Frets)) continue;
            if (IndexFingerPosition.Find(t, arrangement.Capo) is not { } index || !index.Allows(anchorFret)) continue;
            candidates ??= [];
            candidates.TryAdd(t.Id, t);
        }

        if (candidates == null) return (null, 0);

        var counts = new Dictionary<int, int>();
        foreach (var level in arrangement.Levels)
        {
            foreach (var chord in level.Chords)
            {
                if (!candidates.ContainsKey(chord.TemplateId)) continue;
                var governing = SoundCompanions.GoverningAnchor(chord.Position, level.Anchors);
                if (governing?.Fret != anchorFret) continue;
                counts[chord.TemplateId] = counts.GetValueOrDefault(chord.TemplateId) + 1;
            }
        }

        ChordTemplate? best = null;
        var bestCount = 0;
        foreach (var (id, count) in counts)
        {
            if (count < bestCount || (count == bestCount && best != null && id > best.Id)) continue;
            best = candidates[id];
            bestCount = count;
        }

        return (best, bestCount);
    }

    /// <summary>The default fix: <see cref="Refingering" /> when attested, else <see cref="AnchorMove" />.</summary>
    public IChartAction? Default(ChartLevel level) => PrefersRefinger ? Refingering(level) : AnchorMove(level);

    /// <summary>The other fix, or null when there is only one.</summary>
    public IChartAction? Alternative(ChartLevel level) => PrefersRefinger ? AnchorMove(level) : null;

    /// <summary>Rebinds <see cref="Chords" /> and <see cref="HandShapes" /> on <paramref name="level" /> onto
    /// <see cref="Refinger" /> as one action; null when no grip is attested.</summary>
    public IChartAction? Refingering(ChartLevel level)
    {
        ArgumentNullException.ThrowIfNull(level);
        if (Refinger is not { } grip) return null;
        var actions = new List<IChartAction>
        {
            new MutateChordsAction(level.Chords, Chords, Chords.Select(c => c.Clone()),
                Chords.Select(c => Rebound(c.Clone(), grip)))
        };
        if (HandShapes.Count > 0)
        {
            actions.Add(new MutateHandShapesAction(level.HandShapes, HandShapes, HandShapes.Select(h => h.Clone()),
                HandShapes.Select(h =>
                {
                    var clone = h.Clone();
                    clone.TemplateId = grip.Id;
                    return clone;
                })));
        }

        return actions.Count == 1 ? actions[0] : new CompositeAction(actions);

        static Chord Rebound(Chord chord, ChordTemplate template)
        {
            chord.TemplateId = template.Id;
            return chord;
        }
    }

    /// <summary>Moves <see cref="Anchor" /> to <see cref="MoveAnchorTo" /> as one action; null when the
    /// fingering states no index.</summary>
    public IChartAction? AnchorMove(ChartLevel level)
    {
        ArgumentNullException.ThrowIfNull(level);
        if (MoveAnchorTo is not { } fret) return null;
        var moved = Anchor.Clone();
        moved.Fret = fret;
        return new MutateAnchorsAction(level.Anchors, [Anchor], [Anchor.Clone()], [moved]);
    }

    /// <summary>
    ///     The sounds a re-fingering rebinds. Starting from a hand shape (I46), or from the hand shape
    ///     over the chord when it declares the chord's grip (E04): the shape plus every chord under it
    ///     on that grip, so the bar never describes a grip its strums do not play.
    /// </summary>
    private static (List<Chord> Chords, List<HandShape> HandShapes) Sounds(ChartLevel level, Chord? chord,
        HandShape? handShape, int templateId)
    {
        var shapes = new List<HandShape>();
        if (handShape != null)
        {
            shapes.Add(handShape);
        }
        else if (chord != null)
        {
            shapes.AddRange(SoundCompanions.CoveringHandShapes([chord.Position], level.HandShapes)
                .Where(h => h.TemplateId == templateId));
        }

        var chords = new List<Chord>();
        if (chord != null) chords.Add(chord);
        foreach (var c in level.Chords)
        {
            if (c.TemplateId != templateId || chords.Contains(c)) continue;
            if (shapes.Any(h => h.Covers(c.Position))) chords.Add(c);
        }

        return (chords, shapes);
    }
}
