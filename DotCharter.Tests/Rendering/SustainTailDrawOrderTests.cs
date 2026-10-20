using DotCharter.Core.Graphics;
using DotCharter.Rendering.Batching;
using DotCharter.Rendering.Components.NoteRendering;
using DotCharter.Rendering.Data;
using DotCharter.Rendering.Data.Render;
using DotCharter.Rendering.Scene;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     Sustain and slide tails are flat ribbons at their string's height, drawn in
///     the transparent pass, which writes no depth: the order they are staged in IS
///     their occlusion. The camera sits above every string, so where two tails
///     overlap on screen the HIGHER string's tail is the nearer one and must be
///     staged LAST. In snapshot order (time, then string) the A string's tail was
///     staged after the low E's and painted over it, although the low E sits above
///     the A by default (#587). Single-note and chord tails are ONE pass ordered
///     that way (#605): every single-note tail used to stage before any chord's,
///     so a chord's A tail still covered a single low E tail; and a bent tail is
///     ordered by where each stretch of it is DRAWN, not by its resting string.
///     <see cref="Headless.HeadlessTailOverlapRenderTests" /> checks the pixels.
/// </summary>
public sealed class SustainTailDrawOrderTests
{
    private const int QuadFloats = QuadBatcher.FloatsPerVertex * 4;
    private const double NoteMs = 1_400;
    private const double SustainMs = 1_000;
    private const int GString = 3;

    [Fact]
    public void The_camera_sits_above_every_string_so_the_higher_tail_is_the_nearer()
    {
        const int maxStrings = 8;
        var topLane = FretboardConstants.BottomStringPosition + (maxStrings - 1) * FretboardConstants.StringDistance;
        Assert.True(FretboardConstants.CameraBaseHeight + FretboardConstants.ChartboardYPosition > topLane,
            "the highway camera no longer looks DOWN on every string: tail order by height is not depth order");
    }

    [Theory]
    [InlineData(false, new[] { 0, 1 })] // the owner's frame: low E above the A, E listed first
    [InlineData(false, new[] { 1, 0 })]
    [InlineData(true, new[] { 0, 1 })] // inverted: low E at the bottom
    [InlineData(true, new[] { 1, 0 })]
    [InlineData(false, new[] { 0, 3, 1, 5, 2, 4 })]
    public void Single_note_tails_are_staged_bottom_string_first(bool invertStrings, int[] stringsInSnapshotOrder)
    {
        var snapshot = Snapshot(invertStrings);
        WithNotes(snapshot, stringsInSnapshotOrder);
        var batcher = new QuadBatcher(null!);

        SustainRenderer.Render(Context(batcher, snapshot));

        AssertStagedBottomUp(batcher, stringsInSnapshotOrder.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_chords_tails_are_staged_bottom_string_first(bool invertStrings)
    {
        var snapshot = Snapshot(invertStrings);
        WithChord(snapshot, NoteFlags.Vibrato, 0, 1, 2);
        var batcher = new QuadBatcher(null!);

        SustainRenderer.Render(Context(batcher, snapshot));

        AssertStagedBottomUp(batcher, 3);
    }

    /// <summary>The #605 case and its neighbours: single-note and chord tails
    /// interleave in ONE climb up the string stack, whichever group a string is in.</summary>
    [Theory]
    [InlineData(false, new[] { 0 }, new[] { 1, 2 })] // a single low E above a chord's A and D
    [InlineData(true, new[] { 2 }, new[] { 0, 1 })] // inverted: a single D above a chord's low E and A
    [InlineData(false, new[] { 1, 3 }, new[] { 0, 2, 4 })] // the groups interleave
    [InlineData(true, new[] { 1, 3 }, new[] { 0, 2, 4 })]
    [InlineData(false, new[] { 2 }, new[] { 0, 1 })] // a chord above a single note (was already right)
    public void Single_note_and_chord_tails_share_one_bottom_up_order(bool invertStrings, int[] singles,
        int[] chordStrings)
    {
        var snapshot = Snapshot(invertStrings);
        WithNotes(snapshot, singles);
        WithChord(snapshot, NoteFlags.None, chordStrings);
        var batcher = new QuadBatcher(null!);

        SustainRenderer.Render(Context(batcher, snapshot));

        AssertStagedBottomUp(batcher, singles.Length + chordStrings.Length);
    }

    [Fact]
    public void The_chord_pass_stages_no_tails_of_its_own()
    {
        var snapshot = Snapshot(invertStrings: false);
        WithChord(snapshot, NoteFlags.Vibrato, 0, 1, 2);
        var batcher = new QuadBatcher(null!);

        ChordRenderer.Render(Context(batcher, snapshot), new RenderResources());

        var data = batcher.StagedVertices.ToArray();
        Assert.True(data.Length > 0, "the chord drew nothing at all — the scene no longer has a chord box");
        for (var q = 0; q < data.Length; q += QuadFloats)
        {
            Assert.True(TailString(data.AsSpan(q, QuadFloats)) < 0,
                "ChordRenderer staged a tail: chord tails belong to SustainRenderer's one ordered pass");
        }
    }

    /// <summary>
    ///     A 1½-step bend lifts the G tail 1.5 lanes, past the next string up. Where
    ///     it is drawn BELOW that string's flat tail it must be staged before it, and
    ///     where it is drawn ABOVE it, after it. Ordered by its resting string, the
    ///     whole bent tail staged before the flat one and the flat tail covered the
    ///     bent stretch that rose in front of it.
    /// </summary>
    [Theory]
    [InlineData(false, 2)] // default: the D string is the next lane above the G
    [InlineData(true, 4)] // inverted: the B string is
    public void A_bent_tail_is_staged_by_the_height_it_is_drawn_at(bool invertStrings, int flatString)
    {
        var snapshot = Snapshot(invertStrings);
        WithNotes(snapshot, GString, flatString);
        WithBend(snapshot, 0, 3f);
        var batcher = new QuadBatcher(null!);

        SustainRenderer.Render(Context(batcher, snapshot));

        var data = batcher.StagedVertices.ToArray();
        var flatY = float.NaN;
        var firstFlat = int.MaxValue;
        var lastFlat = -1;
        for (var q = 0; q < data.Length; q += QuadFloats)
        {
            if (TailString(data.AsSpan(q, QuadFloats)) != flatString) continue;
            flatY = data[q + 1];
            firstFlat = Math.Min(firstFlat, q);
            lastFlat = Math.Max(lastFlat, q);
        }

        Assert.True(lastFlat >= 0, "the flat tail was not staged");
        var below = 0;
        var above = 0;
        for (var q = 0; q < data.Length; q += QuadFloats)
        {
            if (TailString(data.AsSpan(q, QuadFloats)) != GString) continue;
            var (minY, maxY) = YRange(data.AsSpan(q, QuadFloats));
            if (maxY < flatY)
            {
                below++;
                Assert.True(q < firstFlat,
                    $"a stretch of the bent tail drawn BELOW the flat tail (y {maxY} < {flatY}) is staged after it");
            }
            else if (minY > flatY)
            {
                above++;
                Assert.True(q > lastFlat,
                    $"a stretch of the bent tail drawn ABOVE the flat tail (y {minY} > {flatY}) is staged before it " +
                    "and the flat tail paints over it");
            }
        }

        Assert.True(below > 0 && above > 0,
            $"the bend no longer crosses the flat tail ({below} quads below, {above} above): the scene tests nothing");
    }

    [Fact]
    public void A_bent_tail_draws_each_stretch_and_its_peak_marker_exactly_once()
    {
        var bent = Stage(bend: true);
        var countFlat = Stage(bend: false);

        // Same tail, same segment count: splitting it across passes must neither
        // drop nor repeat a segment. The bend adds ONE white peak marker.
        Assert.Equal(countFlat.Tails, bent.Tails);
        Assert.Equal(1, bent.PeakMarkers);
        Assert.Equal(0, countFlat.PeakMarkers);

        static (int Tails, int PeakMarkers) Stage(bool bend)
        {
            var snapshot = Snapshot(invertStrings: false);
            WithNotes(snapshot, GString);
            if (bend) WithBend(snapshot, 0, 3f);
            else snapshot.Notes[0].Flags = NoteFlags.Tremolo; // a technique: the same 48 segments, no white shine
            var batcher = new QuadBatcher(null!);
            SustainRenderer.Render(Context(batcher, snapshot));
            var data = batcher.StagedVertices.ToArray();
            var tails = 0;
            var peaks = 0;
            for (var q = 0; q < data.Length; q += QuadFloats)
            {
                var quad = data.AsSpan(q, QuadFloats);
                if (TailString(quad) == GString) tails++;
                if (quad[3] == 1f && quad[4] == 1f && quad[5] == 1f && quad[6] == 220 / 255f) peaks++;
            }

            return (tails, peaks);
        }
    }

    [Fact]
    public void Ordering_the_tails_by_height_allocates_nothing()
    {
        // One context per run: a batcher cannot be emptied without a device.
        var contexts = new RenderContext[6];
        for (var c = 0; c < contexts.Length; c++)
        {
            var snapshot = Snapshot(invertStrings: false);
            WithNotes(snapshot, 0, 1, GString);
            WithBend(snapshot, 2, 3f);
            WithChord(snapshot, NoteFlags.Vibrato, 2, 4);
            contexts[c] = Context(new QuadBatcher(null!), snapshot);
        }

        var resources = new RenderResources();
        var next = 0;
        AllocationProbe.AssertAllocationFree("the one height-ordered sustain + chord tail pass", () =>
        {
            var context = contexts[next++];
            SustainRenderer.Render(context);
            ChordRenderer.Render(context, resources);
        });
    }

    /// <summary>The order in which each string's tail first appears among the
    /// staged quads (a tail's outer edges carry its string colour at
    /// <see cref="RenderingConstants.BarAlpha" />) must climb the string stack.</summary>
    private static void AssertStagedBottomUp(QuadBatcher batcher, int tailCount)
    {
        var data = batcher.StagedVertices.ToArray();
        var firstYs = new List<float>();
        var seen = new HashSet<int>();
        for (var q = 0; q < data.Length; q += QuadFloats)
        {
            var stringIndex = TailString(data.AsSpan(q, QuadFloats));
            if (stringIndex < 0 || !seen.Add(stringIndex)) continue;
            firstYs.Add(data[q + 1]);
        }

        Assert.Equal(tailCount, firstYs.Count);
        for (var i = 1; i < firstYs.Count; i++)
        {
            Assert.True(firstYs[i] > firstYs[i - 1],
                $"tail {i} (y {firstYs[i]}) is staged after a HIGHER tail (y {firstYs[i - 1]}) and paints over it: " +
                string.Join(", ", firstYs));
        }
    }

    /// <summary>The string whose tail edge this quad is: untextured (mode 0), lying
    /// flat (normal +Y), in the string's colour at full bar alpha; else -1.</summary>
    private static int TailString(ReadOnlySpan<float> quad)
    {
        if (quad[6] != RenderingConstants.BarAlpha / 255f || quad[8] != 1f || quad[12] != 0f) return -1;
        for (var s = 0; s < 6; s++)
        {
            var c = FretboardConstants.StringColors[s];
            if (quad[3] == c.R / 255f && quad[4] == c.G / 255f && quad[5] == c.B / 255f) return s;
        }

        return -1;
    }

    private static (float Min, float Max) YRange(ReadOnlySpan<float> quad)
    {
        var min = float.MaxValue;
        var max = float.MinValue;
        for (var v = 0; v < 4; v++)
        {
            var y = quad[v * QuadBatcher.FloatsPerVertex + 1];
            min = Math.Min(min, y);
            max = Math.Max(max, y);
        }

        return (min, max);
    }

    private static void WithNotes(FrameSnapshot snapshot, params int[] strings)
    {
        snapshot.Notes = strings
            .Select(s => new RenderNote { Time = NoteMs, Sustain = SustainMs, StringIndex = (byte)s, Fret = 7 })
            .ToArray();
        snapshot.NoteCount = strings.Length;
    }

    /// <summary>Bends note <paramref name="noteIndex" /> up to <paramref name="step" />
    /// half-steps mid-tail and back down, so part of it rests and part is lifted.</summary>
    private static void WithBend(FrameSnapshot snapshot, int noteIndex, float step)
    {
        snapshot.BendValues =
        [
            new RenderBendValue { Time = NoteMs + SustainMs * 0.3, Step = 0f },
            new RenderBendValue { Time = NoteMs + SustainMs * 0.5, Step = step },
            new RenderBendValue { Time = NoteMs + SustainMs * 0.7, Step = 0f }
        ];
        snapshot.Notes[noteIndex].Flags = NoteFlags.HasBends;
        snapshot.Notes[noteIndex].BendOffset = 0;
        snapshot.Notes[noteIndex].BendCount = (byte)snapshot.BendValues.Length;
    }

    private static void WithChord(FrameSnapshot snapshot, NoteFlags flags, params int[] strings)
    {
        snapshot.Chords =
            [new RenderChord { Time = NoteMs, TemplateId = -1, NoteOffset = 0, NoteCount = (byte)strings.Length }];
        snapshot.ChordCount = 1;
        snapshot.ChordNotes = strings
            .Select(s => new RenderChordNote { StringIndex = (byte)s, Fret = 7, Sustain = SustainMs, Flags = flags })
            .ToArray();
        snapshot.ChordNoteCount = strings.Length;
    }

    private static FrameSnapshot Snapshot(bool invertStrings) => new()
    {
        StringCount = 6,
        InvertStrings = invertStrings,
        Anchors = [new RenderAnchor { Time = 0, EndTime = 100_000, Fret = 5, Width = 4 }],
        AnchorCount = 1
    };

    private static RenderContext Context(QuadBatcher batcher, FrameSnapshot snapshot) => new()
    {
        Snapshot = snapshot,
        TimeMs = 1_000,
        BoardWidth = 10f,
        QuadBatcher = batcher,
        TimeToZScale = HighwaySpeedPolicy.TimeToZScale(HighwaySpeedPolicy.MaxScrollSpeed),
        FadeEndZ = HighwaySpeedPolicy.MaxDrawDistanceZ,
        FadeStartZ = HighwaySpeedPolicy.FadeStartZ(HighwaySpeedPolicy.MaxDrawDistanceZ),
        HorizonMs = HighwaySpeedPolicy.HorizonMs(
            HighwaySpeedPolicy.MaxScrollSpeed, HighwaySpeedPolicy.MaxDrawDistanceZ)
    };
}
