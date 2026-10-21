namespace DotCharter.Tests.Core.Utilities;

public class ChordTemplateFingerSetterTests
{
    private static ChordTemplate Fingered(params (int String, int Fret)[] frets)
    {
        var t = new ChordTemplate();
        foreach (var (s, f) in frets) t.Frets[s] = f;
        ChordTemplateFingerSetter.SetSuggestedFingers(t);
        return t;
    }

    private static void AssertFingers(ChordTemplate t, params (int String, int Finger)[] expected)
    {
        Assert.Equal(expected.Length, t.Fingers.Count);
        foreach (var (s, finger) in expected)
            Assert.Equal(finger, t.Fingers.TryGetValue(s, out var actual) ? actual : -1);
    }

    [Fact]
    public void SetSuggestedFingers_AllOpen_DoesNothing()
    {
        var t = new ChordTemplate { Frets = new Dictionary<int, int> { [0] = 0, [1] = 0, [2] = 0 } };
        ChordTemplateFingerSetter.SetSuggestedFingers(t);
        Assert.Empty(t.Fingers);
    }

    [Fact]
    public void SetSuggestedFingers_RemovesFingersOnOpenStrings()
    {
        // Pre-existing fingers on open strings must be stripped.
        var t = new ChordTemplate
        {
            Frets = new Dictionary<int, int> { [0] = 0, [1] = 2, [2] = 2 },
            Fingers = new Dictionary<int, int> { [0] = 1, [1] = 3, [2] = 4 }
        };
        ChordTemplateFingerSetter.SetSuggestedFingers(t);
        Assert.False(t.Fingers.ContainsKey(0));
    }

    [Fact]
    public void SetSuggestedFingers_PowerChord_SetsFingers()
    {
        // 2-string power chord on 5th fret: root + fret+2 on next string.
        var t = new ChordTemplate { Frets = new Dictionary<int, int> { [0] = 5, [1] = 7 } };
        ChordTemplateFingerSetter.SetSuggestedFingers(t);
        // Shape {0,2} on 2-string: fingers {1, min(4, 2+1)=3}
        Assert.Equal(1, t.Fingers[0]);
        Assert.Equal(3, t.Fingers[1]);
    }

    [Fact]
    public void SetSuggestedFingers_UnrecognizedShape_FallsBackToFretOffsetFingers()
    {
        var t = new ChordTemplate
        {
            Frets = new Dictionary<int, int> { [0] = 5, [1] = 99 }, // invalid / absurd gap
            Fingers = new Dictionary<int, int> { [0] = 2, [1] = 2 }
        };
        ChordTemplateFingerSetter.SetSuggestedFingers(t);
        // Fallback assigns finger by fret offset: lowest fret -> 1, +>=3 -> 4.
        Assert.Equal(1, t.Fingers[0]);
        Assert.Equal(4, t.Fingers[1]);
    }

    // ── Press groups: which strings one finger may legally cover ────────────

    [Fact]
    public void PressGroups_BarresUnderAHigherFrettedString() =>
        // The index lies across both fret-5 strings; the fret-7 string in
        // between sounds over the top of it. This is the E-shape barre idea.
        AssertFingers(Fingered((0, 5), (1, 7), (2, 5)), (0, 1), (1, 3), (2, 1));

    [Fact]
    public void SameFretPairAcrossAMutedString_IsOneIndexBarre() =>
        // Two fret-5 notes with a string between that is not in the chord: the
        // index lies across all three and mutes the middle one with its flat,
        // never a second finger on its own fret (the charters' grip).
        AssertFingers(Fingered((0, 5), (2, 5)), (0, 1), (2, 1));

    [Fact]
    public void PressGroups_DoesNotBarreThroughAnOpenString() =>
        // Barring here would stop string 1, which must ring open.
        AssertFingers(Fingered((0, 5), (1, 0), (2, 5)), (0, 1), (2, 2));

    [Fact]
    public void PressGroups_FingersNeverCross()
    {
        // Two separate groups land on fret 7. The higher string takes the next
        // finger up rather than reusing the one already on a lower string.
        AssertFingers(Fingered((0, 5), (1, 7), (2, 6), (3, 7)),
            (0, 1), (1, 3), (2, 2), (3, 4));
    }

    // ── A lone stopped note ─────────────────────────────────────────────────

    [Fact]
    public void SingleFrettedString_TakesTheIndexFinger() => AssertFingers(Fingered((0, 7)), (0, 1));

    [Fact]
    public void SingleFrettedString_TakesTheIndexFingerAlongsideAnOpenString() => AssertFingers(Fingered((3, 9), (4, 0)), (3, 1));

    // ── Only the index barres a two-string group ────────────────────────────

    [Fact]
    public void TwoStringGroup_AboveTheIndex_SplitsIntoTwoFingers() =>
        // Strings 2 and 3 share fret 9 but cannot be reached by the index
        // (string 1 is outside the chord), so they take a fingertip each.
        AssertFingers(Fingered((0, 9), (2, 9), (3, 9)), (0, 1), (2, 2), (3, 3));

    [Fact]
    public void ThreeStringGroup_StaysBarred()
    {
        // Three adjacent strings at one fret are barred, not split. (At the
        // index's OWN fret they join the index barre instead — see
        // MinorSevenShell_IndexBarresAcrossTheMutedA.)
        AssertFingers(Fingered((0, 9), (2, 10), (3, 10), (4, 10)),
            (0, 1), (2, 2), (3, 2), (4, 2));
    }

    [Fact]
    public void EShapeBarre_UsesRingAndLittleFingers()
    {
        AssertFingers(Fingered((0, 5), (1, 7), (2, 7), (3, 6), (4, 5), (5, 5)),
            (0, 1), (1, 3), (2, 4), (3, 2), (4, 1), (5, 1));
    }

    [Fact]
    public void AShapeBarre_UsesARingFingerBarre()
    {
        // Three strings two frets above the root: one finger laid across them
        // rather than 2/3/4 crowded into a single fret.
        AssertFingers(Fingered((0, 5), (1, 7), (2, 7), (3, 7)),
            (0, 1), (1, 3), (2, 3), (3, 3));
    }

    // ── Position-dependent spread ───────────────────────────────────────────

    [Fact]
    public void AscendingDoubleStop_LowOnTheNeck_UsesTheRingFinger() =>
        // Wide fret spacing near the nut suits the index-ring spread.
        AssertFingers(Fingered((3, 2), (4, 3)), (3, 1), (4, 3));

    [Fact]
    public void AscendingDoubleStop_HighOnTheNeck_UsesTheMiddleFinger() =>
        // Same shape, narrow spacing: one finger per fret.
        AssertFingers(Fingered((3, 9), (4, 10)), (3, 1), (4, 2));

    [Fact]
    public void DescendingDoubleStop_LowOnTheNeck_KeepsTheMiddleFinger() =>
        // The higher fret is on the LOWER string, so the hand rotates and the
        // ring finger cannot reach back down past the index.
        AssertFingers(Fingered((3, 3), (4, 2)), (3, 2), (4, 1));

    [Fact]
    public void ThreePressPositions_LowOnTheNeck_PackOneFingerPerFret() =>
        // With three positions to cover there is no spare finger to spread with.
        AssertFingers(Fingered((0, 2), (1, 3), (2, 4)), (0, 1), (1, 2), (2, 3));

    [Fact]
    public void PairOneFretAboveAGappedIndex_LowOnTheNeck_UsesRingAndLittle() => AssertFingers(Fingered((0, 5), (2, 6), (3, 6)), (0, 1), (2, 3), (3, 4));

    // ── Deliberately NOT special-cased ──────────────────────────────────────

    [Fact]
    public void SameFretDoubleStop_IsBarred() =>
        // The reference corpus fingers this shape 3/4 more often than 1/1, but
        // 28 of those 32 instances come from one chart; across the other 26
        // charts the same-fret double stop is barred in every fret band and on
        // every string pair. A one-chart habit is not a general rule, so this
        // stays a barre.
        AssertFingers(Fingered((4, 5), (5, 5)), (4, 1), (5, 1));

    // ── No barre directly beneath an open string (#488) ────────────────────

    [Fact]
    public void OpenEMinor_SplitsThePairUnderTheOpenGString() =>
        // 022000: a flat index across A and D would damp the open G above it.
        AssertFingers(Fingered((0, 0), (1, 2), (2, 2), (3, 0), (4, 0), (5, 0)), (1, 1), (2, 2));

    [Fact]
    public void OpenEMinorSeven_SplitsThePairAndAddsTheRing() =>
        AssertFingers(Fingered((0, 0), (1, 2), (2, 2), (3, 0), (4, 3), (5, 0)), (1, 1), (2, 2), (4, 3));

    [Fact]
    public void OpenAMajor_SplitsTheThreeStringRunUnderTheOpenE() =>
        AssertFingers(Fingered((1, 0), (2, 2), (3, 2), (4, 2), (5, 0)), (2, 1), (3, 2), (4, 3));

    [Fact]
    public void PairWithAFrettedStringAbove_StaysBarredOverAnOpenStringBelow() =>
        // x0223x: the open A is BELOW the D/G pair, past the fingertip, and the
        // B above is fretted — the barre stays.
        AssertFingers(Fingered((1, 0), (2, 2), (3, 2), (4, 3)), (2, 1), (3, 1), (4, 3));

    [Fact]
    public void OpenD_KeepsTheRingBetweenIndexAndMiddle() =>
        // xx0232 is 1-3-2 in the reference corpus (707 chords, 10 songs): a
        // finger at a higher fret between two at a lower fret is no crossing.
        AssertFingers(Fingered((2, 0), (3, 2), (4, 3), (5, 2)), (3, 1), (4, 3), (5, 2));

    // ── The index barre across a muted string (#488) ───────────────────────

    [Fact]
    public void MinorSevenShell_IndexBarresAcrossTheMutedA() =>
        // 4x444x: one index barre, not the index plus a middle-finger barre
        // on the same fret (1x222x).
        AssertFingers(Fingered((0, 4), (2, 4), (3, 4), (4, 4)), (0, 1), (2, 1), (3, 1), (4, 1));

    [Fact]
    public void DominantSevenShell_IndexBarresAcrossTheMutedA() =>
        // 5x565x: 1x121x, not the crossed 1x243x.
        AssertFingers(Fingered((0, 5), (2, 5), (3, 6), (4, 5)), (0, 1), (2, 1), (3, 2), (4, 1));

    [Fact]
    public void MinorSevenOnTheA_IndexBarresAcrossTheMutedD() =>
        AssertFingers(Fingered((1, 5), (3, 5), (4, 6), (5, 5)), (1, 1), (3, 1), (4, 2), (5, 1));

    [Fact]
    public void TwoAdjacentStringsBeyondAMute_KeepTheirOwnFingers() =>
        // 3x33xx: the corpus splits this (2x34 / 1x23, 10 songs) and never
        // barres it, so the merge needs a barre beyond the mute.
        AssertFingers(Fingered((0, 3), (2, 3), (3, 3)), (0, 1), (2, 2), (3, 3));

    [Fact]
    public void OneStringBeyondAMute_KeepsItsOwnFinger() =>
        // x5x56x is x1x23x in 692 of 804 reference chords.
        AssertFingers(Fingered((1, 5), (3, 5), (4, 6)), (1, 1), (3, 2), (4, 3));

    // ── Lint: fingers never cross along the neck ───────────────────────────

    [Fact]
    public void EveryShapeUpToFourFrettedStrings_FingersFollowFretOrder()
    {
        // Every shape over {muted, open, n..n+3} with one to four fretted
        // strings, at three neck positions. A lower finger never sits on a
        // higher fret than a higher finger, and one finger never spans two
        // frets. (String order is NOT linted: the open D, 1-3-2, puts the
        // ring finger between the index and middle, and is right.)
        var failures = new List<string>();
        foreach (var n in new[] { 1, 5, 12 })
        {
            int[] options = [-1, 0, n, n + 1, n + 2, n + 3];
            for (var code = 0; code < 46656; code++)
            {
                var t = new ChordTemplate();
                var c = code;
                for (var s = 0; s < 6; s++, c /= 6)
                {
                    if (options[c % 6] >= 0) t.Frets[s] = options[c % 6];
                }

                var frettedCount = t.Frets.Values.Count(f => f > 0);
                if (frettedCount is 0 or > 4) continue;

                ChordTemplateFingerSetter.SetSuggestedFingers(t);
                // A THUMB (0) over the neck on the lowest fretted string is a
                // learned charter grip (LearnedChordFingerings, #502), not a
                // finger in the fret order: it is set aside.
                var lowestString = t.Frets.Where(kv => kv.Value > 0).Min(kv => kv.Key);
                var fretted = t.Frets.Where(kv => kv.Value > 0 &&
                                                  !(kv.Key == lowestString && t.Fingers.GetValueOrDefault(kv.Key, -1) == 0)).ToList();
                foreach (var a in fretted)
                {
                    var fa = t.Fingers.GetValueOrDefault(a.Key, -1);
                    var crossed = fretted.Any(b =>
                    {
                        var fb = t.Fingers.GetValueOrDefault(b.Key, -1);
                        return (fa < fb && a.Value > b.Value) || (fa == fb && a.Value != b.Value);
                    });
                    if (fa is >= 1 and <= 4 && !crossed) continue;

                    failures.Add(string.Join(",", Enumerable.Range(0, 6).Select(s => t.Frets.TryGetValue(s, out var f) ? f.ToString() : "x")));
                    break;
                }
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count} crossed shapes, e.g. {string.Join(" | ", failures.Take(5))}");
    }
}
