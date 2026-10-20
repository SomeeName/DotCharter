using DotCharter.Rendering.Data;
using DotCharter.Rendering.Data.Render;
using DotCharter.Rendering.Headless;

namespace DotCharter.Tests.Rendering.Headless;

/// <summary>
///     Two sustain tails on adjacent strings, low E and A, through the REAL 3D
///     highway (#587). Both are flat ribbons under a camera that looks down on
///     every string, so where they overlap on screen the A tail's far end lies
///     BEHIND the low E tail's near end: the higher string is in front. The pixels
///     both tails cover are found by rendering each alone; in the frame with both,
///     those pixels must be the low E's. The transparent pass writes no depth, so
///     it was the A tail — staged after the E in snapshot order — that showed
///     there. A chord's tail obeys the same rule against a single note's (#605).
///     The CPU-side order is pinned by <see cref="SustainTailDrawOrderTests" />.
///     GPU-gated: <c>DOTCHARTER_GPU_TESTS=1</c>; <c>DOTCHARTER_GPU_DUMP=1</c>
///     writes <c>vk_tail_overlap_*</c>.
/// </summary>
[Collection("HeadlessGpu")]
public class HeadlessTailOverlapRenderTests
{
    private const int Width = 1600;
    private const int Height = 900;
    private const double NoteMs = 2_000;
    private const double SustainMs = 4_000;

    /// <summary>Past the strike, so no note head is in the frame — only tails.</summary>
    private const double FrameMs = NoteMs + 300;

    private const int LowE = 0;
    private const int AString = 1;

    [Theory]
    [InlineData(false, LowE)] // default: the low E is the top string
    [InlineData(true, AString)] // inverted: the A sits above the low E
    public void Where_two_tails_overlap_the_higher_strings_tail_is_in_front(bool invertStrings, int frontString)
    {
        Assert.SkipWhen(!GoldenFrame.GpuTestsEnabled, "GPU headless test — set DOTCHARTER_GPU_TESTS=1 to run.");

        using var r = new HeadlessHighwayRenderer(useRealChartTextures: true);
        var both = r.Render(Snapshot(invertStrings, LowE, AString), Width, Height, FrameMs);
        var eOnly = r.Render(Snapshot(invertStrings, LowE), Width, Height, FrameMs);
        var aOnly = r.Render(Snapshot(invertStrings, AString), Width, Height, FrameMs);
        var bare = r.Render(Snapshot(invertStrings), Width, Height, FrameMs);
        GoldenFrame.Dump(both, Width, Height, $"vk_tail_overlap_{(invertStrings ? "inverted" : "default")}");

        var overlap = 0;
        var eWins = 0;
        for (var i = 0; i < both.Length; i += 4)
        {
            if (Distance(eOnly, bare, i) < 60 || Distance(aOnly, bare, i) < 60 || Distance(eOnly, aOnly, i) < 60)
                continue;
            overlap++;
            if (Distance(both, eOnly, i) < Distance(both, aOnly, i)) eWins++;
        }

        Assert.True(overlap > 200, $"the two tails overlap on only {overlap} pixels — the scene no longer tests #587");
        var frontWins = frontString == LowE ? eWins : overlap - eWins;
        var frontName = frontString == LowE ? "low E" : "A";
        Assert.True(frontWins > overlap * 0.9,
            $"the {frontName} tail is in front on only {frontWins} of the {overlap} pixels where the tails overlap");
    }

    /// <summary>
    ///     The same rule across the two groups (#605): a single-note tail on the
    ///     HIGHER string beside a chord whose tail sits on the next string down.
    ///     Every single-note tail used to be staged before any chord's, so the
    ///     chord's tail covered the higher string's single-note tail.
    /// </summary>
    [Theory]
    [InlineData(false, LowE, AString, 2)] // default: a single low E above a chord's A and D
    [InlineData(true, 2, LowE, AString)] // inverted: a single D above a chord's low E and A
    public void Where_a_chord_tail_overlaps_a_single_note_tail_the_higher_string_is_in_front(bool invertStrings,
        int singleString, int chordLow, int chordHigh)
    {
        Assert.SkipWhen(!GoldenFrame.GpuTestsEnabled, "GPU headless test — set DOTCHARTER_GPU_TESTS=1 to run.");

        using var r = new HeadlessHighwayRenderer(useRealChartTextures: true);
        var both = r.Render(WithChord(Snapshot(invertStrings, singleString), chordLow, chordHigh), Width, Height,
            FrameMs);
        var singleOnly = r.Render(Snapshot(invertStrings, singleString), Width, Height, FrameMs);
        var chordOnly = r.Render(WithChord(Snapshot(invertStrings), chordLow, chordHigh), Width, Height, FrameMs);
        var bare = r.Render(Snapshot(invertStrings), Width, Height, FrameMs);
        GoldenFrame.Dump(both, Width, Height,
            $"vk_tail_overlap_chord_{(invertStrings ? "inverted" : "default")}");

        var overlap = 0;
        var singleWins = 0;
        for (var i = 0; i < both.Length; i += 4)
        {
            if (Distance(singleOnly, bare, i) < 60 || Distance(chordOnly, bare, i) < 60 ||
                Distance(singleOnly, chordOnly, i) < 60)
            {
                continue;
            }

            overlap++;
            if (Distance(both, singleOnly, i) < Distance(both, chordOnly, i)) singleWins++;
        }

        Assert.True(overlap > 200,
            $"the chord and single-note tails overlap on only {overlap} pixels — the scene no longer tests #605");
        Assert.True(singleWins > overlap * 0.9,
            $"the higher string's single-note tail is in front on only {singleWins} of the {overlap} pixels " +
            "where it overlaps the chord's tail");
    }

    private static FrameSnapshot WithChord(FrameSnapshot snapshot, params int[] strings)
    {
        snapshot.Chords =
            [new RenderChord { Time = NoteMs, TemplateId = -1, NoteOffset = 0, NoteCount = (byte)strings.Length }];
        snapshot.ChordCount = 1;
        snapshot.ChordNotes = strings
            .Select(s => new RenderChordNote { StringIndex = (byte)s, Fret = 7, Sustain = SustainMs })
            .ToArray();
        snapshot.ChordNoteCount = strings.Length;
        return snapshot;
    }

    private static int Distance(byte[] a, byte[] b, int i) =>
        Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]);

    private static FrameSnapshot Snapshot(bool invertStrings, params int[] strings)
    {
        var notes = strings
            .Select(s => new RenderNote { Time = NoteMs, Sustain = SustainMs, StringIndex = (byte)s, Fret = 7 })
            .ToArray();
        return new FrameSnapshot
        {
            StringCount = 6,
            InvertStrings = invertStrings,
            HighwayScrollSpeed = 1.3f,
            EnableNoteHitExplosions = false,
            EnableHitSparks = false,
            EnableEnhancedHitFeedback = false,
            Anchors = [new RenderAnchor { Time = 0, EndTime = 20_000, Fret = 5, Width = 4 }],
            AnchorCount = 1,
            Notes = notes,
            NoteCount = notes.Length
        };
    }
}
