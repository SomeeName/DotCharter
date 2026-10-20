namespace DotCharter.Tests.Services.Audio;

/// <summary>
/// A4. `docs/agents/services/audio-engine.md` states the rule outright — <i>"Never
/// enumerate `_stems` outside `_gate` — snapshot under the lock and iterate the
/// copy, <b>so the lock is never held across a P/Invoke</b>"</i> — and the
/// second half of it was broken for the slowest P/Invokes the service makes:
/// `JuceAudioEngine.OpenFile` / `OpenMemory`, 1–3 seconds each inside JUCE's
/// format readers, times 4–6 for a stem set.
/// <para>
/// Meanwhile six UI-thread members lock the same gate (`CurrentAudioPath`,
/// `HasStems`, `LoadedStemNames`, the volume setters, and `SnapshotStems()` —
/// which every one of Play/Pause/Stop/SeekTo calls first), so pressing Space
/// while a song loaded blocked the UI thread for the whole decode.
/// </para>
/// <para>
/// This is a source lint rather than a behavioural test on purpose: proving it
/// at runtime needs a real audio device and a real multi-second decode, whereas
/// the invariant is entirely structural — a media open must not appear inside a
/// `lock (_gate)` block. It is the same shape as the other reachability guards
/// in this suite. Engine create/destroy under the gate is deliberately NOT
/// asserted on: `ReplaceEngineGated` is the engine's exclusive owner by design.
/// </para>
/// </summary>
public sealed class PlaybackGateHoldGuardTests
{
    /// <summary>
    ///     EVERY partial of the service, derived rather than listed: the class is
    ///     split across a dozen files and a hand list silently stops looking at
    ///     whichever partial a media open moves into next.
    /// </summary>
    private static readonly string[] ServiceFiles =
        PlaybackServiceSourceFiles.All();

    /// <summary>The multi-second native calls that must never run under `_gate`.</summary>
    // The engine create (and its fallback walk) and an in-place reconfigure open
    // a DEVICE: a failing ASIO open is ~6.6 s per attempt, and holding _gate
    // across it froze the window for 13 s at startup and on every watchdog
    // recovery (#345). The engine constructor is spelt in two pieces: this class only READS
    // source, and NativeEngineCollectionLintTests would read the whole token as this class
    // opening an engine.
    private static readonly string[] ForbiddenUnderGate =
    [
        ".OpenFile(", ".OpenMemory(", ".LoadPlugin(",
        "CreateEngineWithFallback(", "new Juce" + "AudioEngine(", ".Reconfigure("
    ];

    /// <summary>
    ///     The native AUDIO-THREAD BARRIERS that must not run under `_gate`
    ///     either. `dca_plugin_detach_from_input` waits out an in-flight audio
    ///     block (and at ABI v28 walks every seat in turn), so it is tens of ms
    ///     rather than seconds — but `_gate` is what Play / Pause / Stop / SeekTo
    ///     all wait on through `SnapshotStems()`, and the budget for a UI gesture
    ///     is 150 ms.
    ///     <para>
    ///     `JuceAudioPlugin.Bypass`'s SETTER is one of these: it P/Invokes
    ///     `PluginDetachFromInput` / `PluginAttachToInputForPlayer`. That is not
    ///     visible at the call site, which is exactly how a bypass toggle came to
    ///     sit inside the lock in the function whose phases 1b and 1c were
    ///     extracted from it for this very reason.
    ///     </para>
    /// </summary>
    private static readonly string[] BarriersForbiddenUnderGate =
        [".Bypass =", ".AttachToInput(", ".DetachFromInput("];

    [Fact]
    public void NoMediaOpenRunsInsideThePlaybackGate()
    {
        foreach (var rel in ServiceFiles)
        {
            var src = StripLineComments(File.ReadAllText(rel));
            foreach (var block in GateBlocks(src))
            {
                foreach (var forbidden in ForbiddenUnderGate)
                {
                    Assert.False(block.Contains(forbidden, StringComparison.Ordinal),
                        $"{Path.GetFileName(rel)}: '{forbidden}' is called inside a lock (_gate) block. That holds the playback " +
                        "gate across a multi-second P/Invoke while the UI thread waits on it in " +
                        "SnapshotStems()/HasStems/CurrentAudioPath. Open unlocked, publish under the lock.");
                }
            }
        }
    }

    [Fact]
    public void NoNativeInputAttachRunsInsideThePlaybackGate()
    {
        foreach (var rel in ServiceFiles)
        {
            var src = StripLineComments(File.ReadAllText(rel));
            foreach (var block in GateBlocks(src))
            {
                foreach (var forbidden in BarriersForbiddenUnderGate)
                {
                    Assert.False(block.Contains(forbidden, StringComparison.Ordinal),
                        $"{Path.GetFileName(rel)}: '{forbidden}' runs inside a lock (_gate) block. Attaching or detaching a " +
                        "plugin from an input chain is a wait-one-block barrier on the audio callback, and " +
                        "six UI-thread members wait on _gate — Play/Pause/Stop/SeekTo all reach it through " +
                        "SnapshotStems(). Collect the transition under the lock and apply it in the unlocked " +
                        "phase 1c. NOTE: JuceAudioPlugin.Bypass's SETTER is one of these calls.");
                }
            }
        }
    }

    /// <summary>
    /// Keeps the guard above honest: if the opens were renamed away the scan
    /// would pass vacuously.
    /// </summary>
    [Fact]
    public void TheServiceStillOpensMediaSomewhere()
    {
        Assert.NotEmpty(ServiceFiles);
        var all = string.Concat(ServiceFiles.Select(File.ReadAllText));
        Assert.Contains(".OpenFile(", all, StringComparison.Ordinal);
        Assert.Contains(".OpenMemory(", all, StringComparison.Ordinal);
        Assert.Contains("CreateEngineWithFallback(", all, StringComparison.Ordinal);
        Assert.Contains(".Reconfigure(", all, StringComparison.Ordinal);
        Assert.Contains("lock (_gate)", all, StringComparison.Ordinal);
        foreach (var barrier in BarriersForbiddenUnderGate)
            Assert.Contains(barrier, all, StringComparison.Ordinal);
    }

    /// <summary>
    /// Strips `//` line comments so a comment that merely MENTIONS OpenFile
    /// cannot fail (or pass) the scan. Crude on purpose — a `//` inside a string
    /// literal would over-strip, which can only make the guard stricter.
    /// </summary>
    private static string StripLineComments(string src)
    {
        var sb = new System.Text.StringBuilder(src.Length);
        foreach (var line in src.Split('\n'))
        {
            var idx = line.IndexOf("//", StringComparison.Ordinal);
            sb.Append(idx >= 0 ? line[..idx] : line).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Yields the body of every `lock (_gate)` statement. Handles both the
    /// braced form and the single-statement form (`lock (_gate) return …;`),
    /// which the property getters use.
    /// </summary>
    private static IEnumerable<string> GateBlocks(string src)
    {
        const string marker = "lock (_gate)";
        var i = 0;
        while ((i = src.IndexOf(marker, i, StringComparison.Ordinal)) >= 0)
        {
            var j = i + marker.Length;
            while (j < src.Length && char.IsWhiteSpace(src[j])) j++;
            if (j >= src.Length) yield break;

            if (src[j] != '{')
            {
                // Single statement: everything up to the terminating ';'.
                var semi = src.IndexOf(';', j);
                if (semi < 0) yield break;
                yield return src[j..(semi + 1)];
                i = semi + 1;
                continue;
            }

            var depth = 0;
            var k = j;
            for (; k < src.Length; k++)
            {
                if (src[k] == '{') depth++;
                else if (src[k] == '}' && --depth == 0) break;
            }
            if (k >= src.Length) yield break;
            yield return src[j..(k + 1)];
            i = k + 1;
        }
    }
}
