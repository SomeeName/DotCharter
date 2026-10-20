using DotCharter.Services.Audio.Coordination;
using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Tests.Services.Audio.Coordination;

/// <summary>
///     Play Mode's in-game tones for a <c>.dcsong</c> (#708): Play Mode hands the coordinator the
///     song's file, and a package is not a Rocksmith archive. Its tone bodies travel in its chart,
///     which the open project already holds, so it takes the own-chart branch — read as an
///     archive it would play dry.
/// </summary>
public sealed class RsTonePackageSourceTests
{
    private const string CleanJson =
        """{"Key":"clean","Name":"clean","GearList":{"Amp":{"Type":"Amps","Key":"Amp_TwinReverb","KnobValues":{}}}}""";

    [Theory]
    [InlineData(@"C:\lib\song.dcsong", null)]
    [InlineData(@"C:\lib\SONG.DCSONG", null)]
    [InlineData(@"C:\lib\song_p.psarc", @"C:\lib\song_p.psarc")]
    [InlineData(null, null)]
    public void Only_a_rocksmith_archive_is_read_for_tone_bodies(string? path, string? expected) =>
        Assert.Equal(expected, RsTonePlaybackCoordinator.PackageToneSource(path));

    [Fact]
    public async Task A_package_plays_the_tones_its_chart_carries()
    {
        var project = new ChartProject();
        for (var i = 0; i < 8; i++) project.Beats.Add(new BeatMarker { Time = i * 1000, IsMeasure = i % 4 == 0 });
        project.ToneDefinitions.Add(new ToneDefinition { Key = "clean", Name = "clean", Json = CleanJson });
        var lead = new Arrangement { Name = "Lead", ToneBase = "clean" };
        project.Arrangements.Add(lead);
        var state = new ProjectState { ActiveProject = project, ActiveArrangement = lead };
        var chain = new FakeDspChainService(
            new[] { "amp", "cab" }.Select(id => new DspModuleType(id, id, [new DspParamInfo("p", "p", -100f, 100f, 0f)])).ToList());
        var tones = new RsTonePlaybackCoordinator(chain, new DcRigApplier(chain), state, new FakeConfigurationService());

        var summary = await tones.PrepareForSongAsync(@"C:\lib\Deftones - My Own Summer.dcsong", lead, "lib:MyOwnSummer");

        Assert.True(summary.HasTones);
        Assert.True(tones.PrepareForSong(@"C:\lib\Deftones - My Own Summer.dcsong", lead).HasTones);
    }
}
