using DotCharter.Rendering.Data;
using DotCharter.Rendering.Data.Render;
using DotCharter.Rendering.Headless;

namespace DotCharter.Tests.Rendering.Headless;

/// <summary>
///     The show lights' stage lights on the GPU: one frame per state, each compared
///     against the same chart with no showlights. The lamp heads must light the
///     top edge in the beam colour, the haze must tint the horizon at the sides in
///     the fog colour, the far end of the highway must stay as dark as it was, and
///     the editor chip must land in the top-left corner.
/// </summary>
[Collection("HeadlessGpu")]
public class HeadlessStageLightsTests
{
    private const int Width = 1600;
    private const int Height = 900;
    private const double TimeMs = 3000;

    private static readonly ShowlightChipLabels ChipLabels = new(
        "Fog: {0} · Beams: {1} · Lasers: {2}", "on", "off", "none",
        ShowlightCatalog.AllDefinitions.ToDictionary(d => d.Note, d => d.DisplayName));

    public static TheoryData<string, int, int, bool> States => new()
    {
        { "fog_purple_beams_cyan_lasers", 35, 55, true },
        { "fog_green_beams_off", 24, ShowlightPalette.BeamsOff, false },
        { "fog_purple_beams_yellowplus_lasers", 35, 58, true },
        { "fog_red_beams_redplus", 32, 56, false }
    };

    [Theory]
    [MemberData(nameof(States))]
    public void The_stage_lights_frame_the_highway_and_leave_its_far_end_dark(
        string name, int fog, int beam, bool lasers)
    {
        Assert.SkipWhen(!GoldenFrame.GpuTestsEnabled,
            "GPU headless test — set DOTCHARTER_GPU_TESTS=1 to run.");

        using var r = new HeadlessHighwayRenderer(useRealChartTextures: true);
        var plain = r.Render(Chart(), Width, Height, TimeMs);
        var px = r.Render(Lit(fog, beam, lasers), Width, Height, TimeMs);
        GoldenFrame.Dump(px, Width, Height, $"vk_stage_{name}");

        // 1. The lamp heads glow on the top edge, bright with beams on, dim but there with beams off.
        var beamOn = beam != ShowlightPalette.BeamsOff;
        var head = MaxDelta(px, plain, 130, 160, 0, 12);
        Assert.True(beamOn ? head > 80 : head is > 15 and < 80, $"{name}: lamp head gain {head}");

        // 2. The haze tints the horizon at the sides toward the fog colour.
        var fogColour = ShowlightPalette.FogColor(fog);
        var tint = MeanColourDelta(px, plain, 40, 400, 340, 420);
        var gain = tint.B * 0.0722 + tint.G * 0.7152 + tint.R * 0.2126;
        Assert.True(gain > 6, $"{name}: the horizon haze only gained {gain:F1} luma");
        if (!beamOn)
        {
            Assert.True(Dominant(tint) == Dominant((fogColour.B, fogColour.G, fogColour.R)) ||
                        Correlates(tint, (fogColour.B, fogColour.G, fogColour.R)),
                $"{name}: the haze tint {tint} does not follow the fog colour {fogColour}");
        }

        // 3. Cones only with beams on: under the first head the sky gains more
        //    than it does in the clear middle, where only the ambient wash reaches.
        var cone = MeanDelta(px, plain, 120, 170, 150, 260) - MeanDelta(px, plain, 620, 670, 150, 260);
        Assert.True(beamOn ? cone > 6 : Math.Abs(cone) < 3, $"{name}: cone gain {cone:F1}");

        // 4. Nothing bright behind the far end of the highway.
        var behind = MaxDelta(px, plain, Width / 2 - 60, Width / 2 + 60, (int)(Height * 0.34), (int)(Height * 0.42));
        Assert.True(behind <= 40, $"{name}: the lights brightened the far highway centre by {behind} luma");

        // 5. The chip's white text sits on a dark plate in the top-left corner.
        var text = CountBright(px, 20, 300, 18, 42, 200);
        Assert.True(text > 60, $"{name}: no chip text in the top-left corner ({text} bright pixels)");
    }

    /// <summary>
    ///     A tapping section: anchor fret 5, 15 frets wide. The lane fill is
    ///     translucent, so the haze must notch out over the whole highway, not just
    ///     its far centre: the far lanes (inset from the edges measured on this
    ///     frame) may gain at most 6 levels on average.
    /// </summary>
    [Fact]
    public void Nothing_shows_through_a_wide_highway()
    {
        Assert.SkipWhen(!GoldenFrame.GpuTestsEnabled,
            "GPU headless test — set DOTCHARTER_GPU_TESTS=1 to run.");

        using var r = new HeadlessHighwayRenderer(useRealChartTextures: true);
        var plain = r.Render(Wide(), Width, Height, TimeMs);
        var lit = r.Render(Wide(Sl(0, 35), Sl(0, 55), Sl(0, ShowlightPalette.LasersOn)), Width, Height, TimeMs);
        GoldenFrame.Dump(plain, Width, Height, "wide_anchor_5x15_nolights");
        GoldenFrame.Dump(lit, Width, Height, "wide_anchor_5x15");

        // The highway's far edge spans x 738–866 at y 468, x 528–1122 at y 700.
        double sum = 0;
        var n = 0;
        var max = 0;
        for (var y = 472; y < 600; y++)
        {
            var t = (y - 468) / 232.0;
            var x0 = (int)(738 - 210 * t) + 8;
            var x1 = (int)(866 + 256 * t) - 8;
            for (var x = x0; x < x1; x++)
            {
                var o = (y * Width + x) * 4;
                var d = Math.Max(Math.Abs(lit[o] - plain[o]),
                    Math.Max(Math.Abs(lit[o + 1] - plain[o + 1]), Math.Abs(lit[o + 2] - plain[o + 2])));
                sum += d;
                max = Math.Max(max, d);
                n++;
            }
        }

        var mean = sum / n;
        Assert.True(mean <= 6, $"the lights show through the wide highway's lanes: mean {mean:F1}, max {max}");
    }

    [Fact]
    public void Lasers_on_add_the_laser_fans()
    {
        Assert.SkipWhen(!GoldenFrame.GpuTestsEnabled,
            "GPU headless test — set DOTCHARTER_GPU_TESTS=1 to run.");

        using var r = new HeadlessHighwayRenderer(useRealChartTextures: true);
        var off = r.Render(Lit(30, 55, false), Width, Height, TimeMs);
        var on = r.Render(Lit(30, 55, true), Width, Height, TimeMs);
        var fan = MaxDelta(on, off, 0, 200, 200, 345);
        Assert.True(fan > 60, $"the laser fans add at most {fan} luma near the left edge");
    }

    [Fact]
    public void The_lights_freeze_with_the_song_and_move_with_it()
    {
        Assert.SkipWhen(!GoldenFrame.GpuTestsEnabled,
            "GPU headless test — set DOTCHARTER_GPU_TESTS=1 to run.");

        using var r = new HeadlessHighwayRenderer(useRealChartTextures: true);
        var a = r.Render(Lit(35, 55, true), Width, Height, TimeMs);
        Thread.Sleep(40);
        var b = r.Render(Lit(35, 55, true), Width, Height, TimeMs);
        Assert.True(MaxDelta(a, b, 0, Width, 0, 400) <= 2, "the same song time drew a different stage");
        GoldenFrame.Dump(a, Width, Height, "vk_stage_motion_0");
        for (var i = 1; i <= 2; i++)
        {
            var c = r.Render(Lit(35, 55, true), Width, Height, TimeMs + 500 * i);
            GoldenFrame.Dump(c, Width, Height, $"vk_stage_motion_{i}");
            Assert.True(MaxDelta(a, c, 0, Width, 0, 400) > 20, "the lights did not move with the song");
        }
    }

    private static FrameSnapshot Wide(params RenderShowlight[] lights)
    {
        var s = Chart(lights);
        s.Anchors = [new RenderAnchor { Time = 0, EndTime = 20000, Fret = 5, Width = 15 }];
        var notes = new List<RenderNote>();
        for (var i = 0; i < 30; i++)
            notes.Add(new RenderNote { Time = TimeMs + 200 + i * 150, StringIndex = (byte)(i % 6), Fret = (byte)(5 + i % 15) });
        s.Notes = notes.ToArray();
        s.NoteCount = notes.Count;
        s.ChordCount = 0;
        return s;
    }

    private static FrameSnapshot Lit(int fog, int beam, bool lasers)
    {
        var laser = lasers ? ShowlightPalette.LasersOn : ShowlightPalette.LasersOff;
        var s = Chart(Sl(0, fog), Sl(0, beam), Sl(0, laser));
        s.ShowlightChipText = ChipLabels.Compose(fog, beam, laser);
        return s;
    }

    private static int CountBright(byte[] p, int x0, int x1, int y0, int y1, double threshold)
    {
        var n = 0;
        for (var y = y0; y < y1; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                if (Luma(p, (y * Width + x) * 4) > threshold) n++;
            }
        }

        return n;
    }

    private static RenderShowlight Sl(double time, int note)
    {
        ShowlightCatalog.TryGetDefinition(null, note, out var def);
        var c = ShowlightPalette.ColorFromNote(note);
        return new RenderShowlight
        {
            Time = time,
            Note = note,
            Lane = def.Lane switch
            {
                ShowlightLane.Fog => RenderShowlightLane.Fog,
                ShowlightLane.Beams => RenderShowlightLane.Beams,
                _ => RenderShowlightLane.Lasers
            },
            R = c.R, G = c.G, B = c.B
        };
    }

    private static FrameSnapshot Chart(params RenderShowlight[] lights)
    {
        var s = new FrameSnapshot
        {
            StringCount = 6,
            HighwayScrollSpeed = 1.3f,
            Anchors = [new RenderAnchor { Time = 0, EndTime = 20000, Fret = 3, Width = 4 }],
            AnchorCount = 1,
            Showlights = lights,
            ShowlightCount = lights.Length
        };
        var beats = new List<RenderBeat>();
        for (var i = 0; i < 24; i++)
            beats.Add(new RenderBeat { Time = i * 500 - 50, IsMeasure = i % 4 == 0, Bpm = 120, BeatsInMeasure = 4, NoteDenominator = 4 });
        s.Beats = beats.ToArray();
        s.BeatCount = beats.Count;
        var notes = new List<RenderNote>();
        for (var i = 0; i < 14; i++)
            notes.Add(new RenderNote { Time = TimeMs + 300 + i * 260, StringIndex = (byte)(i % 6), Fret = (byte)(2 + i % 4) });
        s.Notes = notes.ToArray();
        s.NoteCount = notes.Count;
        s.Templates = [new RenderChordTemplate { Id = 0, Name = "G5" }];
        s.TemplateCount = 1;
        s.Chords =
        [
            new RenderChord { Time = TimeMs + 900, TemplateId = 0, NoteOffset = 0, NoteCount = 3 },
            new RenderChord { Time = TimeMs + 2400, TemplateId = 0, NoteOffset = 3, NoteCount = 3 }
        ];
        s.ChordCount = s.Chords.Length;
        s.ChordNotes =
        [
            new RenderChordNote { StringIndex = 0, Fret = 3, Sustain = 300 },
            new RenderChordNote { StringIndex = 1, Fret = 5, Sustain = 300 },
            new RenderChordNote { StringIndex = 2, Fret = 5, Sustain = 300 },
            new RenderChordNote { StringIndex = 0, Fret = 3 },
            new RenderChordNote { StringIndex = 1, Fret = 5 },
            new RenderChordNote { StringIndex = 2, Fret = 5 }
        ];
        s.ChordNoteCount = s.ChordNotes.Length;
        return s;
    }

    private static double Luma(byte[] p, int o) => 0.0722 * p[o] + 0.7152 * p[o + 1] + 0.2126 * p[o + 2];

    private static double MeanDelta(byte[] a, byte[] b, int x0, int x1, int y0, int y1)
    {
        double sum = 0;
        var n = 0;
        for (var y = y0; y < y1; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                var o = (y * Width + x) * 4;
                sum += Luma(a, o) - Luma(b, o);
                n++;
            }
        }

        return sum / n;
    }

    private static int MaxDelta(byte[] a, byte[] b, int x0, int x1, int y0, int y1)
    {
        var max = 0;
        for (var y = y0; y < y1; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                var o = (y * Width + x) * 4;
                max = Math.Max(max, (int)Math.Round(Math.Abs(Luma(a, o) - Luma(b, o))));
            }
        }

        return max;
    }

    /// <summary>Mean (B, G, R) gain of <paramref name="a" /> over <paramref name="b" />.</summary>
    private static (double B, double G, double R) MeanColourDelta(byte[] a, byte[] b, int x0, int x1, int y0, int y1)
    {
        double sb = 0, sg = 0, sr = 0;
        var n = 0;
        for (var y = y0; y < y1; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                var o = (y * Width + x) * 4;
                sb += a[o] - b[o];
                sg += a[o + 1] - b[o + 1];
                sr += a[o + 2] - b[o + 2];
                n++;
            }
        }

        return (sb / n, sg / n, sr / n);
    }

    private static int Dominant((double B, double G, double R) c) =>
        c.B >= c.G && c.B >= c.R ? 0 : c.G >= c.R ? 1 : 2;

    private static bool Correlates((double B, double G, double R) gain, (byte B, byte G, byte R) colour)
    {
        double[] g = [gain.B, gain.G, gain.R];
        double[] c = [colour.B, colour.G, colour.R];
        double gm = g.Average(), cm = c.Average();
        double num = 0, dg = 0, dc = 0;
        for (var i = 0; i < 3; i++)
        {
            num += (g[i] - gm) * (c[i] - cm);
            dg += (g[i] - gm) * (g[i] - gm);
            dc += (c[i] - cm) * (c[i] - cm);
        }

        return dg > 0 && dc > 0 && num / Math.Sqrt(dg * dc) > 0.6;
    }
}
