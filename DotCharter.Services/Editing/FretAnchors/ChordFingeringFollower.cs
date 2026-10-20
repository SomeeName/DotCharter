using System.Runtime.CompilerServices;
using DotCharter.Services.Editing.Anchors;
using DotCharter.Services.Editing.Fingering;

namespace DotCharter.Services.Editing.FretAnchors;

/// <summary>
/// Makes a chord's FREE fingering follow the anchor the walk chose.
///     <para>
///         The walk (<see cref="FretAnchorGenerationService" /> with a
///         <c>freeGrip</c> resolver) may keep the hand where it already is for a
///         chord whose fingering nobody authored — a double stop G9+B9 under FHP 8
///         is played with the higher fingers, not by moving the index onto fret 9.
///         That chord's template still says index-on-9, which the game would
///         contradict (it derives the finger from the anchor) and inspector rule
///         E04 reports. This pass resolves, per chord, its BASE grip (the
///         suggester's fingering for its frets) SHIFTED up the hand by the
///         distance between the grip's index fret and the governing anchor —
///         shift 0 being the base grip itself, so a chord a later pass does not
///         need to shift returns to it.
///     </para>
///     <para>
///         <b>A <see cref="ChordTemplate" /> is shared, so it is never mutated.</b>
///         The variant is resolved as its own template through
///         <see cref="IFingeringGenerationService.FindIdenticalTemplate" /> (frets,
///         fingers, name and arpeggio flavour) and minted only when nothing
///         matches; the chord and the hand shapes over it are repointed. The
///         <see cref="FingeringFollowPlan" /> is a description, so an undoable
///         caller can put the minted templates in the same history node as the
///         anchors.
///     </para>
///     <para>
///         <c>ChordTemplateFingerSetter</c> stays a pure function of the shape (the
///         Core rule): the context lives here, in Services.
///     </para>
/// </summary>
public static class ChordFingeringFollower
{
    /// <summary>
    ///     How far <paramref name="template" />'s fingering is the context-free
    ///     suggestion for its frets moved UP the hand: <c>0</c> when it is the
    ///     suggestion itself, <c>k</c> when every finger on a fretted string above
    ///     the capo is the suggested one plus the same <c>k</c>, null when it is
    ///     anything else. A template with no fingering gives null: there is
    ///     nothing to follow. <paramref name="twoFretPair" /> is the arrangement's
    ///     style for a power chord or two-fret octave
    /// (<see cref="ArrangementFingeringStyle.HintFor" />).
    /// </summary>
    public static int? SuggestedShift(ChordTemplate template, int stringCount, int capo = 0,
        PairFingering? twoFretPair = null)
    {
        if (template.Fingers.Count == 0 || template.Frets.Count == 0) return null;
        var grip = SuggestedGrip(template, stringCount, twoFretPair);
        if (grip.Fingers.Count != template.Fingers.Count) return null;

        int? shift = null;
        var pinned = false;
        foreach (var (stringIndex, finger) in template.Fingers)
        {
            if (!grip.Fingers.TryGetValue(stringIndex, out var suggested)) return null;
            var delta = finger - suggested;
            // A string at or below the capo, or one the suggestion does not put
            // a finger on, never moves with the hand (ResolveVariant skips it).
            if (!template.Frets.TryGetValue(stringIndex, out var fret) || fret <= capo || suggested < 1)
            {
                if (delta != 0) return null;
                pinned |= fret > capo;
                continue;
            }

            if (shift == null) shift = delta;
            else if (shift != delta) return null;
        }

        return shift switch
        {
            null or 0 => 0,
            < 0 => null,
            // A grip with an unfingered fretted string is never shifted.
            _ => pinned ? null : shift
        };
    }

    /// <summary>
    ///     True when <paramref name="template" /> is a variant <see cref="Plan" />
    ///     minted: <see cref="ChordTemplate.FollowedFingering" />. A grip shifted up
    ///     the hand is free only then — charters finger a double stop 2-2 or 3-4 all
    ///     the time, and reading every uniform shift of the suggestion as free
    ///     re-fingered 3,187 of their chords on the corpus (`--fhp-fingers
    ///     reference`, train) and cost 3.7 / 1.1 pp of guitar FHP (train /
    ///     holdout). The flag is persisted in the <c>.rscp</c> and copied by
    ///     <c>Clone</c>, so it survives a reopen and a <c>ChartProjectCloner</c>
    /// snapshot alike.
    /// </summary>
    public static bool IsMintedVariant(ChordTemplate template) => template.FollowedFingering;

    /// <summary>
    ///     True when <paramref name="template" />'s fingering is FREE: exactly the
    ///     suggestion, or a variant this pass minted from it
    ///     (<see cref="IsMintedVariant" />) — which may shift again, or back. Any
    ///     other fingering, a uniform shift of the suggestion included, is
    ///     AUTHORED.
    /// </summary>
    public static bool IsFreeFingering(ChordTemplate template, int stringCount, int capo = 0,
        PairFingering? twoFretPair = null) =>
        SuggestedShift(template, stringCount, capo, twoFretPair) switch
        {
            0 => true,
            > 0 => IsMintedVariant(template),
            _ => false
        };

    /// <summary>
    ///     A memoising resolver for one arrangement: a FREE template's BASE grip
    ///     (its frets with the suggested fingering, a probe never added anywhere),
    ///     or null for an AUTHORED one. The walk reads a free chord through its
    ///     base grip, so a variant an earlier pass minted never pins its anchor;
    ///     this pass shifts from it.
    /// </summary>
    /// <param name="arrangement">Supplies the string count and the capo.</param>
    /// <param name="isAuthored">
    ///     Templates the caller KNOWS were authored even when their fingering
    ///     equals the suggestion — a Guitar Pro import's per-note fingers
    ///     (<c>GpAuthoredFingering.IsAuthoredTemplate</c>). Null: only the
    ///     fingering decides.
    /// </param>
    /// <param name="twoFretPair">
    /// The style hint per template: what the suggestion is for a power
    ///     chord or a two-fret octave. Null: the arrangement's own style, each
    ///     template's from the OTHER templates
    ///     (<see cref="ArrangementFingeringStyle.HintsFromOthers" />).
    /// </param>
    public static Func<ChordTemplate, ChordTemplate?> FreeGripResolver(
        Arrangement arrangement,
        Func<ChordTemplate, bool>? isAuthored = null,
        Func<ChordTemplate, PairFingering?>? twoFretPair = null)
    {
        var strings = arrangement.Tuning.Length > 0 ? arrangement.Tuning.Length : arrangement.StringCount;
        strings = Math.Max(1, strings);
        var capo = Math.Max(0, arrangement.Capo);
        twoFretPair ??= ArrangementFingeringStyle.HintsFromOthers(arrangement);
        var cache = new Dictionary<ChordTemplate, ChordTemplate?>(ReferenceEqualityComparer.Instance);
        return t =>
        {
            if (!cache.TryGetValue(t, out var grip))
            {
                var hint = twoFretPair(t);
                // A pinned grip is authored whatever its fingers say.
                grip = !t.IsPinned && isAuthored?.Invoke(t) != true && IsFreeFingering(t, strings, capo, hint)
                    ? SuggestedGrip(t, strings, hint)
                    : null;
                cache[t] = grip;
            }

            return grip;
        };
    }

    /// <summary>
    ///     Plans the re-fingering of every free chord in <paramref name="level" />
    ///     onto its base grip shifted up by as far as the grip's index fret sits
    ///     ABOVE the anchor governing it (as much as the grip has spare fingers),
    ///     or back onto the base grip when the anchor does not sit below it.
    ///     Nothing is changed; apply the plan.
    /// </summary>
    /// <param name="arrangement">Owns the templates (and the capo); never modified.</param>
    /// <param name="level">The level whose chords are judged; never modified.</param>
    /// <param name="anchors">The anchors the walk generated for <paramref name="level" />.</param>
    /// <param name="freeGrip">The same resolver the walk was given (<see cref="FreeGripResolver" />).</param>
    /// <param name="fingering">Resolves an identical existing template and the next free id.</param>
    /// <param name="pending">
    ///     Templates an earlier plan of the SAME edit minted but nobody has added
    ///     yet (one plan per difficulty level): reused when identical, and their
    ///     ids are never handed out again.
    /// </param>
    public static FingeringFollowPlan Plan(
        Arrangement arrangement,
        ChartLevel level,
        IReadOnlyList<FretAnchor> anchors,
        Func<ChordTemplate, ChordTemplate?> freeGrip,
        IFingeringGenerationService fingering,
        IReadOnlyList<ChordTemplate>? pending = null)
    {
        if (anchors.Count == 0 || level.Chords.Count == 0) return FingeringFollowPlan.Empty;

        var capo = Math.Max(0, arrangement.Capo);
        // By Id, like every other template lookup — never by list position.
        var byId = ChordTemplateResolver.IndexById(arrangement.ChordTemplates);

        // Stable: two anchors at one position resolve to the later one in the
        // list, as the walk's own output order does.
        var sortedAnchors = anchors.OrderBy(a => a.Position).ToList();
        var minted = new List<ChordTemplate>();
        var known = new List<ChordTemplate>(pending ?? []);
        var variants = new Dictionary<(ChordTemplate, int), ChordTemplate?>(VariantKeyComparer.Instance);
        var chords = new List<(Chord, int, int)>();
        var nextId = Math.Max(fingering.NextTemplateId(arrangement), known.Count == 0 ? 0 : known.Max(t => t.Id) + 1);

        var sortedChords = level.Chords.OrderBy(c => c.Position).ToList();
        var anchorIndex = -1;
        foreach (var chord in sortedChords)
        {
            while (anchorIndex + 1 < sortedAnchors.Count && sortedAnchors[anchorIndex + 1].Position <= chord.Position)
                anchorIndex++;
            if (anchorIndex < 0) continue;
            if (!byId.TryGetValue(chord.TemplateId, out var template)) continue;
            if (freeGrip(template) is not { } grip) continue;

            var index = IndexFingerPosition.Of(grip, capo);
            if (index is null) continue;
            var shift = Math.Max(0, index.Value - sortedAnchors[anchorIndex].Fret);

            if (!variants.TryGetValue((template, shift), out var variant))
            {
                variant = ResolveVariant(arrangement, template, grip, shift, capo, fingering, known, minted, ref nextId);
                variants[(template, shift)] = variant;
            }

            if (variant != null && variant.Id != template.Id)
                chords.Add((chord, template.Id, variant.Id));
        }

        if (chords.Count == 0) return FingeringFollowPlan.Empty;

        // A hand shape draws the grip of the chord it was built over: repoint it
        // with that chord when it named the chord's OLD template.
        var shapes = new List<(HandShape, int, int)>();
        var repointed = new Dictionary<Chord, (int OldId, int NewId)>(ReferenceEqualityComparer.Instance);
        foreach (var (chord, oldId, newId) in chords) repointed[chord] = (oldId, newId);
        foreach (var shape in level.HandShapes)
        {
            // The FIRST chord the shape covers decides; a shape over a chord that
            // kept its grip keeps its template.
            int lo = 0, hi = sortedChords.Count;
            while (lo < hi)
            {
                var mid = (lo + hi) >> 1;
                if (sortedChords[mid].Position < shape.Position) lo = mid + 1;
                else hi = mid;
            }

            if (lo == sortedChords.Count || !shape.Covers(sortedChords[lo].Position)) continue;
            if (repointed.TryGetValue(sortedChords[lo], out var ids) && shape.TemplateId == ids.OldId)
                shapes.Add((shape, ids.OldId, ids.NewId));
        }

        return new FingeringFollowPlan(minted, chords, shapes);
    }

    // The frets of `template` with the suggester's fingering: the base grip.
    private static ChordTemplate SuggestedGrip(ChordTemplate template, int stringCount, PairFingering? twoFretPair)
    {
        var grip = new ChordTemplate { Frets = new Dictionary<int, int>(template.Frets) };
        ChordTemplateFingerSetter.SetSuggestedFingers(grip, Math.Max(1, stringCount), twoFretPair);
        return grip;
    }

    // The base grip shifted `shift` frets up the hand: every finger on a fretted
    // string above the capo + shift. `template` itself when that is what it
    // already says; null when the shift pushes a finger past the pinky.
    private static ChordTemplate? ResolveVariant(
        Arrangement arrangement,
        ChordTemplate template,
        ChordTemplate grip,
        int shift,
        int capo,
        IFingeringGenerationService fingering,
        List<ChordTemplate> known,
        List<ChordTemplate> minted,
        ref int nextId)
    {
        var fingers = new Dictionary<int, int>(grip.Fingers);
        if (shift > 0)
        {
            foreach (var (stringIndex, fret) in grip.Frets)
            {
                if (fret <= capo) continue;
                var finger = grip.GetFinger(stringIndex);
                if (finger < 1 || finger + shift > 4) return null;
                fingers[stringIndex] = finger + shift;
            }
        }

        if (MapEquality.SameEntries(fingers, template.Fingers)) return template;

        var variant = template.Clone();
        variant.Id = -1;
        // Only a SHIFTED grip is the generator's to move; shift 0 is the
        // suggestion, which is free by its fingers alone.
        variant.FollowedFingering = shift > 0;
        variant.Fingers.Clear();
        foreach (var (stringIndex, finger) in fingers) variant.Fingers[stringIndex] = finger;

        var existing = fingering.FindIdenticalTemplate(arrangement, variant)
                       ?? known.FirstOrDefault(m => m.Equals(variant));
        if (existing != null) return existing;

        variant.Id = nextId++;
        minted.Add(variant);
        known.Add(variant);
        return variant;
    }

    private sealed class VariantKeyComparer : IEqualityComparer<(ChordTemplate Template, int Shift)>
    {
        public static readonly VariantKeyComparer Instance = new();

        public bool Equals((ChordTemplate Template, int Shift) x, (ChordTemplate Template, int Shift) y) =>
            ReferenceEquals(x.Template, y.Template) && x.Shift == y.Shift;

        public int GetHashCode((ChordTemplate Template, int Shift) obj) =>
            HashCode.Combine(RuntimeHelpers.GetHashCode(obj.Template), obj.Shift);
    }
}

/// <summary>
///     What <see cref="ChordFingeringFollower.Plan" /> decided: templates to add
///     and the chords / hand shapes to repoint (old id → new id).
/// </summary>
public sealed record FingeringFollowPlan(
    IReadOnlyList<ChordTemplate> MintedTemplates,
    IReadOnlyList<(Chord Chord, int OldId, int NewId)> Chords,
    IReadOnlyList<(HandShape Shape, int OldId, int NewId)> HandShapes)
{
    public static readonly FingeringFollowPlan Empty = new([], [], []);

    public bool IsEmpty => Chords.Count == 0;

    /// <summary>Applies the plan directly — for a caller with no undo history
    /// (an importer building a fresh arrangement). Leaves the templates the
    /// chords moved OFF in place; <see cref="ImportFhpPass" /> drops the ones
    /// nobody uses any more.</summary>
    public void ApplyTo(Arrangement arrangement)
    {
        foreach (var t in MintedTemplates) arrangement.ChordTemplates.Add(t);
        foreach (var (chord, _, newId) in Chords) chord.TemplateId = newId;
        foreach (var (shape, _, newId) in HandShapes) shape.TemplateId = newId;
    }
}
