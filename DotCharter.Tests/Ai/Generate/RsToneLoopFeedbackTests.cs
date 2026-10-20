using DotCharter.Services.Ai.Generate;

namespace DotCharter.Tests.Ai.Generate;

/// <summary>What a revision round tells the model, from the named features of the song and of the render.</summary>
public sealed class RsToneLoopFeedbackTests
{
    private static readonly ToneWindowFeatures Song = new(5, -18, -6, 12, 900, 3000, -7.9, 0.1, -6, 1.2, 0.3, 5.0);

    [Fact]
    public void A_render_that_matches_draws_no_lines() => Assert.Empty(RsToneLoopFeedback.Differences(Song, Song));

    [Fact]
    public void Brighter_squashed_static_and_dry_are_each_named_with_both_numbers()
    {
        var render = Song with { TiltDbPerOctave = -4.0, CrestDb = 7.5, ModulationDepth = 0.02, ModulationHz = double.NaN, DecaySeconds = 0.3 };

        var lines = RsToneLoopFeedback.Differences(Song, render);

        Assert.Equal(4, lines.Count);
        Assert.StartsWith("too bright: spectral tilt -4.0 dB/octave against the song's -7.9", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("too compressed or distorted: crest 7.5 dB against the song's 12.0", lines[1], StringComparison.Ordinal);
        Assert.StartsWith("missing modulation: the song's level moves 0.30 at 5.0 Hz", lines[2], StringComparison.Ordinal);
        Assert.StartsWith("too dry: the song's notes decay over 1.20 s, the render's over 0.30 s", lines[3], StringComparison.Ordinal);
    }

    [Fact]
    public void The_opposite_directions_read_the_other_way()
    {
        var song = Song with { ModulationDepth = 0.01, ModulationHz = double.NaN };
        var render = song with { TiltDbPerOctave = -11, CrestDb = 18, ModulationDepth = 0.4, ModulationHz = 4, DecaySeconds = 3.0 };

        var lines = RsToneLoopFeedback.Differences(song, render);

        Assert.Equal(["too dark", "not compressed or driven enough", "too much modulation", "too wet"], lines.Select(l => l[..l.IndexOf(':')]));
    }

    [Fact]
    public void Differences_inside_the_steps_are_not_feedback()
    {
        var render = Song with
        {
            TiltDbPerOctave = Song.TiltDbPerOctave + RsToneLoopFeedback.TiltStep - 0.1,
            CrestDb = Song.CrestDb - RsToneLoopFeedback.CrestStep + 0.1,
            ModulationDepth = 0.2,
            DecaySeconds = 2.0
        };

        Assert.Empty(RsToneLoopFeedback.Differences(Song, render));
    }
}
