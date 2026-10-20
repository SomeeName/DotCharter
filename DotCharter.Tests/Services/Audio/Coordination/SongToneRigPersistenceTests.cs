using DotCharter.Services.Audio.Coordination;
using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.Audio.Dsp.Presets;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Audio.Coordination;

/// <summary>
///     #166: while a song's tone drives the rig (the chart synth, the editor's tone
///     preview, a Play Mode run) the player's tone session must not save the live
///     chain as the player's rig. It used to, every second, so an exit mid-song left
///     the SONG's tone on disk as "my rig" — found on the owner's profile as Muse
///     "Hysteria"'s bass chain. One in-memory chain shared by the real coordinator and
///     the real session, the way the app wires seat 0; the store is a temp directory.
/// </summary>
public sealed class SongToneRigPersistenceTests : IDisposable
{
    private const string CleanJson =
        """{"Key":"clean","Name":"clean","GearList":{"Amp":{"Type":"Amps","Key":"Amp_TwinReverb","KnobValues":{}}}}""";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DotCharterTests", Guid.NewGuid().ToString("N"));

    public void Dispose() => BestEffortDelete.Directory(_dir);

    private static IReadOnlyList<DspModuleType> Catalog() =>
        new[] { "amp", "cab", "gain", "reverb", "delay" }
            .Select(id => new DspModuleType(id, id, [new DspParamInfo("p", "p", -100f, 100f, 0f)]))
            .ToList();

    private string[] Saved() =>
        new ProfileToneRigStore(_dir).GetRig(0)?.Modules.Select(m => m.ModuleTypeId).ToArray() ?? [];

    private (FakeDspChainService Chain, ToneSessionService Session, RsTonePlaybackCoordinator Tones) Wire()
    {
        var chain = new FakeDspChainService(Catalog());
        var config = new FakeConfigurationService();
        config.Update(c => c.DefaultTonePresetId = "");
        var session = new ToneSessionService(chain, config, new DspTonePresetService(chain, config), new ProfileToneRigStore(_dir));

        var project = new ChartProject();
        for (var i = 0; i < 8; i++) project.Beats.Add(new BeatMarker { Time = i * 1000, IsMeasure = i % 4 == 0 });
        project.ToneDefinitions.Add(new ToneDefinition { Key = "clean", Name = "clean", Json = CleanJson });
        var arr = new Arrangement { ToneBase = "clean" };
        project.Arrangements.Add(arr);
        var state = new ProjectState { ActiveProject = project, ActiveArrangement = arr };
        var tones = new RsTonePlaybackCoordinator(chain, new DcRigApplier(chain), state, config);
        tones.PrepareForProject(project, arr);

        // As App.StateServices wires seat 0.
        session.BoardDisplaced = () => tones.IsPlayersBoardDisplaced;

        // The player's own board, saved.
        chain.AddNode("reverb");
        session.Flush();
        Assert.Equal(["reverb"], Saved());
        return (chain, session, tones);
    }

    [Fact]
    public void WhileASongToneDrives_TheSessionSavesNothing_EvenOnAShutdownFlush()
    {
        var (chain, session, tones) = Wire();
        using (session)
        {
            Assert.True(tones.BeginPreview());
            Assert.True(tones.IsPlayersBoardDisplaced);
            Assert.Contains("amp", chain.GetNodes().Select(n => n.ModuleTypeId));

            session.Flush();
            Assert.Equal(["reverb"], Saved());

            // Shutdown: the preview is abandoned (no restore into a dying engine) and the
            // session's last flush runs. The board on disk must still be the player's.
            tones.AbandonPreview();
            Assert.True(tones.IsPlayersBoardDisplaced, "an abandoned drive restored nothing, so the chain is still the song's");
            session.Flush();
            Assert.Equal(["reverb"], Saved());
        }
    }

    [Fact]
    public async Task OnceThePlayersBoardIsBack_ItsEditsAreSavedAgain()
    {
        var (chain, session, tones) = Wire();
        using (session)
        {
            Assert.True(tones.BeginPreview());
            tones.EndPreview();
            await tones.RestoreCompletion.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

            Assert.False(tones.IsPlayersBoardDisplaced);
            Assert.Equal(["reverb"], chain.GetNodes().Select(n => n.ModuleTypeId).ToArray());

            chain.AddNode("delay");
            session.Flush();
            Assert.Equal(["reverb", "delay"], Saved());
        }
    }
}
