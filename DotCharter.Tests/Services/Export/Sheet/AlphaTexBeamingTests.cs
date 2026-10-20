using AlphaTab;
using AlphaTab.Importer;
using AlphaTab.Model;
using DotCharter.Services.Export.Sheet;

namespace DotCharter.Tests.Services.Export.Sheet;

/// <summary>
/// Beaming coverage for the domain → alphaTex exporter. AlphaTab auto-beams a
/// run of consecutive <c>:8</c>/<c>:16</c> beats, so runs beam like Guitar Pro
/// as long as the exporter emits clean, uniform durations. The regression these
/// guard: on a tempo-mapped (non-uniform) beat grid the old measure-wide-average
/// <c>msPerTick</c> quantization drifted grid-aligned notes a tick off the grid,
/// so a straight eighth-note run came out as dotted-16th + tied-64th fragments —
/// which suppresses beaming and renders as individual flagged notes. The fix
/// maps each onset through the measure's real per-beat anchor times.
/// </summary>
[Collection("AlphaTab")]
public class AlphaTexBeamingTests
{
    private static (ChartProject project, Arrangement arr, ChartLevel level) NewProject()
    {
        var project = new ChartProject();
        var arr = new Arrangement { Name = "Lead", StringCount = 6 };
        var level = new ChartLevel();
        arr.Levels.Add(level);
        project.Arrangements.Add(arr);
        return (project, arr, level);
    }

    private static List<BeatMarker> Beats(params double[] times)
    {
        var beats = new List<BeatMarker>();
        foreach (var t in times)
            beats.Add(new BeatMarker { Time = t, Bpm = 120, BeatsInMeasure = 4, NoteDenominator = 4 });
        return beats;
    }

    private static List<BeatMarker> UniformBeats(int count, double msPerBeat = 500)
    {
        var t = new double[count];
        for (var i = 0; i < count; i++) t[i] = i * msPerBeat;
        return Beats(t);
    }

    /// <summary>Places <paramref name="subdiv"/> notes per beat across all four
    /// beats of the first bar, exactly on the fractional grid.</summary>
    private static void FillRun(ChartLevel level, int subdiv)
    {
        for (var beat = 0; beat < 4; beat++)
        {
            for (var k = 0; k < subdiv; k++)
            {
                level.Notes.Add(new GuitarNote
                {
                    Position = new FractionalPosition(beat, new Fraction(k, subdiv)),
                    StringIndex = 0, Fret = 3
                });
            }
        }
    }

    private static List<Beat> FirstBarBeats(string tex)
    {
        var importer = new AlphaTexImporter();
        importer.InitFromString(tex, new Settings());
        var score = importer.ReadScore();
        var bar = score.Tracks[0].Staves[0].Bars[0];
        return bar.Voices[0].Beats.ToList();
    }

    [Fact]
    public void EighthRun_OnUniformGrid_EmitsCleanBeamableEighths()
    {
        var (project, arr, level) = NewProject();
        FillRun(level, subdiv: 2); // 8 eighths

        var tex = AlphaTexExporter.Export(project, arr, level, UniformBeats(8)).AlphaTex;

        var beats = FirstBarBeats(tex);
        Assert.Equal(8, beats.Count);
        Assert.All(beats, b => Assert.Equal(Duration.Eighth, b.Duration));
        Assert.All(beats, b => Assert.False(b.IsRest));
    }

    [Fact]
    public void EighthRun_OnTempoMappedGrid_StaysCleanBeamableEighths()
    {
        // A real tempo-mapped grid is NOT perfectly uniform: a few ms of wobble
        // per beat. The notes still sit on exact eighth subdivisions.
        var (project, arr, level) = NewProject();
        FillRun(level, subdiv: 2);

        var beats = Beats(0, 511, 998, 1495, 2020, 2498, 3005, 3491);
        var tex = AlphaTexExporter.Export(project, arr, level, beats).AlphaTex;

        // No dotted/tied/finer fragments should appear in the run.
        Assert.DoesNotContain("{d}", tex);
        Assert.DoesNotContain("-.", tex);   // no within-run ties
        Assert.DoesNotContain(":64", tex);  // no 64th slivers
        Assert.DoesNotContain(":16", tex);  // a straight eighth run is all :8

        var parsed = FirstBarBeats(tex);
        Assert.Equal(8, parsed.Count);
        Assert.All(parsed, b => Assert.Equal(Duration.Eighth, b.Duration));
        Assert.All(parsed, b => Assert.False(b.IsRest));
    }

    [Fact]
    public void SixteenthRun_OnTempoMappedGrid_StaysCleanBeamableSixteenths()
    {
        var (project, arr, level) = NewProject();
        FillRun(level, subdiv: 4); // 16 sixteenths

        var beats = Beats(0, 511, 998, 1495, 2020, 2498, 3005, 3491);
        var tex = AlphaTexExporter.Export(project, arr, level, beats).AlphaTex;

        Assert.DoesNotContain("{d}", tex);
        Assert.DoesNotContain("-.", tex);
        Assert.DoesNotContain(":64", tex);
        Assert.DoesNotContain(":32", tex);

        var parsed = FirstBarBeats(tex);
        Assert.Equal(16, parsed.Count);
        Assert.All(parsed, b => Assert.Equal(Duration.Sixteenth, b.Duration));
    }

    [Fact]
    public void Gallop_OnTempoMappedGrid_KeepsEighthPlusTwoSixteenthsPerBeat()
    {
        // eighth + two sixteenths per beat — a common figure that must keep its
        // exact rhythm (and therefore its beam group) on an uneven grid.
        var (project, arr, level) = NewProject();
        for (var beat = 0; beat < 4; beat++)
        {
            level.Notes.Add(new GuitarNote { Position = new FractionalPosition(beat, new Fraction(0, 4)), StringIndex = 0, Fret = 3 });
            level.Notes.Add(new GuitarNote { Position = new FractionalPosition(beat, new Fraction(2, 4)), StringIndex = 0, Fret = 5 });
            level.Notes.Add(new GuitarNote { Position = new FractionalPosition(beat, new Fraction(3, 4)), StringIndex = 0, Fret = 7 });
        }

        var beats = Beats(0, 511, 998, 1495, 2020, 2498, 3005, 3491);
        var tex = AlphaTexExporter.Export(project, arr, level, beats).AlphaTex;

        Assert.DoesNotContain("{d}", tex);
        Assert.DoesNotContain("-.", tex);
        Assert.DoesNotContain(":64", tex);

        var parsed = FirstBarBeats(tex);
        Assert.Equal(12, parsed.Count); // 3 notes × 4 beats
        for (var i = 0; i < 12; i += 3)
        {
            Assert.Equal(Duration.Eighth, parsed[i].Duration);
            Assert.Equal(Duration.Sixteenth, parsed[i + 1].Duration);
            Assert.Equal(Duration.Sixteenth, parsed[i + 2].Duration);
        }
    }
}
