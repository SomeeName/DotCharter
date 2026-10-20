using DotCharter.Core.Graphics;
using DotCharter.Rendering.Batching;
using DotCharter.Rendering.Components.NoteRendering;
using DotCharter.Rendering.Data;
using DotCharter.Rendering.Data.Render;
using DotCharter.Rendering.Scene;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     The 3D fingering overlay (Settings ▸ Graphics ▸ "Show fingering overlay on
///     3D preview", keybind F8) has to put numbers on the neck for the grip the
///     player is about to hold. It used to draw only inside a 200-250 ms window
///     before each chord, so a paused editor almost never showed anything and at
///     normal speed the numbers flashed for a fifth of a second — the owner's
///     "fingering does not render in 3D".
///     <para>
///         Asserted on the STAGED GEOMETRY through
///         <see cref="FingeringRenderer.RenderCore" />, which skips the
///         process-wide <see cref="FingeringRenderer.Enabled" /> gate, so no case
///         here flips a static another class could be reading. The atlas is a
///         4×4 grid: row 0 = disc, capsule end, capsule middle, "T"; row 1 =
///         "1".."4", which is how a quad's UV cell names what it draws.
///     </para>
/// </summary>
public sealed class FingeringOverlayTests
{
    private const int Floats = QuadBatcher.FloatsPerVertex;
    private const int QuadFloats = Floats * 4;

    // C major: A string fret 3 (finger 3), D fret 2 (2), B fret 1 (1).
    private static readonly (int String, int Fret, int Finger)[] CMajor = [(1, 3, 3), (2, 2, 2), (4, 1, 1)];

    // G shape played with 2-3-4: low E fret 3 (finger 3), A fret 2 (2), high e fret 3 (4).
    private static readonly (int String, int Fret, int Finger)[] GMajor = [(0, 3, 3), (1, 2, 2), (5, 3, 4)];

    // F barre: finger 1 across all six strings at fret 1 + 2/3/4 on top.
    private static readonly (int String, int Fret, int Finger)[] FBarre =
        [(0, 1, 1), (1, 3, 3), (2, 3, 4), (3, 2, 2), (4, 1, 1), (5, 1, 1)];

    [Fact]
    public void A_chord_600ms_out_already_shows_its_numbers()
    {
        // The old window was chord.Time - 200 ms. 600 ms out is a chord the player
        // can see coming — and in a paused editor, the next chord after the cursor.
        var s = Snapshot([Template(0, CMajor)]);
        AddChord(s, 1000, 0);

        Assert.Equal(new[] { "1", "2", "3" }, Letters(Stage(s, 400)));
    }

    [Fact]
    public void The_numbers_are_still_there_at_the_strike()
    {
        var s = Snapshot([Template(0, CMajor)]);
        AddChord(s, 1000, 0);

        Assert.Equal(new[] { "1", "2", "3" }, Letters(Stage(s, 1000)));
    }

    [Fact]
    public void Only_one_grip_is_on_the_neck_at_a_time()
    {
        // Two bare chords both inside the lead: the old overlay drew BOTH, two
        // sets of numbers on one neck. The earlier one wins.
        var s = Snapshot([Template(0, CMajor), Template(1, GMajor)]);
        AddChord(s, 900, 0);
        AddChord(s, 1100, 1);

        var quads = Stage(s, 500);
        Assert.Equal(new[] { "1", "2", "3" }, Letters(quads));
        Assert.Equal(6, quads.Count); // three discs + three numbers
    }

    [Fact]
    public void A_held_hand_shape_keeps_its_numbers_until_it_ends()
    {
        var s = Snapshot([Template(0, CMajor), Template(1, GMajor)]);
        s.HandShapes = [new RenderHandShape { Time = 0, EndTime = 1000, TemplateId = 0 }];
        s.HandShapeCount = 1;
        AddChord(s, 1300, 1);

        Assert.Equal(new[] { "1", "2", "3" }, Letters(Stage(s, 800)));
        // …and once it has ended, the next grip takes over.
        Assert.Equal(new[] { "2", "3", "4" }, Letters(Stage(s, 1050)));
    }

    [Fact]
    public void Single_notes_without_fingering_draw_nothing_but_the_chords_around_them_still_do()
    {
        // The chart shape the owner asked about: chords carry fingers, single
        // notes carry none (the domain model has no per-note finger at all).
        var s = Snapshot([Template(0, CMajor)]);
        s.Notes =
        [
            new RenderNote { Time = 200, StringIndex = 2, Fret = 5 },
            new RenderNote { Time = 400, StringIndex = 3, Fret = 7 }
        ];
        s.NoteCount = 2;
        AddChord(s, 3000, 0);

        Assert.Empty(Stage(s, 100)); // only single notes within reach
        Assert.Equal(new[] { "1", "2", "3" }, Letters(Stage(s, 2600)));
    }

    [Fact]
    public void A_chord_whose_template_has_no_fingers_draws_nothing()
    {
        var s = Snapshot([Template(0, [(1, 3, -1), (2, 2, -1)])]);
        AddChord(s, 500, 0);

        Assert.Empty(Stage(s, 400));
    }

    [Fact]
    public void Lefty_mode_counter_mirrors_the_numbers_so_they_stay_readable()
    {
        // The lefty flip mirrors X through the camera matrix; a number quad with
        // the normal U order would come out as mirror writing.
        var s = Snapshot([Template(0, CMajor)]);
        AddChord(s, 500, 0);

        foreach (var q in Stage(s, 400).Where(q => q.IsLetter))
            Assert.True(q.ULeft < q.URight, "right-handed numbers must keep the atlas U order");

        s.LeftyFlip = true;
        foreach (var q in Stage(s, 400).Where(q => q.IsLetter))
            Assert.True(q.ULeft > q.URight, "lefty numbers must swap U to cancel the camera mirror");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_barre_capsule_is_rounded_outward_at_both_ends_in_either_string_order(bool invertStrings)
    {
        // The end cell is an arch rounded at its TOP (low V). The upper cap must
        // show it as sampled (V grows downward), the lower cap flipped. It used
        // to key off the string INDEX, so inverted strings turned both caps inward.
        var s = Snapshot([Template(0, FBarre)]);
        s.InvertStrings = invertStrings;
        AddChord(s, 500, 0);

        var caps = Stage(s, 400).Where(q => q.Cell == (1, 0)).OrderByDescending(q => q.YTop).ToList();
        Assert.Equal(2, caps.Count);
        Assert.True(caps[0].VTop < caps[0].VBottom, "the upper cap must round upward");
        Assert.True(caps[1].VTop > caps[1].VBottom, "the lower cap must round downward");
    }

    [Fact]
    public void A_grip_fades_in_at_the_edge_of_the_lead_instead_of_popping()
    {
        Assert.Equal(0f, FingeringRenderer.FadeAlpha(1000 + FingeringRenderer.LeadMs, 1000), 3);
        Assert.Equal(1f,
            FingeringRenderer.FadeAlpha(1000 + FingeringRenderer.LeadMs - FingeringRenderer.FadeInMs, 1000), 3);
        Assert.Equal(1f, FingeringRenderer.FadeAlpha(900, 1000), 3); // already struck
        var mid = FingeringRenderer.FadeAlpha(1000 + FingeringRenderer.LeadMs - FingeringRenderer.FadeInMs / 2, 1000);
        Assert.InRange(mid, 0.2f, 0.8f);
    }

    // ---- fixtures -----------------------------------------------------------

    private readonly record struct StagedQuad(
        (int Col, int Row) Cell, float YTop, float ULeft, float URight, float VTop, float VBottom)
    {
        public bool IsLetter => Cell.Row == 1 || Cell == (3, 0);

        public string Letter => Cell == (3, 0) ? "T" : (Cell.Col + 1).ToString();
    }

    private static string[] Letters(IEnumerable<StagedQuad> quads) =>
        quads.Where(q => q.IsLetter).Select(q => q.Letter).Order(StringComparer.Ordinal).ToArray();

    private static List<StagedQuad> Stage(FrameSnapshot snapshot, double timeMs)
    {
        var batcher = new QuadBatcher(null!);
        FingeringRenderer.RenderCore(Context(batcher, snapshot, timeMs));

        var data = batcher.StagedVertices.ToArray();
        var quads = new List<StagedQuad>();
        for (var q = 0; q + QuadFloats <= data.Length; q += QuadFloats)
        {
            // AddQuadUv corner order: (x0,y0) (x0,y1) (x1,y1) (x1,y0), y0 = top.
            float U(int corner) => data[q + corner * Floats + 10];
            float V(int corner) => data[q + corner * Floats + 11];
            var minU = Math.Min(U(0), U(3));
            var minV = Math.Min(V(0), V(1));
            quads.Add(new StagedQuad(
                ((int)(minU / 0.25f), (int)(minV / 0.25f)),
                data[q + 1], U(0), U(3), V(0), V(1)));
        }

        return quads;
    }

    private static RenderChordTemplate Template(int id, (int String, int Fret, int Finger)[] grip)
    {
        var t = new RenderChordTemplate { Id = id, Name = "" };
        for (var s = 0; s < 8; s++)
        {
            t.SetFret(s, -1);
            t.SetFinger(s, -1);
        }

        foreach (var (str, fret, finger) in grip)
        {
            t.SetFret(str, fret);
            t.SetFinger(str, finger);
        }

        return t;
    }

    private static FrameSnapshot Snapshot(RenderChordTemplate[] templates) => new()
    {
        StringCount = 6,
        Anchors = [new RenderAnchor { Time = 0, EndTime = 100_000, Fret = 1, Width = 4 }],
        AnchorCount = 1,
        Templates = templates,
        TemplateCount = templates.Length,
        Chords = [],
        ChordNotes = [],
        HandShapes = [],
        Notes = []
    };

    private static void AddChord(FrameSnapshot s, double time, int templateId)
    {
        s.Chords = [.. s.Chords.Take(s.ChordCount), new RenderChord { Time = time, TemplateId = templateId }];
        s.ChordCount++;
    }

    private static RenderContext Context(QuadBatcher batcher, FrameSnapshot snapshot, double timeMs) => new()
    {
        Snapshot = snapshot,
        TimeMs = timeMs,
        BoardWidth = 10f,
        QuadBatcher = batcher,
        TimeToZScale = HighwaySpeedPolicy.TimeToZScale(HighwaySpeedPolicy.MaxScrollSpeed),
        FadeEndZ = HighwaySpeedPolicy.MaxDrawDistanceZ,
        FadeStartZ = HighwaySpeedPolicy.FadeStartZ(HighwaySpeedPolicy.MaxDrawDistanceZ),
        HorizonMs = HighwaySpeedPolicy.HorizonMs(HighwaySpeedPolicy.MaxScrollSpeed, HighwaySpeedPolicy.MaxDrawDistanceZ)
    };
}
