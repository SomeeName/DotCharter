using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using DotCharter.Tone.Audio;

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
            [("drums", @"C:\stems\drums.wav", 1.0, false, false)], MasterMixMuted: true);

        // Returns promptly instead of recursing into an engine rebuild.
        svc.RestoreStemsAfterEngineRebuild(snap);

        Assert.False(svc.HasStems);
    }

    /// <summary>
    /// The wiring itself: `ReplaceEngineGated` must take the snapshot BEFORE the
    /// teardown that clears the dictionary, and re-issue it afterwards. Asserted
    /// on the source because driving a real engine replace needs a real device.
    /// </summary>
    [Fact]
    public void ReplaceEngineGated_SnapshotsBeforeTeardownAndRestoresAfter()
    {
        var body = MethodBody("private void ReplaceEngineGated(");

        var snapshot = body.IndexOf("SnapshotStemMixerLocked()", StringComparison.Ordinal);
        var free = body.IndexOf("FreeAudioLocked()", StringComparison.Ordinal);
        var restore = body.IndexOf("RestoreStemsAfterEngineRebuild(", StringComparison.Ordinal);

        Assert.True(snapshot >= 0,
            "ReplaceEngineGated must snapshot the stem mixer — FreeAudioLocked clears it.");
        Assert.True(free >= 0, "ReplaceEngineGated must still tear the audio down.");
        Assert.True(restore >= 0,
            "ReplaceEngineGated must re-issue the stem set after the rebuild.");
        Assert.True(snapshot < free, "The snapshot must be taken BEFORE the teardown clears the stems.");
        Assert.True(restore > free, "The restore must run AFTER the engine has been rebuilt.");
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

    private static string MethodBody(string signature)
    {
        // EVERY partial of the service, concatenated: the class is split across a
        // dozen files, so pinning one filename makes this lint go vacuous the
        // moment a method moves to a neighbouring partial.
        var src = ServiceSource();
        var start = src.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find '{signature}'.");
        var open = src.IndexOf('{', start + signature.Length);
        var depth = 0;
        var i = open;
        for (; i < src.Length; i++)
        {
            if (src[i] == '{') depth++;
            else if (src[i] == '}' && --depth == 0) break;
        }
        return src[open..Math.Min(i + 1, src.Length)];
    }

    /// <summary>Every `JuceAudioPlaybackService*.cs` partial and its extracted
    /// collaborators (<see cref="PlaybackServiceSourceFiles" />), concatenated in a
    /// stable order — derived from the directory so a new partial is covered the
    /// day it appears.</summary>
    private static string ServiceSource() =>
        string.Concat(PlaybackServiceSourceFiles.All().Select(File.ReadAllText));
}
