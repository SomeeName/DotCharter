using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using DotCharter.Tone.Audio;
using DotCharter.Tone.Audio.Engine;

namespace DotCharter.Tests.Services.Audio;

/// <summary>
/// A6. `ReplaceEngineGated` rehydrates the song, the instrument VSTi, the guitar
/// chain and the mirror device across an engine rebuild. The stem set was the one
/// thing it dropped: `FreeAudioLocked` → `ClearStemsLocked` wiped the dictionary
/// and nothing snapshotted it first, so after ANY device change, buffer-size
/// change or watchdog recovery a user mixing stems silently lost them — and the
/// device watchdog's own class comment claimed the opposite ("the loaded song,
/// stems, VSTs and mixer state are restored").
/// </summary>
public sealed class EngineReplaceStemRestoreTests
{
    /// <summary>
    /// The snapshot has to carry the whole USER mix, not just which files were
    /// open — a restore that reloads the stems at default gain/unmuted has still
    /// thrown away the balance the user set.
    /// </summary>
    [Fact]
    public void TheSnapshotCarriesEveryPerStemMixSetting()
    {
        using var svc = new JuceAudioPlaybackService(FakeLocalization.English());
        SetStem(svc, "drums", @"C:\stems\drums.wav", volume: 0.25, muted: true, soloed: false);
        SetStem(svc, "vocals", @"C:\stems\vocals.wav", volume: 1.75, muted: false, soloed: true);
        SetField(svc, "_masterMixMuted", false);

        var snap = svc.SnapshotStemMixerLocked();

        Assert.NotNull(snap);
        Assert.False(snap!.MasterMixMuted);
        Assert.Equal(2, snap.Stems.Count);

        var drums = snap.Stems.Single(s => s.Name == "drums");
        Assert.Equal(@"C:\stems\drums.wav", drums.Path);
        Assert.Equal(0.25, drums.Volume);
        Assert.True(drums.Muted);
        Assert.False(drums.Soloed);
        Assert.Equal(125, drums.OffsetMs);

        var vocals = snap.Stems.Single(s => s.Name == "vocals");
        Assert.Equal(1.75, vocals.Volume);
        Assert.False(vocals.Muted);
        Assert.True(vocals.Soloed);
    }

    /// <summary>No stems loaded is a null snapshot — nothing to carry, nothing to redo.</summary>
    [Fact]
    public void NoStemsGivesNoSnapshot()
    {
        using var svc = new JuceAudioPlaybackService(FakeLocalization.English());
        Assert.Null(svc.SnapshotStemMixerLocked());
    }

    /// <summary>
    /// The restore must stand down when the rebuild FAILED and left the engine
    /// null. `LoadStemsCore` would otherwise call `EnsureEngine` → a nested
    /// `ReplaceEngineCore`, blocking on the non-reentrant `_vstGate` the caller
    /// is still holding — a 60 s stall instead of a lost stem set.
    /// </summary>
    [Fact]
    public void RestoreIsANoOpWhenTheRebuildLeftNoEngine()
    {
        using var svc = new JuceAudioPlaybackService(FakeLocalization.English());
        var snap = new JuceAudioPlaybackService.StemMixerSnapshot(
            [("drums", @"C:\stems\drums.wav", 1.0, false, false, 125)], MasterMixMuted: true);

        // Returns promptly instead of recursing into an engine rebuild.
        svc.RestoreStemsAfterEngineRebuild(snap);

        Assert.False(svc.HasStems);
    }

    [Fact]
    public void AQueuedMasterReload_PreservesTheCapturedRealMixerSettings()
    {
        using var svc = new JuceAudioPlaybackService(FakeLocalization.English());
        SetStem(svc, "drums", "drums.wav", 0.25, true, false);
        SetStem(svc, "vocals", "vocals.wav", 1.75, false, true);
        SetField(svc, "_masterMixMuted", false);
        var snapshot = svc.SnapshotStemMixerLocked();
        svc.FreeAudio();
        Assert.False(svc.HasStems);
        JuceAudioPlaybackService.StemMixerSnapshot? restored = null;
        var engine = new object();
        MediaRebuildReplay.Replay<MemoryStream, JuceAudioPlaybackService.StemMixerSnapshot>(new object(), new object(),
            new MediaLoadRequest("song.wav", 1), () => new MediaLoadRequest("song.wav", 2), engine, () => engine,
            () => new MemoryStream(), _ => Assert.Fail("The obsolete master must not be reopened."), () => true,
            snapshot, state => restored = state);
        Assert.NotNull(restored);
        Assert.Equal(snapshot, restored);
        Assert.False(restored.MasterMixMuted);
        Assert.Equal(0.25, restored.Stems.Single(s => s.Name == "drums").Volume);
        Assert.True(restored.Stems.Single(s => s.Name == "drums").Muted);
        Assert.True(restored.Stems.Single(s => s.Name == "vocals").Soloed);
    }

    // ----- reflection helpers ------------------------------------------------

    private static void SetStem(JuceAudioPlaybackService svc, string name, string path,
        double volume, bool muted, bool soloed)
    {
        var entryType = typeof(JuceAudioPlaybackService)
            .GetNestedType("StemEntry", BindingFlags.NonPublic)!;
        // GetUninitializedObject rather than Activator: StemEntry has `required`
        // members, and there is no public way to build one from outside.
        var entry = RuntimeHelpers.GetUninitializedObject(entryType);
        entryType.GetProperty("Name")!.SetValue(entry, name);
        entryType.GetProperty("Path")!.SetValue(entry, path);
        entryType.GetProperty("OffsetMs")!.SetValue(entry, 125.0);
        entryType.GetField("UserVolume")!.SetValue(entry, volume);
        entryType.GetField("UserMuted")!.SetValue(entry, muted);
        entryType.GetField("UserSoloed")!.SetValue(entry, soloed);

        var stems = (IDictionary)typeof(JuceAudioPlaybackService)
            .GetField("_stems", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(svc)!;
        stems[name] = entry;
    }

    private static void SetField(JuceAudioPlaybackService svc, string field, object value) =>
        typeof(JuceAudioPlaybackService)
            .GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(svc, value);

}
