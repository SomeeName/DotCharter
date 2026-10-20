using DotCharter.Services.Editing.Fixing;

namespace DotCharter.Services.Editing.Fingering;

/// <summary>
/// How ONE arrangement fingers the two-string grips the song chooses:
///     the power chord and the two-fret octave, each <c>1·3</c> or <c>1·4</c>
///     (<see cref="TwoFretPairFingering" />). Read from the fingerings the
///     arrangement already carries — the majority of its templates of that pair
///     that are fingered one of the two ways — so there is no setting: the chart
///     already says it. A pair with no clear majority (none fingered, or a tie)
///     keeps the suggester's own choice.
///     <para>
///         <b>Why this lives here and not in the suggester.</b>
///         <c>ChordTemplateFingerSetter</c> is a pure function of the fret shape
///         (the Core rule). The style is an INPUT to it
///         (<see cref="HintFor" />), resolved per arrangement by every automatic
///         fingering: a template minted by an edit
///         (<c>ChordTemplateResolver</c>), Refret, a tab import
///         (<c>ImportFhpPass</c>), the FHP follower's free-grip test
///         (<c>ChordFingeringFollower.FreeGripResolver</c>), the chord panel's
///         Generate fingerings and display seed, and a resized hand-shape bar
///         (<see cref="RebuildHandShapeGrip" />).
///     </para>
///     <para>
///         Measured with <c>tools/chart-eval --style</c> on the owner's verified
///         charts, never letting a template's own answer into its hint: chord
///         fingering after FHP on held-out songs 85.7% → 88.6% (hint from the
///         shapes played earlier) / 89.7% (from every other shape). The protocol
///         and the rejected variants: <c>docs/agents/core/guitar-and-tuning.md</c>.
///     </para>
/// </summary>
/// <param name="PowerChord">The arrangement's power-chord fingering, or null.</param>
/// <param name="TwoFretOctave">The arrangement's two-fret octave fingering, or null.</param>
public sealed record ArrangementFingeringStyle(PairFingering? PowerChord, PairFingering? TwoFretOctave)
{
    /// <summary>No style: every pair takes the suggester's own fingering.</summary>
    public static ArrangementFingeringStyle None { get; } = new(null, null);

    /// <summary>The style <paramref name="arrangement" />'s own templates state.</summary>
    public static ArrangementFingeringStyle Of(Arrangement arrangement)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        return From(arrangement, arrangement.ChordTemplates);
    }

    /// <summary>
    ///     The style <paramref name="fingered" /> state, classified in
    ///     <paramref name="arrangement" />'s tuning. Each template is one vote,
    ///     whatever it is used by: a song plays a style, not a chord count.
    /// </summary>
    public static ArrangementFingeringStyle From(Arrangement arrangement, IEnumerable<ChordTemplate> fingered)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        ArgumentNullException.ThrowIfNull(fingered);
        Span<int> votes = stackalloc int[4]; // [pair * 2 + fingering]
        foreach (var template in fingered)
        {
            if (TwoFretPairFingering.Classify(arrangement, template, out var low, out var high) is not { } pair) continue;
            if (TwoFretPairFingering.Current(template, low, high) is not { } fingering) continue;
            votes[(int)pair * 2 + (int)fingering]++;
        }

        return new ArrangementFingeringStyle(
            Majority(votes[(int)TwoFretPair.PowerChord * 2], votes[(int)TwoFretPair.PowerChord * 2 + 1]),
            Majority(votes[(int)TwoFretPair.TwoFretOctave * 2], votes[(int)TwoFretPair.TwoFretOctave * 2 + 1]));
    }

    /// <summary>
    ///     The clear majority of two vote counts, or null on a tie (none at all
    ///     included).
    /// </summary>
    public static PairFingering? Majority(int indexPinky, int indexRing) =>
        indexPinky > indexRing ? PairFingering.IndexPinky
        : indexRing > indexPinky ? PairFingering.IndexRing
        : null;

    /// <summary>The style's fingering for <paramref name="pair" />, or null.</summary>
    public PairFingering? For(TwoFretPair pair) => pair == TwoFretPair.PowerChord ? PowerChord : TwoFretOctave;

    /// <summary>
    ///     The hint to hand <c>ChordTemplateFingerSetter</c> for
    ///     <paramref name="template" />: this style's fingering when the template
    ///     is a power chord or a two-fret octave in <paramref name="arrangement" />'s
    ///     tuning, else null.
    /// </summary>
    public PairFingering? HintFor(Arrangement arrangement, ChordTemplate template) =>
        TwoFretPairFingering.Classify(arrangement, template, out _, out _) is { } pair ? For(pair) : null;

    /// <summary>
    ///     The hint for every template of <paramref name="arrangement" />, each
    ///     from the arrangement's OTHER templates: a template never votes for
    ///     itself, or one authored <c>1·4</c> power chord alone in its song would
    ///     read as the suggestion and lose its "authored" pin in the FHP walk. The
    ///     votes are counted once; a template outside the arrangement sees them
    ///     all. Reads the templates as they are now (callers memoise per pass).
    /// </summary>
    public static Func<ChordTemplate, PairFingering?> HintsFromOthers(Arrangement arrangement)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        var votes = new int[4]; // [pair * 2 + fingering]
        var members = new HashSet<ChordTemplate>(ReferenceEqualityComparer.Instance);
        foreach (var template in arrangement.ChordTemplates)
        {
            members.Add(template);
            if (TwoFretPairFingering.Classify(arrangement, template, out var low, out var high) is not { } pair) continue;
            if (TwoFretPairFingering.Current(template, low, high) is not { } fingering) continue;
            votes[(int)pair * 2 + (int)fingering]++;
        }

        return template =>
        {
            if (TwoFretPairFingering.Classify(arrangement, template, out var low, out var high) is not { } pair) return null;
            var pinky = votes[(int)pair * 2 + (int)PairFingering.IndexPinky];
            var ring = votes[(int)pair * 2 + (int)PairFingering.IndexRing];
            if (members.Contains(template))
            {
                var own = TwoFretPairFingering.Current(template, low, high);
                if (own == PairFingering.IndexPinky) pinky--;
                else if (own == PairFingering.IndexRing) ring--;
            }

            return Majority(pinky, ring);
        };
    }

    /// <summary>
    ///     A tab import's pass (no undo, so templates are re-fingered in place):
    ///     the style the AUTHORED templates state (a Guitar Pro file's own finger
    ///     marks) is applied to every other power chord and two-fret octave that
    ///     still carries the suggester's plain fingering. Nothing authored, or no
    ///     clear majority: nothing changes. Returns how many templates changed.
    /// </summary>
    public static int FollowAuthored(Arrangement arrangement, Func<ChordTemplate, bool> isAuthored)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        ArgumentNullException.ThrowIfNull(isAuthored);
        var style = From(arrangement, arrangement.ChordTemplates.Where(isAuthored));
        if (style == None) return 0;

        var changed = 0;
        foreach (var template in arrangement.ChordTemplates)
        {
            // A pinned grip is authored by definition.
            if (isAuthored(template) || template.IsPinned) continue;
            if (TwoFretPairFingering.Classify(arrangement, template, out var low, out var high) is not { } pair) continue;
            if (style.For(pair) is not { } fingering) continue;
            if (TwoFretPairFingering.Current(template, low, high) != TwoFretPairFingering.SuggesterDefault(pair)) continue;
            if (fingering == TwoFretPairFingering.SuggesterDefault(pair)) continue;
            template.Fingers = TwoFretPairFingering.FingersFor(fingering, low, high);
            changed++;
        }

        return changed;
    }

    /// <summary>
    ///     Fingers <paramref name="template" /> as the suggester would, following
    ///     <paramref name="arrangement" />'s own style for a power chord or a
    ///     two-fret octave. The style is only read when the template IS one of
    ///     them, so minting any other shape costs nothing extra.
    /// </summary>
    public static void SetSuggestedFingers(Arrangement arrangement, ChordTemplate template, int stringCount)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        ArgumentNullException.ThrowIfNull(template);
        // A pinned grip keeps the fingers it was authored with.
        if (template.IsPinned) return;
        var hint = TwoFretPairFingering.Classify(arrangement, template, out _, out _) != null
            ? HintsFromOthers(arrangement)(template)
            : null;
        ChordTemplateFingerSetter.SetSuggestedFingers(template, Math.Max(1, stringCount), hint);
    }

    /// <summary>
    ///     <see cref="HandShapeAutoTemplate.Rebuild" /> for a hand-shape bar resized over
    ///     <paramref name="arrangement" />'s notes, its power chord or two-fret octave fingered
    ///     in the arrangement's style. <paramref name="current" />, the grip being replaced,
    ///     does not vote: it is the bar's own old answer.
    /// </summary>
    public static ChordTemplate? RebuildHandShapeGrip(
        Arrangement arrangement, ChordTemplate? current, ChordTemplate? seed, int stringCount)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        return HandShapeAutoTemplate.Rebuild(current, seed, stringCount, grip =>
            From(arrangement, arrangement.ChordTemplates.Where(t => !ReferenceEquals(t, current)))
                .HintFor(arrangement, grip));
    }
}
