using DotCharter.Core.Domain.Guitar;

namespace DotCharter.Core.Utilities;

/// <summary>
///     Suggests a left-hand fingering for a chord template from its fret shape
///     alone.
/// </summary>
/// <remarks>
///     <para>
///         The suggestion is a PURE FUNCTION OF THE FRET SHAPE. Templates dedupe
///         by frets, so one shape can only ever carry one fingering — the same
///         shape played in two different musical contexts (different preceding
///         hand position) must still get a single answer here. Measured with
///         <c>tools/chart-eval</c> over the owner's 86 finished charts (43,517
///         fingered chords): 81.0% exact, 88.1% per string, against a ceiling
///         of 92.4% for ANY function of the fret shape (each shape's most
///         common fingering). Over the <c>TestData\EOF</c> templates: 74.6% of
///         1,648, against ~89%. Most of the rest is one charter's habit (the
///         octave as 1-3 or 1-4, a G/B double stop as 1-2, 2-3 or 3-4), which
///         no rule of the shape can serve without losing another charter.
///     </para>
///     <para>
///         Two stages: a catalogue of recognized standard shapes first, then an
///         ergonomic heuristic for everything else. The heuristic models the
///         hand rather than the fret numbers — which strings one finger may
///         legally press together (the index barre may lie across a muted
///         string, and no barre lies directly beneath an open one), that
///         fingers cannot cross, and that fret spacing (and therefore the
///         comfortable finger pair) depends on where on the neck the shape sits.
///     </para>
/// </remarks>
public class ChordTemplateFingerSetter
{
    /// <summary>Highest fret at which the neck is "wide" — see <see cref="ApplyLowPositionSpread" />.</summary>
    private const int WideFretSpacingLimit = 5;

    private static readonly List<List<Shape>> RecognizedShapes = BuildRecognizedShapes();

    private static List<List<Shape>> BuildRecognizedShapes()
    {
        var shapes = new List<Shape>();

        // 2-String Power Chords & Octaves.
        //
        // i == 1 (a string exactly one fret above the root) is deliberately
        // absent: which finger takes it depends on WHERE on the neck the shape
        // sits, which a position-blind catalogue entry cannot express. The
        // heuristic below owns that case.
        for (var i = 0; i < 7; i++)
        {
            if (i == 1) continue;
            shapes.Add(new Shape([0, i], [1, Math.Min(4, i + 1)]));
            shapes.Add(new Shape([0, -1, i], [1, null, Math.Min(4, i + 2)]));
        }

        // 3-String Standard Barres & Open Chords (i == 1 omitted, as above).
        for (var i = 0; i < 5; i++)
        {
            if (i == 1) continue;
            shapes.Add(new Shape([0, 0, i], [1, 1, Math.Min(4, i + 1)]));
        }

        shapes.Add(new Shape([0, 1, 0], [1, 3, 2]));
        shapes.Add(new Shape([0, 2, 2], [1, 3, 4]));

        // 4-String
        shapes.Add(new Shape([0, 2, 2, 1], [1, 3, 4, 2]));
        // The A-shape barre: three adjacent strings two frets above the root are
        // barred by the ring finger. Crowding fingers 2/3/4 into one fret across
        // three strings needs far more arch than laying one finger across them.
        shapes.Add(new Shape([0, 2, 2, 2], [1, 3, 3, 3]));

        // 5-String
        shapes.Add(new Shape([0, 2, 2, 2, 0], [1, 3, 3, 3, 1]));
        shapes.Add(new Shape([0, 2, 2, 1, 0], [1, 3, 4, 2, 1]));

        // 6-String
        shapes.Add(new Shape([0, 2, 2, 1, 0, 0], [1, 3, 4, 2, 1, 1]));
        shapes.Add(new Shape([0, 2, 2, 2, 0, 0], [1, 3, 3, 3, 1, 1]));
        shapes.Add(new Shape([0, 0, 0, 0, 0, 0], [1, 1, 1, 1, 1, 1]));

        var sortedShapes = new List<List<Shape>>();
        for (var i = 0; i <= 6; i++) sortedShapes.Add([]);
        foreach (var shape in shapes) sortedShapes[shape.RequiredFrets.Length].Add(shape);

        return sortedShapes;
    }

    /// <summary>
    ///     Replaces <paramref name="template" />'s fingers with a suggestion
    ///     derived from its frets. Fingers on open strings are always stripped.
    ///     A shape the owner's finished charts settle differently
    /// (<see cref="LearnedChordFingerings" />) takes the learned
    ///     fingering; every other shape the heuristic
    ///     (<see cref="SetHeuristicFingers" />).
    /// </summary>
    /// <param name="template">The template to finger, mutated in place.</param>
    /// <param name="maxStrings">String count of the arrangement.</param>
    public static void SetSuggestedFingers(ChordTemplate template, int maxStrings = 6) =>
        SetSuggestedFingers(template, maxStrings, null);

    /// <summary>
    ///     <see cref="SetSuggestedFingers(ChordTemplate, int)" />, with the
    ///     arrangement's style for a two-string grip whose upper note is two frets
    /// above the root. Still a pure function: the shape and the hint.
    /// </summary>
    /// <param name="template">The template to finger, mutated in place.</param>
    /// <param name="maxStrings">String count of the arrangement.</param>
    /// <param name="twoFretPair">
    ///     How the caller's arrangement plays a power chord or a two-fret octave,
    ///     decided on the Services side for THIS template (which knows the tuning,
    ///     so the caller says whether the pair is one). Null keeps the
    ///     suggestion. Ignored unless the template is exactly two fretted
    ///     strings with the upper one two frets higher.
    /// </param>
    public static void SetSuggestedFingers(ChordTemplate template, int maxStrings, PairFingering? twoFretPair)
    {
        if (twoFretPair is { } pair && TwoFretPairStrings(template) is var (low, high))
        {
            template.Fingers.Clear();
            template.Fingers[low] = 1;
            template.Fingers[high] = pair == PairFingering.IndexRing ? 3 : 4;
            return;
        }

        if (LearnedChordFingerings.For(template, maxStrings) is not { } learned)
        {
            SetHeuristicFingers(template, maxStrings);
            return;
        }

        foreach (var (stringIndex, fret) in template.Frets)
        {
            var finger = stringIndex < learned.Fingers.Length ? learned.Fingers[stringIndex] : -1;
            if (fret == 0 || finger < 0) template.Fingers.Remove(stringIndex);
            else template.Fingers[stringIndex] = finger;
        }
    }

    /// <summary>
    ///     The (lower, upper) string of a template that is exactly two fretted
    ///     strings, the upper one two frets above the lower, or null.
    /// </summary>
    public static (int Low, int High)? TwoFretPairStrings(ChordTemplate template)
    {
        if (template.Frets.Count != 2) return null;
        int low = -1, high = -1;
        foreach (var stringIndex in template.Frets.Keys)
        {
            if (low < 0 || stringIndex < low) (low, high) = (stringIndex, low);
            else high = stringIndex;
        }

        var lowFret = template.Frets[low];
        return lowFret > 0 && template.Frets[high] == lowFret + 2 ? (low, high) : null;
    }

    /// <summary>
    ///     The suggestion WITHOUT the learned table: the catalogue of standard
    ///     shapes, then the ergonomic heuristic. What <c>tools/chart-eval --learn</c>
    ///     compares the charters' fingerings against.
    /// </summary>
    /// <param name="template">The template to finger, mutated in place.</param>
    /// <param name="maxStrings">String count of the arrangement.</param>
    public static void SetHeuristicFingers(ChordTemplate template, int maxStrings = 6)
    {
        // Clean open strings
        var keys = template.Frets.Keys.ToList();
        foreach (var k in keys)
        {
            if (template.Frets[k] == 0)
                template.Fingers.Remove(k);
        }

        var lowestNonzeroString = maxStrings;
        var highestNonzeroString = -1;
        var lowestString = maxStrings;
        var highestString = -1;
        var lowestNonzeroFret = 99;
        var frettedCount = 0;

        foreach (var kvp in template.Frets)
        {
            lowestString = Math.Min(kvp.Key, lowestString);
            highestString = Math.Max(kvp.Key, highestString);

            if (kvp.Value != 0)
            {
                lowestNonzeroString = Math.Min(kvp.Key, lowestNonzeroString);
                highestNonzeroString = Math.Max(kvp.Key, highestNonzeroString);
                lowestNonzeroFret = Math.Min(kvp.Value, lowestNonzeroFret);
                frettedCount++;
            }
        }

        if (highestNonzeroString == -1) return; // All open strings or empty

        // A lone stopped note is played with the index finger: it is the finger
        // the hand position is built around (and the one the fret-hand anchor
        // tracks), so nothing else has a reason to take it.
        if (frettedCount == 1)
        {
            template.Fingers[lowestNonzeroString] = 1;
            return;
        }

        var len = highestString - lowestNonzeroString + 1;
        if (len is < 2 or > 6) return;

        var frets = new int[len];
        for (var i = 0; i < len; i++)
        {
            var currentString = lowestNonzeroString + i;
            if (template.Frets.TryGetValue(currentString, out var fretVal) && fretVal > 0)
                frets[i] = fretVal - lowestNonzeroFret;
            else
                frets[i] = -1;
        }

        var matchingShape = RecognizedShapes[frets.Length].FirstOrDefault(s => s.Matches(frets));
        if (matchingShape != null)
        {
            matchingShape.Apply(template, lowestNonzeroString);
            return;
        }

        AssignFingersByHandShape(template, lowestNonzeroString, highestNonzeroString, lowestNonzeroFret);
    }

    /// <summary>
    ///     Fallback fingering for shapes the catalogue does not know.
    /// </summary>
    /// <param name="template">The template to finger, mutated in place.</param>
    /// <param name="lowestString">Lowest fretted string in the shape.</param>
    /// <param name="highestString">Highest fretted string in the shape.</param>
    /// <param name="lowestFret">Lowest fretted fret in the shape.</param>
    private static void AssignFingersByHandShape(ChordTemplate template,
        int lowestString, int highestString, int lowestFret)
    {
        var fretted = new List<int>();
        for (var s = lowestString; s <= highestString; s++)
        {
            if (template.Frets.TryGetValue(s, out var fret) && fret > 0)
                fretted.Add(s);
        }

        if (fretted.Count == 0) return;

        var pressed = BuildPressGroups(template, fretted);
        MergeIndexBarreAcrossMutedStrings(template, pressed);
        pressed = SplitBarresUnderOpenStrings(template, pressed);

        // The shape's press positions BEFORE any split, i.e. how many places on
        // the neck the hand has to reach. ApplyLowPositionSpread needs that, not
        // the finger count.
        var positions = pressed.Count;
        var lowerPosition = pressed[0];
        var upperPosition = pressed[^1];

        var groups = SplitNonIndexPairs(pressed);
        groups.Sort((a, b) => a.Fret != b.Fret ? a.Fret - b.Fret : a.LowestString - b.LowestString);

        AssignFingersToGroups(groups, lowestFret);

        if (positions == 2)
            ApplyLowPositionSpread(groups, lowerPosition, upperPosition);

        foreach (var group in groups)
        {
            foreach (var s in group.Strings)
                template.Fingers[s] = group.Finger;
        }
    }

    /// <summary>
    ///     Splits the fretted strings into maximal sets one finger may legally
    ///     press together.
    /// </summary>
    /// <param name="template">Template supplying the frets.</param>
    /// <param name="fretted">Fretted strings, ascending.</param>
    /// <returns>Press groups in ascending string order.</returns>
    private static List<PressGroup> BuildPressGroups(ChordTemplate template, List<int> fretted)
    {
        var groups = new List<PressGroup>();

        foreach (var s in fretted)
        {
            var fret = template.Frets[s];
            var joined = false;

            foreach (var group in groups)
            {
                if (group.Fret == fret && CanBarreBetween(template, group.Strings[^1], s, fret))
                {
                    group.Strings.Add(s);
                    joined = true;
                    break;
                }
            }

            if (!joined) groups.Add(new PressGroup(fret, s));
        }

        return groups;
    }

    /// <summary>
    ///     Whether one finger laid across <paramref name="from" />..<paramref name="to" />
    ///     at <paramref name="fret" /> would leave the strings in between sounding
    ///     as the shape requires.
    /// </summary>
    /// <param name="template">Template supplying the frets.</param>
    /// <param name="from">Lower string of the span.</param>
    /// <param name="to">Higher string of the span.</param>
    /// <param name="fret">Fret the finger would be laid across.</param>
    /// <returns><c>true</c> when the barre is legal.</returns>
    private static bool CanBarreBetween(ChordTemplate template, int from, int to, int fret)
    {
        for (var s = from + 1; s < to; s++)
        {
            // Not part of the chord: a barre would make it ring.
            if (!template.Frets.TryGetValue(s, out var between)) return false;
            // Open: a barre would stop it.
            if (between == 0) return false;
            // Below the barre: a barre would raise its pitch.
            if (between < fret) return false;
        }

        return true;
    }

    /// <summary>
    ///     Lays the index barre across muted strings to take in a second barre at
    ///     its own fret.
    /// </summary>
    /// <remarks>
    ///     A muted string under a barre is muted by the barre finger itself, so
    ///     only open strings and strings fretted below the barre stop it
    ///     (<see cref="CanBarreBetween" /> refuses muted strings because a barre
    ///     over a string the chord does not list is not safe in general). The
    ///     merge applies only when the group beyond the mute is itself a barre —
    ///     three or more strings, or two straddling a higher-fretted string —
    ///     because then the alternative is a second finger barring the index's
    ///     own fret (<c>1x222x</c>) or a fingertip pair crossed by the finger
    ///     above (<c>1x243x</c>). A lone string, or two adjacent strings, beyond
    ///     the mute keep their own fingertips: the reference corpus splits those
    ///     (<c>x1x23x</c>, <c>2x34xx</c>) far more often than it barres them.
    /// </remarks>
    /// <param name="template">Template supplying the frets.</param>
    /// <param name="pressed">Press groups in ascending string order, merged in place.</param>
    private static void MergeIndexBarreAcrossMutedStrings(ChordTemplate template, List<PressGroup> pressed)
    {
        var index = IndexGroup(pressed);
        for (var i = 0; i < pressed.Count; i++)
        {
            var group = pressed[i];
            if (ReferenceEquals(group, index) || group.Fret != index.Fret) continue;
            if (group.LowestString < index.LowestString) continue;

            var span = group.Strings[^1] - group.LowestString + 1;
            var isBarre = group.Strings.Count >= 3 || (group.Strings.Count == 2 && span > 2);
            if (!isBarre || !CanBarreAcrossMutes(template, index.Strings[^1], group.LowestString, index.Fret)) continue;

            index.Strings.AddRange(group.Strings);
            pressed.RemoveAt(i);
            i--;
        }
    }

    /// <summary>
    ///     <see cref="CanBarreBetween" />, except that a string the chord does not
    ///     list is muted by the barre finger rather than refusing the barre.
    /// </summary>
    /// <param name="template">Template supplying the frets.</param>
    /// <param name="from">Lower string of the span.</param>
    /// <param name="to">Higher string of the span.</param>
    /// <param name="fret">Fret the finger would be laid across.</param>
    /// <returns><c>true</c> when the barre is legal.</returns>
    private static bool CanBarreAcrossMutes(ChordTemplate template, int from, int to, int fret)
    {
        for (var s = from + 1; s < to; s++)
        {
            if (!template.Frets.TryGetValue(s, out var between)) continue;
            if (between == 0 || between < fret) return false;
        }

        return true;
    }

    /// <summary>
    ///     A barre never lies directly below a string that must ring open: it is
    ///     split into one fingertip per string.
    /// </summary>
    /// <remarks>
    ///     A finger laid flat across strings also touches the next string up
    ///     with the rest of its length and damps it; only arched fingertips leave
    ///     it ringing. The open-position chords show it (Em <c>022000</c> is
    ///     <c>x12xxx</c>/<c>x23xxx</c>, A <c>x02220</c> is <c>x123</c>), and over
    ///     the reference corpus a lowest-fret run directly beneath an open string
    ///     is split in every one of its 598 chords (15 songs) — never barred.
    /// </remarks>
    /// <param name="template">Template supplying the frets.</param>
    /// <param name="pressed">Press groups in ascending string order.</param>
    /// <returns>The resulting groups, still in ascending string order.</returns>
    private static List<PressGroup> SplitBarresUnderOpenStrings(ChordTemplate template, List<PressGroup> pressed)
    {
        var groups = new List<PressGroup>();
        foreach (var group in pressed)
        {
            var above = group.Strings[^1] + 1;
            if (group.Strings.Count > 1 && template.Frets.TryGetValue(above, out var fret) && fret == 0)
            {
                foreach (var s in group.Strings) groups.Add(new PressGroup(group.Fret, s));
            }
            else
            {
                groups.Add(group);
            }
        }

        return groups;
    }

    /// <summary>
    ///     The group the index finger takes: the one lowest on the neck, and
    ///     lowest in string order among those.
    /// </summary>
    /// <param name="pressed">Press groups.</param>
    /// <returns>The index group.</returns>
    private static PressGroup IndexGroup(List<PressGroup> pressed)
    {
        var indexGroup = pressed[0];
        foreach (var group in pressed)
        {
            if (group.Fret < indexGroup.Fret ||
                (group.Fret == indexGroup.Fret && group.LowestString < indexGroup.LowestString))
            {
                indexGroup = group;
            }
        }

        return indexGroup;
    }

    /// <summary>
    ///     Only the index finger barres just two strings; every other finger
    ///     covering a two-string group is split into two fingertips.
    /// </summary>
    /// <remarks>
    ///     The index is the one finger the hand can lay flat along a fret with
    ///     the thumb braced opposite it. The middle/ring/little fingers press
    ///     with the tip, so two strings at one fret take two of them — which is
    ///     also why the E-shape barre is fingered 1-3-4 and not 1-3-3. Three or
    ///     more strings at one fret are a different matter and stay barred.
    /// </remarks>
    /// <param name="pressed">Press groups in ascending string order.</param>
    /// <returns>The resulting groups.</returns>
    private static List<PressGroup> SplitNonIndexPairs(List<PressGroup> pressed)
    {
        // The same group the finger walk assigns 1 to.
        var indexGroup = IndexGroup(pressed);

        var groups = new List<PressGroup>();
        foreach (var group in pressed)
        {
            if (group.Strings.Count == 2 && !ReferenceEquals(group, indexGroup))
            {
                groups.Add(new PressGroup(group.Fret, group.Strings[0]));
                groups.Add(new PressGroup(group.Fret, group.Strings[1]));
            }
            else
            {
                groups.Add(group);
            }
        }

        return groups;
    }

    /// <summary>
    ///     Walks the groups from the lowest fret upward assigning one finger per
    ///     fret, never letting a finger cross one already placed on a lower fret
    ///     or a lower string.
    /// </summary>
    /// <param name="groups">Groups sorted by fret, then string.</param>
    /// <param name="lowestFret">Lowest fretted fret in the shape.</param>
    private static void AssignFingersToGroups(List<PressGroup> groups, int lowestFret)
    {
        var previous = 0;
        foreach (var group in groups)
        {
            var finger = Math.Clamp(1 + group.Fret - lowestFret, 1, 4);
            if (finger <= previous) finger = previous + 1;
            group.Finger = Math.Min(4, finger);
            previous = group.Finger;
        }

        // Clamping at the little finger can collide two groups onto finger 4.
        // When there are at most four groups a plain 1..4 walk always fits.
        var distinct = new HashSet<int>();
        foreach (var group in groups) distinct.Add(group.Finger);
        if (distinct.Count == groups.Count || groups.Count > 4) return;

        for (var i = 0; i < groups.Count; i++) groups[i].Finger = i + 1;
    }

    /// <summary>
    ///     Low on the neck a shape needing only two press positions one fret
    ///     apart takes the ring finger for the upper one, not the middle.
    /// </summary>
    /// <remarks>
    ///     Fret spacing is roughly 35 mm at the nut and roughly 20 mm past the
    ///     twelfth fret. The relaxed spread between index and middle fingertips
    ///     is around the latter; between index and ring, around the former. So a
    ///     one-fret diagonal low on the neck falls naturally under 1-3 and high
    ///     on the neck under 1-2. This only applies when the shape needs two
    ///     press positions: with three or more the fingers must pack one per
    ///     fret whatever the spacing, and the corpus agrees (finger 2 in 92-100%
    ///     of those). It also only applies ascending — when the higher fret is
    ///     on a LOWER string the hand rotates and the ring finger cannot reach
    ///     back down past the index.
    /// </remarks>
    /// <param name="groups">Groups sorted by fret, then string, already fingered.</param>
    /// <param name="lower">The lower of the shape's two press positions.</param>
    /// <param name="upper">The upper of the shape's two press positions.</param>
    private static void ApplyLowPositionSpread(List<PressGroup> groups, PressGroup lower, PressGroup upper)
    {
        if (lower.Fret > WideFretSpacingLimit) return;
        if (upper.Fret != lower.Fret + 1) return;
        if (upper.LowestString <= lower.Strings[^1]) return;

        foreach (var group in groups)
        {
            if (group.Fret == upper.Fret)
                group.Finger = Math.Min(4, group.Finger + 1);
        }
    }

    /// <summary>A set of strings one finger presses at one fret.</summary>
    private sealed class PressGroup
    {
        public PressGroup(int fret, int firstString)
        {
            Fret = fret;
            Strings = [firstString];
        }

        public int Fret { get; }
        public List<int> Strings { get; }
        public int LowestString => Strings[0];
        public int Finger { get; set; }
    }

    private class Shape
    {
        public Shape(int[] requiredFrets, int?[] fingers)
        {
            RequiredFrets = requiredFrets;
            Fingers = fingers;
        }

        public int[] RequiredFrets { get; }
        public int?[] Fingers { get; }

        public bool Matches(int[] shape)
        {
            if (shape.Length > RequiredFrets.Length) return false;
            for (var i = 0; i < shape.Length; i++)
            {
                if (shape[i] != RequiredFrets[i])
                    return false;
            }

            return true;
        }

        public void Apply(ChordTemplate template, int fromString)
        {
            for (var i = fromString; i < fromString + Fingers.Length; i++)
            {
                if (template.Frets.TryGetValue(i, out var f) && f != 0)
                {
                    if (Fingers[i - fromString] is { } fingerValue)
                        template.Fingers[i] = fingerValue;
                    else
                        template.Fingers.Remove(i);
                }
            }
        }
    }
}
