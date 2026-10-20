// PedalboardViewModel: SCENES (A–H) — switch, rename, store, clear — over the chain's managed scene set.

using DotCharter.Tone.Audio.Dsp.Presets;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.Audio.Dsp.Scenes;

namespace DotCharter.Tone.UI.ViewModels.PlayMode;

public sealed partial class PedalboardViewModel
{
    private RigSceneSlotViewModel[]? _sceneSlots;

    /// <summary>
    ///     The eight scene buttons (A–H) of the rig's scene bar. Always eight, so
    ///     the bar never re-lays; the lamps and names are refreshed in place from
    ///     the chain's scene set (<see cref="RefreshScenes" />).
    /// </summary>
    public IReadOnlyList<RigSceneSlotViewModel> SceneSlots =>
        _sceneSlots ??= Enumerable.Range(0, RigSceneSet.Count)
            .Select(i => new RigSceneSlotViewModel(this, i))
            .ToArray();

    /// <summary>The scene that is playing (0..7). A rig without scenes plays A.</summary>
    public int ActiveSceneIndex { get; private set; }

    /// <summary>
    ///     Switches to scene <paramref name="index" /> (0..7 = A..H) — the bar's
    ///     buttons, keys 1–8 and the scene MIDI CC all land here.
    ///     <para>
    ///         <b>It never adds or removes a unit.</b> The target rig is the LIVE
    ///         rig with the scene's params and bypass written over it
    ///         (<see cref="RigScenes.Switch" />), so the applier's diff reuses
    ///         every node and writes values only: delay and reverb tails ring on
    ///         through the switch, and it is as fast as turning the knobs. The
    ///         scene being left needs no store — the live values ARE the active
    ///         scene (<see cref="DcRigSnapshot.FromChain" /> folds them in), which
    ///         is Helix's "snapshot edits: recall": an edit is never lost by
    ///         switching away.
    ///     </para>
    ///     <para>
    ///         An unused scene is FORKED from what is playing (the rule the A/B
    ///         compare uses), so the press always lands somewhere and nothing
    ///         audible changes. One switch is one undo step. A switch turns the
    ///         master bypass off first: a scene is a sound to hear.
    ///     </para>
    /// </summary>
    public void SelectSceneAt(int index)
    {
        if (index is < 0 or >= RigSceneSet.Count) return;

        // A TONE IS ARRIVING (the apply queue is diffing it onto the chain on a
        // worker): the chain is half one rig and half the other, and a switch
        // built from that snapshot would put the half-old rig back once the apply
        // lets go of the chain. A pedal board sends exactly this — a program
        // change and the scene CC together — so the switch waits for the tone
        // and lands on IT (OnPresetApplied → ApplyDeferredScene).
        if (Browser.IsApplying)
        {
            _sceneAfterApply = index;
            return;
        }

        SwitchScene(index);
    }

    private int? _sceneAfterApply;

    /// <summary>The scene a switch asked for while a tone was arriving, now that it has.</summary>
    private void ApplyDeferredScene()
    {
        if (_sceneAfterApply is not { } scene) return;
        _sceneAfterApply = null;
        SwitchScene(scene);
    }

    private void SwitchScene(int index)
    {
        if (IsMasterBypassed) ToggleMasterBypass();

        var live = BuildCurrentRig();
        var next = RigScenes.Switch(live, index);
        if (next is null)
        {
            RefreshScenes();
            return;
        }

        _applyingHistory = true;
        try
        {
            if (ReferenceEquals(next.Modules, live.Modules))
            {
                // a fork: the sound does not change, only the scene set does
                _dsp.Scenes = next.Scenes;
            }
            else
            {
                // verbatim (USER origin, never re-levelled), onto the seat on screen
                _presets.ApplyToPlayer(SelectedPlayer, new DspTonePreset(
                    "scene", "Scene", DspToneCategory.Clean, string.Empty, next, DspToneOrigin.User));
                Sync();
            }
        }
        finally
        {
            _applyingHistory = false;
        }

        RefreshScenes();
        CommitHistoryStep();
    }

    /// <summary>Names scene <paramref name="index" /> (blank = back to its letter).</summary>
    public void RenameScene(int index, string? name)
    {
        if (index is < 0 or >= RigSceneSet.Count) return;
        var live = BuildCurrentRig();
        var set = live.Scenes ?? RigScenes.Start(live.Modules);
        CommitScenes(RigScenes.Rename(set, index, name));
    }

    /// <summary>Stores what is playing into scene <paramref name="index" />
    /// ("store current sound here"). The playing scene already IS the live rig.</summary>
    public void StoreScene(int index)
    {
        if (index is < 0 or >= RigSceneSet.Count) return;
        var live = BuildCurrentRig();
        var set = live.Scenes ?? RigScenes.Start(live.Modules);
        if (set.Active == index) return;
        CommitScenes(RigScenes.Store(set, index, live.Modules));
    }

    /// <summary>Empties scene <paramref name="index" /> (never the one playing).</summary>
    public void ClearScene(int index)
    {
        var live = BuildCurrentRig();
        if (live.Scenes is not { } set || set.Active == index) return;
        CommitScenes(RigScenes.Clear(set, index));
    }

    /// <summary>Puts an edited scene set on the chain (a set that says nothing
    /// beyond the live rig is no scenes at all) as one undo step.</summary>
    private void CommitScenes(RigSceneSet set)
    {
        _dsp.Scenes = RigScenes.IsTrivial(set) ? null : set;
        RefreshScenes();
        CommitHistoryStep();
    }

    /// <summary>Re-reads the scene bar from the chain. Called after every board
    /// reconcile (a preset apply, an undo, an engine rebuild, a seat or profile
    /// switch) because each of them can bring a different scene set.</summary>
    private void RefreshScenes()
    {
        var set = _dsp.Scenes is { } scenes ? RigScenes.Normalize(scenes) : null;
        var active = set?.Active ?? 0;
        for (var i = 0; i < SceneSlots.Count; i++)
        {
            var slot = set?.Slots[i];
            SceneSlots[i].Update(slot?.Name, i == active || slot?.IsStored == true, i == active);
        }

        if (ActiveSceneIndex != active)
        {
            ActiveSceneIndex = active;
            OnPropertyChanged(nameof(ActiveSceneIndex));
        }
    }
}
