using DotCharter.Services.Audio.Coordination;
using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.Parsing.Psarc;

namespace DotCharter.Tests.Services.Audio.Coordination;

/// <summary>
///     The song-tone edit: one of a package's tones held on the rig, the player's
///     board put back afterwards, and a SAVED edit playing instead of the
///     conversion — in the run and in the next edit.
/// </summary>
public sealed class RsTonePlaybackCoordinatorToneEditTests
{
    private static IReadOnlyList<DspModuleType> Catalog() =>
        new[] { "amp", "cab", "gain", "overdrive", "distortion", "ds1", "eq", "reverb", "delay", "chorus", "flanger" }
            .Select(id => new DspModuleType(id, id, new[] { new DspParamInfo("p", "p", -100f, 100f, 0f) }))
            .ToList();

    private const string CleanJson =
        """{"Key":"clean","Name":"Clean Tone","GearList":{"Amp":{"Type":"Amps","Key":"Amp_TwinReverb","KnobValues":{}}}}""";

    private const string HeavyJson =
        """{"Key":"heavy","Name":"heavy","GearList":{"Amp":{"Type":"Amps","Key":"Amp_5150","KnobValues":{}},"PrePedal1":{"Type":"Pedals","Key":"Pedal_FutureFilter","Category":"Filter","KnobValues":{"Pedal_FutureFilter_Sweep":50}}}}""";

    /// <summary>An edit nobody could mistake for a conversion: a lone chorus.</summary>
    private static readonly DcRigPreset EditedRig =
        new([new DcRigModule("chorus", new Dictionary<string, float> { ["p"] = 3f })]);

    private const string Song = "lib:CallMe";

    /// <param name="edits">The saved edits by song identity — the store, in memory.</param>
    /// <param name="state">The project state; beats every second by default.</param>
    private static (RsTonePlaybackCoordinator Coord, FakeDspChainService Chain) NewCoordinator(
        Dictionary<string, Dictionary<string, DcRigPreset>>? edits = null, ProjectState? state = null)
    {
        var chain = new FakeDspChainService(Catalog());
        var coord = new RsTonePlaybackCoordinator(chain, new DcRigApplier(chain), state ?? StateWithBeats(),
            new FakeConfigurationService { AppConfig = { UseInGameTones = true } })
        {
            ToneDefinitionSource = _ =>
            [
                new PsarcToneDefinition("clean", "Clean Tone", CleanJson),
                new PsarcToneDefinition("heavy", "heavy", HeavyJson)
            ],
            SongTonesSource = song => edits is not null && edits.TryGetValue(song, out var tones)
                ? tones
                : []
        };
        return (coord, chain);
    }

    private static ProjectState StateWithBeats()
    {
        var state = new ProjectState();
        for (var i = 0; i < 8; i++)
            state.Beats.Add(new BeatMarker { Time = i * 1000, IsMeasure = i % 4 == 0 });
        return state;
    }

    private static Arrangement Arrangement()
    {
        var arr = new Arrangement { ToneBase = "Clean Tone" }; // the chart names it by NAME
        arr.ToneChanges.Add(new ToneChange { Position = new FractionalPosition(4), ToneName = "heavy" });
        return arr;
    }

    private static Dictionary<string, Dictionary<string, DcRigPreset>> EditOf(string key, string song = Song) =>
        new() { [song] = new Dictionary<string, DcRigPreset>(StringComparer.OrdinalIgnoreCase) { [key] = EditedRig } };

    private static string[] Order(IDspChainService chain) => chain.GetNodes().Select(n => n.ModuleTypeId).ToArray();

    [Fact]
    public async Task BeginToneEdit_PutsTheToneOnTheRig_AndEndToneEdit_GivesThePlayersBoardBack()
    {
        var (coord, chain) = NewCoordinator();
        chain.AddNode("reverb"); // the player's own board
        coord.PrepareForSong("song.psarc", Arrangement(), Song);

        Assert.True(coord.BeginToneEdit("heavy"));
        Assert.Equal(RsToneDriver.SongToneEdit, coord.Driver);
        Assert.Equal("heavy", coord.EditingTone);
        Assert.Contains("amp", Order(chain));
        Assert.DoesNotContain("reverb", Order(chain));

        coord.EndToneEdit();
        await coord.RestoreCompletion;

        Assert.Equal(RsToneDriver.None, coord.Driver);
        Assert.Equal(["reverb"], Order(chain));
    }

    [Fact]
    public void ASavedEdit_ReplacesTheConversion_UnderItsKeyAndItsName()
    {
        var (coord, chain) = NewCoordinator(EditOf("clean"));

        var summary = coord.PrepareForSong("song.psarc", Arrangement(), Song);

        // The chart names the tone "Clean Tone"; the edit was saved under "clean".
        Assert.Equal(["Clean Tone"], summary.EditedToneNames);
        Assert.True(coord.BeginToneEdit("Clean Tone"));
        Assert.Equal(["chorus"], Order(chain));
    }

    [Fact]
    public void ASavedEdit_ClearsThatTonesApproximations()
    {
        var (unedited, _) = NewCoordinator();
        var (edited, _) = NewCoordinator(EditOf("heavy"));

        var before = unedited.PrepareForSong("song.psarc", Arrangement(), Song);
        var after = edited.PrepareForSong("song.psarc", Arrangement(), Song);

        Assert.True(before.HasApproximations); // the unknown filter pedal has no equivalent
        Assert.False(after.HasApproximations);
    }

    [Fact]
    public void TheRunPlaysTheEdit()
    {
        var (coord, chain) = NewCoordinator(EditOf("clean"));
        coord.PrepareForSong("song.psarc", Arrangement(), Song);

        Assert.True(coord.BeginRun()); // playhead 0: the base tone, "Clean Tone"

        Assert.Equal(["chorus"], Order(chain));
        coord.EndRun();
    }

    [Fact]
    public async Task ARunStartedMidEdit_TakesTheRigOver_AndStillRestoresThePlayersBoard()
    {
        var (coord, chain) = NewCoordinator();
        chain.AddNode("reverb");
        coord.PrepareForSong("song.psarc", Arrangement(), Song);
        Assert.True(coord.BeginToneEdit("heavy"));

        Assert.True(coord.BeginRun());
        Assert.Equal(RsToneDriver.PlayModeRun, coord.Driver);
        Assert.False(coord.IsEditingTone);

        coord.EndRun();
        await coord.RestoreCompletion;
        Assert.Equal(["reverb"], Order(chain));
    }

    [Fact]
    public void BeginToneEdit_IsRefused_WhileARunOwnsTheRig_AndForAnUnknownTone()
    {
        var (coord, _) = NewCoordinator();
        coord.PrepareForSong("song.psarc", Arrangement(), Song);

        Assert.False(coord.BeginToneEdit("no-such-tone"));
        Assert.True(coord.BeginRun());
        Assert.False(coord.BeginToneEdit("heavy"));
        Assert.Equal(RsToneDriver.PlayModeRun, coord.Driver);
        coord.EndRun();
    }

    [Fact]
    public void BeginToneEdit_IsRefused_ForAnOwnChart()
    {
        var (coord, _) = NewCoordinator();
        coord.PrepareForSong(null, Arrangement());

        Assert.False(coord.BeginToneEdit("heavy"));
    }

    [Fact]
    public void ResolvePackageToneKey_MapsANameToTheManifestKey()
    {
        var (coord, _) = NewCoordinator();
        coord.PrepareForSong("song.psarc", Arrangement(), Song);

        Assert.Equal("clean", coord.ResolvePackageToneKey("Clean Tone"));
        Assert.Equal("heavy", coord.ResolvePackageToneKey(" HEAVY "));
        Assert.Null(coord.ResolvePackageToneKey("nope"));
    }

    [Fact]
    public void EditsAreLaidOverPerSong_InAPackageThatHoldsSeveralSongs()
    {
        // One package path, two songs: the conversion is cached once for the
        // package, so an edit baked into that cache would leak into the next song.
        var (coord, chain) = NewCoordinator(EditOf("clean", song: "lib:SongA"));

        var a = coord.PrepareForSong("compat_pack_p.psarc", Arrangement(), "lib:SongA");
        var b = coord.PrepareForSong("compat_pack_p.psarc", Arrangement(), "lib:SongB");

        Assert.Equal(["Clean Tone"], a.EditedToneNames);
        Assert.Empty(b.EditedToneNames);
        Assert.True(coord.BeginToneEdit("Clean Tone"));
        Assert.DoesNotContain("chorus", Order(chain)); // song B plays the conversion
        coord.EndToneEdit();
    }

    [Fact]
    public void ASongWithoutAnIdentity_CannotSaveEdits()
    {
        var (coord, _) = NewCoordinator();

        Assert.True(coord.PrepareForSong("song.psarc", Arrangement(), Song).CanSaveToneEdits);
        Assert.False(coord.PrepareForSong("song.psarc", Arrangement()).CanSaveToneEdits);
    }

    [Fact]
    public async Task AfterAReset_ReapplyEditedTone_PutsTheConversionBack()
    {
        var edits = EditOf("heavy");
        var (coord, chain) = NewCoordinator();
        coord.SongTonesSource = song => edits.TryGetValue(song, out var tones) ? tones : [];
        coord.PrepareForSong("song.psarc", Arrangement(), Song);
        Assert.True(coord.BeginToneEdit("heavy"));
        Assert.Equal(["chorus"], Order(chain));

        // The host removed the edit from the store, then re-prepared.
        edits = [];
        coord.PrepareForSong("song.psarc", Arrangement(), Song);
        var applied = new TaskCompletionSource();
        coord.ActiveToneChanged += _ => applied.TrySetResult();

        Assert.True(coord.ReapplyEditedTone());
        await applied.Task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken); // deadlock detector, not a budget

        Assert.Contains("amp", Order(chain));
        Assert.DoesNotContain("chorus", Order(chain));
        coord.EndToneEdit();
    }
}
