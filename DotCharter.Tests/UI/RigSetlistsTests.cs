using Avalonia.Input;
using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.Audio.Dsp.Presets;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.Audio.Dsp.Scenes;
using DotCharter.Tone.UI.ViewModels.PlayMode;
using DotCharter.Tone.UI.Views.PlayMode;

namespace DotCharter.Tests.UI;

/// <summary>
///     SETLISTS and the MIDI performance path, through the rig over a fake chain
///     with the REAL preset service, asserted against the CHAIN and the persisted
///     config: a setlist is made, filled, reordered and survives a restart; the
///     live one steps song to song (starting each on its scene) and stops at its
///     ends; a program change picks song n (0 = the first), or with no setlist the
///     n-th own tone; the scene CC picks a scene; the channel filter and the
///     switches hold. The bar, the panel and the keys through the real view are
///     <c>Perf/RigSceneBarUiPerfSmokeTests</c>.
/// </summary>
public sealed class RigSetlistsTests
{
    // Module ids the REAL target catalogue does not know, on purpose: the preset
    // pipeline fills a KNOWN module's missing params from that catalogue, which the
    // fake chain (with its two-param modules) would then carry as extra values —
    // an artefact no real engine has, whose nodes always report every param.
    private static readonly DspModuleType Drive = new("scenedrive", "Overdrive", [new("drive", "Drive", 0, 1, 0.5f)]);
    private static readonly DspModuleType Delay = new("scenedelay", "Delay", [new("mix", "Mix", 0, 1, 0.3f)]);

    private sealed record Rig(PedalboardViewModel Board, FakeDspChainService Chain, FakeConfigurationService Config, DspTonePresetService Presets)
    {
        public RigSetlistsViewModel Lists => Board.Setlists;
    }

    private static Rig NewRig(FakeConfigurationService? config = null)
    {
        config ??= new FakeConfigurationService();
        var chain = new FakeDspChainService(new[] { Drive, Delay });
        var presets = new DspTonePresetService(chain, config);
        return new Rig(TestRigBuilders.Pedalboard(chain, presets, FakeLocalization.English(), config), chain, config, presets);
    }

    /// <summary>A user tone: one overdrive at <paramref name="drive" />, optionally with scene B at 0.95.</summary>
    private static DspTonePreset Tone(DspTonePresetService presets, string name, float drive, bool withSceneB = false)
    {
        var modules = new List<DcRigModule> { new("scenedrive", new Dictionary<string, float> { ["drive"] = drive }) };
        var rig = new DcRigPreset(modules);
        if (withSceneB)
        {
            rig = rig with
            {
                Scenes = RigScenes.Store(RigScenes.Start(modules), 1,
                    [new DcRigModule("scenedrive", new Dictionary<string, float> { ["drive"] = 0.95f })])
            };
        }

        return presets.SaveUser(name, DspToneCategory.Clean, string.Empty, rig);
    }

    private static float DriveOf(FakeDspChainService chain) => chain.GetNodes().Single().Values["drive"];

    private static void Play(Rig rig, DspTonePreset tone) => rig.Board.Browser.ApplyPreset(tone);

    /// <summary>A setlist of the given tones, made through the panel's own commands.</summary>
    private static RigSetlistViewModel Gig(Rig rig, params DspTonePreset[] tones)
    {
        rig.Lists.NewSetlistCommand.Execute(null);
        foreach (var tone in tones)
        {
            Play(rig, tone);
            rig.Lists.AddCurrentToneCommand.Execute(null);
        }

        return rig.Lists.SelectedSetlist!;
    }

    [Fact]
    public void ASetlist_IsMadeFilledReorderedAndRenamed_AndSurvivesARestart()
    {
        var rig = NewRig();
        using var _ = rig.Board;
        var clean = Tone(rig.Presets, "Clean", 0.1f);
        var crunch = Tone(rig.Presets, "Crunch", 0.5f);
        var lead = Tone(rig.Presets, "Lead", 0.9f);
        var gig = Gig(rig, clean, crunch, lead);
        gig.Name = "Friday";
        rig.Lists.MoveEntryUpCommand.Execute(gig.Entries[2]);        // lead before crunch
        rig.Lists.RemoveEntryCommand.Execute(gig.Entries[0]);        // clean out
        gig.Entries[1].SelectedScene = rig.Lists.SceneChoices[3];    // crunch starts on C
        rig.Lists.ToggleActiveCommand.Execute(null);

        Assert.Equal(["Lead", "Crunch"], gig.Entries.Select(e => e.PresetName));
        Assert.Equal([1, 2], gig.Entries.Select(e => e.Position));

        var again = NewRig(rig.Config);                              // a restart
        using var __ = again.Board;
        var back = Assert.Single(again.Lists.Setlists);
        Assert.Equal("Friday", back.Name);
        Assert.Same(back, again.Lists.ActiveSetlist);
        Assert.Equal([lead.Id, crunch.Id], back.Entries.Select(e => e.PresetId));
        Assert.Equal(2, back.Entries[1].SelectedScene!.Scene);
        Assert.Equal(-1, again.Lists.ActiveIndex);                    // which song was playing is session state

        again.Lists.DeleteSetlistCommand.Execute(null);
        Assert.Empty(rig.Config.LoadAppConfig().RigSetlists);
        Assert.Null(rig.Config.LoadAppConfig().RigActiveSetlistId);
    }

    [Fact]
    public void TheLiveSetlist_StepsSongToSong_AndStopsAtItsEnds()
    {
        var rig = NewRig();
        using var _ = rig.Board;
        var a = Tone(rig.Presets, "A", 0.1f);
        var b = Tone(rig.Presets, "B", 0.2f);
        Gig(rig, a, b);
        Assert.False(rig.Lists.NextCommand.CanExecute(null));        // nothing live yet
        rig.Lists.ToggleActiveCommand.Execute(null);
        Play(rig, Tone(rig.Presets, "Elsewhere", 0.7f));

        rig.Lists.NextCommand.Execute(null);                          // the first song
        Assert.Equal(0.1f, DriveOf(rig.Chain), 3);
        Assert.Equal("1/2", rig.Lists.ActivePosition);
        rig.Lists.NextCommand.Execute(null);
        Assert.Equal(0.2f, DriveOf(rig.Chain), 3);
        rig.Lists.NextCommand.Execute(null);                          // the end: stays
        Assert.Equal(0.2f, DriveOf(rig.Chain), 3);
        Assert.Equal("B", rig.Lists.ActiveEntryName);
        rig.Lists.PreviousCommand.Execute(null);
        rig.Lists.PreviousCommand.Execute(null);                      // the start: stays
        Assert.Equal(0.1f, DriveOf(rig.Chain), 3);
        Assert.True(rig.Lists.SelectedSetlist!.Entries[0].IsCurrent);
        Assert.Equal(b.Id, rig.Lists.ActiveSetlist!.Entries[1].PresetId);
    }

    [Fact]
    public void ASong_StartsOnItsScene_InTheSameApply_WithoutReadingAsAnEdit()
    {
        var rig = NewRig();
        using var _ = rig.Board;
        var scened = Tone(rig.Presets, "Scened", 0.3f, withSceneB: true);
        var gig = Gig(rig, scened);
        gig.Entries[0].SelectedScene = rig.Lists.SceneChoices[2];    // B
        rig.Lists.ToggleActiveCommand.Execute(null);
        Play(rig, Tone(rig.Presets, "Elsewhere", 0.7f));

        rig.Lists.PlayEntryCommand.Execute(gig.Entries[0]);

        Assert.Equal(0.95f, DriveOf(rig.Chain), 3);
        Assert.Equal(1, rig.Board.ActiveSceneIndex);
        Assert.False(RigEditState.IsEdited(rig.Board.BuildCurrentRig(), rig.Board.Browser.ActivePreset!.Rig));
    }

    [Fact]
    public void AMissingTone_SaysSo_AndChangesNothing()
    {
        var rig = NewRig();
        using var _ = rig.Board;
        var gone = Tone(rig.Presets, "Gone", 0.4f);
        Gig(rig, gone);
        rig.Lists.ToggleActiveCommand.Execute(null);
        Play(rig, Tone(rig.Presets, "Here", 0.6f));
        rig.Presets.DeleteUser(gone.Id);
        rig.Lists.RefreshNames();

        rig.Lists.NextCommand.Execute(null);

        Assert.Equal(0.6f, DriveOf(rig.Chain), 3);
        Assert.True(rig.Lists.ActiveSetlist!.Entries[0].IsMissing);
        Assert.False(string.IsNullOrEmpty(rig.Lists.Status));
    }

    // ── MIDI ─────────────────────────────────────────────────────────────────

    [Fact]
    public void AProgramChange_PicksSongN_OfTheLiveSetlist_AndIgnoresOnePastItsEnd()
    {
        var rig = NewRig();
        using var _ = rig.Board;
        var a = Tone(rig.Presets, "A", 0.1f);
        var b = Tone(rig.Presets, "B", 0.2f);
        Gig(rig, a, b);
        rig.Lists.ToggleActiveCommand.Execute(null);

        rig.Board.HandleMidiProgram(0, 1);
        Assert.Equal(0.2f, DriveOf(rig.Chain), 3);
        rig.Board.HandleMidiProgram(0, 0);                            // program 0 = the FIRST song
        Assert.Equal(0.1f, DriveOf(rig.Chain), 3);
        rig.Board.HandleMidiProgram(0, 5);                            // no song 6: ignored, never wrapped
        Assert.Equal(0.1f, DriveOf(rig.Chain), 3);
        Assert.False(string.IsNullOrEmpty(rig.Lists.Status));
    }

    [Fact]
    public void WithNoSetlistLive_AProgramChange_PicksTheNthOwnTone()
    {
        var rig = NewRig();
        using var _ = rig.Board;
        Tone(rig.Presets, "First", 0.1f);
        Tone(rig.Presets, "Second", 0.2f);

        rig.Board.HandleMidiProgram(3, 1);
        Assert.Equal(0.2f, DriveOf(rig.Chain), 3);
        rig.Board.HandleMidiProgram(3, 9);                            // nothing there: unchanged
        Assert.Equal(0.2f, DriveOf(rig.Chain), 3);
    }

    [Fact]
    public void TheChannelFilter_AndTheProgramChangeSwitch_Hold()
    {
        var config = new FakeConfigurationService();
        config.Update(c => c.RigMidiChannel = 2);                     // channel 2 only
        var rig = NewRig(config);
        using var _ = rig.Board;
        Tone(rig.Presets, "First", 0.1f);
        Tone(rig.Presets, "Second", 0.2f);
        rig.Board.Browser.ApplyPreset(rig.Presets.UserPresets[0]);

        rig.Board.HandleMidiProgram(0, 1);                            // channel 1: not for the rig
        Assert.Equal(0.1f, DriveOf(rig.Chain), 3);
        rig.Board.HandleMidiProgram(1, 1);                            // channel 2 (0-based 1)
        Assert.Equal(0.2f, DriveOf(rig.Chain), 3);

        config.Update(c => c.RigMidiProgramChange = false);
        rig.Board.HandleMidiProgram(1, 0);
        Assert.Equal(0.2f, DriveOf(rig.Chain), 3);
    }

    [Fact]
    public void TheSceneCc_PicksAScene_OtherCcsDriveTheKnobs_AndALearnTakesAnything()
    {
        var rig = NewRig();
        using var _ = rig.Board;
        rig.Board.AddGear(Drive);

        rig.Board.HandleMidiControl(4, 69, 2);                        // CC 69 = 2: scene C
        Assert.Equal(2, rig.Board.ActiveSceneIndex);
        rig.Board.HandleMidiControl(4, 69, 127);                      // not a scene: ignored
        Assert.Equal(2, rig.Board.ActiveSceneIndex);
        rig.Board.HandleMidiControl(4, 70, 0);                        // another CC: not a scene
        Assert.Equal(2, rig.Board.ActiveSceneIndex);

        rig.Board.SelectNodeAt(0);
        rig.Board.Controls.Editor.Edit(rig.Board.Nodes[0]);
        rig.Board.Controls.Editor.BeginMidiLearnCommand.Execute(null);
        Assert.True(rig.Board.Controls.Editor.IsLearning);
        rig.Board.HandleMidiControl(0, 69, 0);                        // learning: the pedal points at a knob
        Assert.Equal(2, rig.Board.ActiveSceneIndex);
        Assert.False(rig.Board.Controls.Editor.IsLearning);
    }

    /// <summary>
    ///     A pedal board sends a program change and the scene CC TOGETHER. The tone
    ///     is diffed onto the chain on a worker, so a scene switch taken while it is
    ///     in flight would be built from half of one rig and half of the other —
    ///     and would put that back once the apply let go. The switch waits for the
    ///     tone and lands on it. The worker is HELD inside the apply (the fake
    ///     chain's <c>BeforeAddNode</c>) so the window is certain, not likely.
    /// </summary>
    [Fact]
    public async Task ASceneAskedForWhileAToneIsArriving_LandsOnThatTone()
    {
        var config = new FakeConfigurationService();
        var chain = new FakeDspChainService(new[] { Drive, Delay });
        var presets = new DspTonePresetService(chain, config);
        var ui = new QueueDispatcher();
        using var board = TestRigBuilders.Pedalboard(chain, presets, FakeLocalization.English(), config, dispatcher: ui);
        var scened = new DcRigPreset([
                new DcRigModule("scenedrive", new Dictionary<string, float> { ["drive"] = 0.2f }),
                new DcRigModule("scenedelay", new Dictionary<string, float> { ["mix"] = 0.3f })]);
        scened = scened with
        {
            Scenes = RigScenes.Store(RigScenes.Start(scened.Modules), 2,
            [
                new DcRigModule("scenedrive", new Dictionary<string, float> { ["drive"] = 0.95f }),
                new DcRigModule("scenedelay", new Dictionary<string, float> { ["mix"] = 0.7f })
            ])
        };
        var tone = presets.SaveUser("Song", DspToneCategory.Clean, string.Empty, scened);

        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        chain.BeforeAddNode = () =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken); // a deadlock detector, never a budget
        };

        board.Browser.ApplyPreset(tone);                                 // the program change…
        Assert.True(entered.Wait(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken), "the apply never reached the chain");
        var ask = Task.Run(() => board.SelectSceneAt(2), TestContext.Current.CancellationToken);               // …and the scene CC, mid-apply
        Assert.True(await Task.WhenAny(ask, Task.Delay(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken)) == ask,
            "a scene switch taken mid-apply blocked on the chain instead of waiting for the tone");
        chain.BeforeAddNode = null;
        release.Set();
        await ui.DrainUntil(() => !board.Browser.IsApplying, TestContext.Current.CancellationToken);

        var nodes = chain.GetNodes();
        Assert.Equal(["scenedrive", "scenedelay"], nodes.Select(n => n.ModuleTypeId));
        Assert.Equal(0.95f, nodes[0].Values["drive"], 3);
        Assert.Equal(0.7f, nodes[1].Values["mix"], 3);
        Assert.Equal(2, board.ActiveSceneIndex);
    }

    /// <summary>Posts queue until the test drains them on its own thread.</summary>
    private sealed class QueueDispatcher : DotCharter.Tone.Diagnostics.IUiDispatcher
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _queue = new();
        public bool CheckAccess() => true;
        public void Post(Action action) => _queue.Enqueue(action);
        public void PostNormal(Action action) => _queue.Enqueue(action);
        public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
        public Task<T> InvokeAsync<T>(Func<T> func) => Task.FromResult(func());
        public Task<T> InvokeAsync<T>(Func<Task<T>> func) => func();
        public IDisposable SchedulePeriodic(TimeSpan period, Action tick) => new Inert();

        /// <summary>Runs posted work until <paramref name="done" /> holds and nothing is queued.</summary>
        public async Task DrainUntil(Func<bool> done, CancellationToken token)
        {
            var until = DateTime.UtcNow + TimeSpan.FromSeconds(60); // deadlock detector
            while (DateTime.UtcNow < until)
            {
                while (_queue.TryDequeue(out var action)) action();
                if (done() && _queue.IsEmpty) return;
                await Task.Delay(10, token);
            }

            Assert.Fail("the tone apply never finished");
        }

        private sealed class Inert : IDisposable
        {
            public void Dispose() { }
        }
    }

    [Theory]
    [InlineData(Key.D1, KeyModifiers.None, false, RigPerformanceKeyAction.Scene, 0)]
    [InlineData(Key.D8, KeyModifiers.None, false, RigPerformanceKeyAction.Scene, 7)]
    [InlineData(Key.NumPad3, KeyModifiers.None, false, RigPerformanceKeyAction.Scene, 2)]
    [InlineData(Key.D9, KeyModifiers.None, false, RigPerformanceKeyAction.None, -1)]
    [InlineData(Key.D2, KeyModifiers.Control, false, RigPerformanceKeyAction.None, -1)]
    [InlineData(Key.D2, KeyModifiers.None, true, RigPerformanceKeyAction.None, -1)]    // typing in a field
    [InlineData(Key.PageUp, KeyModifiers.None, false, RigPerformanceKeyAction.PreviousSong, -1)]
    [InlineData(Key.PageDown, KeyModifiers.None, false, RigPerformanceKeyAction.NextSong, -1)]
    public void ThePerformanceKeys_Map(Key key, KeyModifiers modifiers, bool typing, RigPerformanceKeyAction action, int scene) => Assert.Equal((action, scene), RigPerformanceKeys.Map(key, modifiers, typing));
}
