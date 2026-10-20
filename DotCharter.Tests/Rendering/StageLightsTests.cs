using Avalonia.Media;
using DotCharter.Rendering.Data;
using DotCharter.Rendering.Data.Render;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     The stage lights' pure mapping: showlight state at a SONG time → haze colour,
///     beam colour and level, plus, laser fans and brightness.
/// </summary>
public class StageLightsTests
{
    private static RenderShowlight Sl(double time, int note)
    {
        ShowlightCatalog.TryGetDefinition(null, note, out var def);
        var lane = def.Lane switch
        {
            ShowlightLane.Fog => RenderShowlightLane.Fog,
            ShowlightLane.Beams => RenderShowlightLane.Beams,
            _ => RenderShowlightLane.Lasers
        };
        var c = ShowlightPalette.ColorFromNote(note);
        return new RenderShowlight { Time = time, Note = note, Lane = lane, R = c.R, G = c.G, B = c.B };
    }

    private static StageLightsState At(double timeMs, RenderShowlight[] lights, bool fog = true, bool beams = true,
        float energy = CrowdBackgroundPalette.NeutralEnergy) =>
        StageLights.Resolve(lights, lights.Length, timeMs, energy, fog, beams);

    [Fact]
    public void No_showlights_means_no_lights() => Assert.False(At(1000, []).Visible);

    [Fact]
    public void Fog_alone_lights_the_haze_and_leaves_the_heads_idle()
    {
        var s = At(1000, [Sl(0, 24)]); // fog green, no beam event
        Assert.True(s.Visible);
        Assert.Equal(ShowlightPalette.FogColor(24), s.Haze);
        Assert.Equal(1f, s.HazeLevel);
        Assert.Equal(0f, s.BeamLevel);
        Assert.Equal(StageLights.IdleHead, s.Beam);
        Assert.Equal(0f, s.Lasers);
    }

    [Fact]
    public void A_beam_colours_the_cones_and_beams_off_turns_them_off()
    {
        var on = At(1000, [Sl(0, 24), Sl(0, 49)]); // fog green, beams pink
        Assert.Equal(ShowlightPalette.BeamColor(49), on.Beam);
        Assert.Equal(1f, on.BeamLevel);

        var off = At(1000, [Sl(0, 24), Sl(0, ShowlightPalette.BeamsOff)]);
        Assert.True(off.Visible, "beams off still shows the idle heads");
        Assert.Equal(0f, off.BeamLevel);
        Assert.Equal(StageLights.IdleHead, off.Beam);
    }

    [Fact]
    public void A_plus_beam_is_brighter_but_not_garish()
    {
        var plain = At(1000, [Sl(0, 35), Sl(0, 53)]);
        var plus = At(1000, [Sl(0, 35), Sl(0, 58)]);
        Assert.Equal(0f, plain.Plus);
        Assert.Equal(1f, plus.Plus);
        Assert.True(plus.BeamGain > plain.BeamGain * 1.2f, $"plus gain {plus.BeamGain} vs {plain.BeamGain}");
        Assert.True(plus.BeamGain <= plain.BeamGain * 1.5f, "a plus beam must not blow out");
    }

    [Fact]
    public void Lasers_on_light_the_fans_in_the_beam_colour_or_cyan()
    {
        var withBeam = At(1000, [Sl(0, 30), Sl(0, 55), Sl(0, ShowlightPalette.LasersOn)]);
        Assert.Equal(1f, withBeam.Lasers);
        Assert.Equal(StageLights.Mix(ShowlightPalette.BeamColor(55), Colors.White, 0.25f), withBeam.Laser);

        var noBeam = At(1000, [Sl(0, 30), Sl(0, ShowlightPalette.BeamsOff), Sl(0, ShowlightPalette.LasersOn)]);
        Assert.Equal(StageLights.DefaultLaser, noBeam.Laser);

        Assert.Equal(0f, At(1000, [Sl(0, 30), Sl(0, ShowlightPalette.LasersOff)]).Lasers);
        Assert.Equal(0f, At(1200, [Sl(0, 30), Sl(500, ShowlightPalette.LasersOn), Sl(900, ShowlightPalette.LasersOff)])
            .Lasers);
    }

    [Fact]
    public void The_toggles_keep_working()
    {
        var lights = new[] { Sl(0, 24), Sl(0, 49), Sl(0, ShowlightPalette.LasersOn) };
        var noBeams = At(1000, lights, beams: false);
        Assert.True(noBeams.Visible);
        Assert.Equal(0f, noBeams.BeamLevel);
        Assert.Equal(0f, noBeams.Lasers);

        var noFog = At(1000, lights, fog: false);
        Assert.True(noFog.Visible, "beams alone still light the stage");
        Assert.Equal(0f, noFog.HazeLevel);
        Assert.Equal(1f, noFog.BeamLevel);

        Assert.False(At(1000, lights, fog: false, beams: false).Visible);
    }

    [Fact]
    public void A_new_event_takes_over_within_the_transition_window()
    {
        var lights = new[] { Sl(0, 24), Sl(1000, 32) }; // green → red at 1 s
        var green = ShowlightPalette.FogColor(24);
        var red = ShowlightPalette.FogColor(32);

        Assert.Equal(green, At(999, lights).Haze);
        var mid = At(1000 + StageLights.TransitionMs / 2, lights).Haze;
        Assert.NotEqual(green, mid);
        Assert.NotEqual(red, mid);
        Assert.Equal(red, At(1000 + StageLights.TransitionMs, lights).Haze);
        Assert.True(StageLights.TransitionMs <= 150);

        var beams = new[] { Sl(0, 24), Sl(0, 49), Sl(1000, ShowlightPalette.BeamsOff) };
        var fading = At(1000 + StageLights.TransitionMs / 2, beams).BeamLevel;
        Assert.InRange(fading, 0.1f, 0.9f);
        Assert.Equal(0f, At(1000 + StageLights.TransitionMs, beams).BeamLevel);
    }

    [Fact]
    public void The_state_is_a_function_of_song_time_alone()
    {
        var lights = new[] { Sl(0, 24), Sl(0, 49), Sl(0, ShowlightPalette.LasersOn), Sl(2000, 35) };
        var a = At(2050, lights);
        Thread.Sleep(30);
        var b = At(2050, lights);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Crowd_energy_scales_the_brightness_and_neutral_is_one()
    {
        var lights = new[] { Sl(0, 24) };
        Assert.Equal(1f, At(0, lights).Brightness, 3);
        Assert.True(At(0, lights, energy: 1f).Brightness > At(0, lights, energy: 0f).Brightness);
    }

    [Fact]
    public void The_light_code_never_reads_the_wall_clock()
    {
        var root = TestPaths.RepoRoot();
        string[] files =
        [
            Path.Combine(root, "DotCharter.Rendering", "Components", "StageLightsRenderer.cs"),
            Path.Combine(root, "DotCharter.Rendering", "Data", "StageLights.cs"),
            Path.Combine(root, "DotCharter.Rendering", "Data", "StageLightsShape.cs")
        ];
        foreach (var f in files)
        {
            var src = File.ReadAllText(f);
            Assert.DoesNotContain("Environment.TickCount", src);
            Assert.DoesNotContain("DateTime.", src);
            Assert.DoesNotContain("Stopwatch", src);
        }
    }

    [Fact]
    public void The_old_drawers_are_gone()
    {
        var root = TestPaths.RepoRoot();
        Assert.False(File.Exists(Path.Combine(root, "DotCharter.Rendering", "Components", "ShowlightsRenderComponent.cs")));
        Assert.False(File.Exists(Path.Combine(root, "DotCharter.Rendering", "Components", "AuroraSkyRenderer.cs")));
        var palette = File.ReadAllText(Path.Combine(root, "DotCharter.Rendering", "Data", "CrowdBackgroundPalette.cs"));
        Assert.DoesNotContain("StageGlow", palette);
    }

    [Fact]
    public void Mix_ends_on_its_endpoints()
    {
        Assert.Equal(Colors.Red, StageLights.Mix(Colors.Red, Colors.Blue, 0f));
        Assert.Equal(Colors.Blue, StageLights.Mix(Colors.Red, Colors.Blue, 1f));
    }
}
