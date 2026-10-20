using DotCharter.Rendering.Data;
using DotCharter.Rendering.Data.Render;
using DotCharter.Rendering.Headless;
using DotCharter.Rendering.Vulkan;

namespace DotCharter.Tests.Rendering.Headless;

/// <summary>
///     Seed golden-frame tests proving the Vulkan headless capability (the 3D
///     highway). Each test renders the REAL <c>Fretboard3DEngine</c> frame path
///     into an offscreen image and asserts STRUCTURAL properties (see
///     <see cref="GoldenFrame" /> for the tolerance policy). GPU-gated:
///     <c>DOTCHARTER_GPU_TESTS=1</c>. When the Khronos validation layer is
///     installed it is enabled and asserted zero-error per frame; when it is not
///     installed the assertion is skipped gracefully.
/// </summary>
[Collection("HeadlessGpu")]
public class HeadlessVulkanRenderTests
{
    private const int Width = 640;
    private const int Height = 360;

    public HeadlessVulkanRenderTests() =>
        // Enable the validation layer for every Vulkan instance created after
        // this point. Honored only if the layer is actually installed (this
        // machine may not have it — then Active stays false and the per-frame
        // validation assertion no-ops).
        Environment.SetEnvironmentVariable("DOTCHARTER_VK_VALIDATION", "1");

    /// <summary>Small deterministic 6-string fixture: one wide anchor, a beat/
    /// measure grid, and a run of notes across all six strings ahead of the
    /// playhead. Time-driven fields are pinned by the caller's fixed timeMs.</summary>
    private static FrameSnapshot BuildFixtureChart()
    {
        var s = new FrameSnapshot
        {
            StringCount = 6,
            HighwayScrollSpeed = 1.3f,
            Anchors =
            [
                new RenderAnchor { Time = 0, EndTime = 20000, Fret = 1, Width = 5 }
            ]
        };
        s.AnchorCount = s.Anchors.Length;

        var beats = new List<RenderBeat>();
        for (var i = 0; i < 48; i++)
        {
            beats.Add(new RenderBeat
            {
                Time = i * 500, IsMeasure = i % 4 == 0, Bpm = 120,
                BeatsInMeasure = 4, NoteDenominator = 4, MeasureNumber = i / 4
            });
        }

        s.Beats = beats.ToArray();
        s.BeatCount = beats.Count;

        var notes = new List<RenderNote>();
        for (var i = 0; i < 18; i++)
        {
            notes.Add(new RenderNote
            {
                Time = 300 + i * 220,
                StringIndex = (byte)(i % 6),
                Fret = (byte)(1 + i % 5),
                Sustain = i % 3 == 0 ? 180 : 0
            });
        }

        s.Notes = notes.ToArray();
        s.NoteCount = notes.Count;

        return s;
    }

    /// <summary>6-string fixture WITH CHORDS and an imminent hand-position change,
    /// exercising the board-face depth stack (fret bars vs inlay dots), the
    /// active-string highlight (upcoming notes/chord notes brighten their string)
    /// and the upcoming-anchor floor sweep. Time-driven fields are pinned by the
    /// caller's fixed timeMs.</summary>
    private static FrameSnapshot BuildChordFixtureChart()
    {
        var s = BuildFixtureChart();

        // A hand-position change 700 ms out (fret 1 → fret 7): inside the 1000 ms
        // anchor-preview lead, so the upcoming-anchor sweep/preview is active.
        s.Anchors =
        [
            new RenderAnchor { Time = 0, EndTime = 700, Fret = 1, Width = 5 },
            new RenderAnchor { Time = 700, EndTime = 20000, Fret = 7, Width = 4 }
        ];
        s.AnchorCount = s.Anchors.Length;

        s.Templates = [new RenderChordTemplate { Id = 0, Name = "C" }];
        s.TemplateCount = 1;

        // Two 3-note chords: one inside the highlight window (strings 0–2), one
        // further out on the highway (strings 3–5). Sustains force the chord
        // notes to render.
        s.Chords =
        [
            new RenderChord { Time = 400, TemplateId = 0, NoteOffset = 0, NoteCount = 3 },
            new RenderChord { Time = 1300, TemplateId = 0, NoteOffset = 3, NoteCount = 3 }
        ];
        s.ChordCount = s.Chords.Length;
        s.ChordNotes =
        [
            new RenderChordNote { StringIndex = 0, Fret = 3, Sustain = 200 },
            new RenderChordNote { StringIndex = 1, Fret = 3, Sustain = 200 },
            new RenderChordNote { StringIndex = 2, Fret = 4, Sustain = 200 },
            new RenderChordNote { StringIndex = 3, Fret = 5, Sustain = 200 },
            new RenderChordNote { StringIndex = 4, Fret = 5, Sustain = 200 },
            new RenderChordNote { StringIndex = 5, Fret = 7, Sustain = 200 }
        ];
        s.ChordNoteCount = s.ChordNotes.Length;

        return s;
    }

    [Fact]
    public void ChordsAnchorsAndStringHighlight_Render()
    {
        Assert.SkipWhen(!GoldenFrame.GpuTestsEnabled,
            "GPU headless test — set DOTCHARTER_GPU_TESTS=1 to run.");

        using var r = new HeadlessHighwayRenderer();
        var snap = BuildChordFixtureChart();
        var px = r.Render(snap, Width, Height, timeMs: 0);
        GoldenFrame.Dump(px, Width, Height, "vk_chords_verify");

        var nonBlack = GoldenFrame.NonBlackRatio(px);
        var colorful = GoldenFrame.ColorfulRatio(px);
        var boardRegion = GoldenFrame.NonBlackRatioInRegion(px, Width, Height, 0.15, 0.45, 0.85, 1.0);
        try
        {
            Assert.True(nonBlack > 0.05, $"Frame is ~all black (non-black ratio {nonBlack:P1}).");
            Assert.True(colorful > 0.004,
                $"No saturated string/gem/chord colour present (colourful ratio {colorful:P2}).");
            Assert.True(boardRegion > 0.2,
                $"Lower-centre board region is sparse (non-black {boardRegion:P1}).");
        }
        catch
        {
            GoldenFrame.Attach(px, Width, Height, "vk_chords_verify_fail");
            throw;
        }

        AssertNoValidationErrors("chords/anchors/highlight render");
    }

    private static void AssertNoValidationErrors(string when)
    {
        // Only meaningful when the layer was requested AND found.
        if (!VulkanValidationDiagnostics.Active) return;
        Assert.True(VulkanValidationDiagnostics.ErrorCount == 0,
            $"Vulkan validation errors after {when}: {VulkanValidationDiagnostics.ErrorCount}. " +
            $"Last: {VulkanValidationDiagnostics.LastError}");
    }

    [Fact]
    public void RendersFixtureChart_BoardStringsAndGemsVisible()
    {
        Assert.SkipWhen(!GoldenFrame.GpuTestsEnabled,
            "GPU headless test — set DOTCHARTER_GPU_TESTS=1 to run.");

        using var r = new HeadlessHighwayRenderer();
        var snap = BuildFixtureChart();
        var px = r.Render(snap, Width, Height, timeMs: 0);
        GoldenFrame.Dump(px, Width, Height, "vk_fixture");

        // Dimensions are genuinely stable — a byte-exact check is fair here.
        Assert.Equal(Width * Height * 4, px.Length);

        var nonBlack = GoldenFrame.NonBlackRatio(px);
        var colorful = GoldenFrame.ColorfulRatio(px);
        var boardRegion = GoldenFrame.NonBlackRatioInRegion(px, Width, Height, 0.15, 0.45, 0.85, 1.0);
        try
        {
            Assert.True(nonBlack > 0.05, $"Frame is ~all black (non-black ratio {nonBlack:P1}).");
            Assert.True(colorful > 0.004,
                $"No saturated string/gem colour present (colourful ratio {colorful:P2}).");
            Assert.True(boardRegion > 0.2,
                $"Lower-centre board region is sparse (non-black {boardRegion:P1}) — board/strings/gems missing.");
        }
        catch
        {
            GoldenFrame.Attach(px, Width, Height, "vk_fixture_fail");
            throw;
        }

        AssertNoValidationErrors("fixture render");
    }

    [Fact]
    public void CameraFill_80_100_120_ProduceDistinctFramings()
    {
        Assert.SkipWhen(!GoldenFrame.GpuTestsEnabled,
            "GPU headless test — set DOTCHARTER_GPU_TESTS=1 to run.");

        using var r = new HeadlessHighwayRenderer();

        byte[] Render(int fill)
        {
            var snap = BuildFixtureChart();
            snap.Preview3DCameraFill = fill;
            return r.Render(snap, Width, Height, timeMs: 0);
        }

        var f80 = Render(80);
        var f100 = Render(100);
        var f120 = Render(120);
        GoldenFrame.Dump(f80, Width, Height, "vk_fill80");
        GoldenFrame.Dump(f100, Width, Height, "vk_fill100");
        GoldenFrame.Dump(f120, Width, Height, "vk_fill120");

        var d80 = GoldenFrame.DifferenceRatio(f100, f80);
        var d120 = GoldenFrame.DifferenceRatio(f100, f120);
        try
        {
            // All three must render, and the fill knob must visibly change the
            // framing (more than AA jitter would).
            Assert.True(GoldenFrame.NonBlackRatio(f100) > 0.05, "100% fill frame is ~all black.");
            Assert.True(d80 > 0.02, $"80% fill framing barely differs from 100% (diff {d80:P1}).");
            Assert.True(d120 > 0.02, $"120% fill framing barely differs from 100% (diff {d120:P1}).");
        }
        catch
        {
            GoldenFrame.Attach(f80, Width, Height, "vk_fill80_fail");
            GoldenFrame.Attach(f100, Width, Height, "vk_fill100_fail");
            GoldenFrame.Attach(f120, Width, Height, "vk_fill120_fail");
            throw;
        }

        AssertNoValidationErrors("camera-fill renders");
    }

    [Fact]
    public void FogAtmosphere_NoOpaqueEdgeBand()
    {
        Assert.SkipWhen(!GoldenFrame.GpuTestsEnabled,
            "GPU headless test — set DOTCHARTER_GPU_TESTS=1 to run.");

        using var r = new HeadlessHighwayRenderer();
        var snap = BuildFixtureChart();
        // Fog + crowd background are on by default in the fixture snapshot.
        var px = r.Render(snap, Width, Height, timeMs: 0);

        // Regression guard for the 2026-07 fog/atmosphere fixes (7db0535a): the
        // background must fade vertically (sky → horizon), never collapse into a
        // flat opaque vertical band at the frame edges. A hard edge band has ~0
        // luminance spread down the column; the atmospheric gradient has plenty.
        var left = GoldenFrame.ColumnLuminanceSpread(px, Width, Height, 0);
        var right = GoldenFrame.ColumnLuminanceSpread(px, Width, Height, Width - 1);
        try
        {
            Assert.True(left > 8, $"Left frame edge is a flat opaque band (luma spread {left}).");
            Assert.True(right > 8, $"Right frame edge is a flat opaque band (luma spread {right}).");
        }
        catch
        {
            GoldenFrame.Attach(px, Width, Height, "vk_fog_edge_fail");
            throw;
        }

        AssertNoValidationErrors("fog/atmosphere render");
    }
}
