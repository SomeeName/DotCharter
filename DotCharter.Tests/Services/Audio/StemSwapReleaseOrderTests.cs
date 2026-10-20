
namespace DotCharter.Tests.Services.Audio;

/// <summary>
///     <para>
///         Since JuceSharp <c>3db99e7</c> an audio source that fits the engine's preload
///         budget decodes into RAM whole, so a stem costs its full float32 length (a
///         5-minute stereo stem is ~106 MB). <c>LoadStemsCore</c> used to open the
///         INCOMING stem set and only then dispose the outgoing one, which made every
///         project switch peak at double the resting audio memory — measured headless at
///         965.7 MB against 492.7 MB for 7 sources of 60.6 MB — and, because
///         <c>AudioEngine::reservePreloadBytes</c> is an engine-WIDE budget, could push
///         the incoming stems past it. A source that loses that race falls back to
///         <c>BufferingAudioSource</c> and stays there for its whole life, reinstating
///         the audio-thread decode-lock stall the preload path exists to remove.
///     </para>
///     <para>
///         There is no seam to assert this behaviourally: <c>LoadStemsCore</c> opens
///         through a concrete <c>JuceAudioEngine</c>, and reaching it means
///         <c>EnsureEngine</c> opening a real audio device — which the gate must never
///         do (a wedged testhost holds the sound card exclusively). So this is a SOURCE
///         lint over the phase order, in the style of the reachability lints. It finds
///         its subject by CONTENT rather than by path, because a partial split silently
///         makes a path-based lint scan a file that no longer contains its subject.
///     </para>
/// </summary>
public sealed class StemSwapReleaseOrderTests
{
    /// <summary>
    ///     The outgoing stem set must be disposed BEFORE the loop that opens the incoming
    ///     one — the same order <c>SwapMasterSource</c> already uses for the master source.
    /// </summary>
    [Fact]
    public void LoadStemsCore_ReleasesTheOutgoingSet_BeforeItOpensTheIncomingOne()
    {
        var (path, body) = LoadStemsCoreBody();

        var release = body.IndexOf("DisposeStemEntries(outgoing)", StringComparison.Ordinal);
        var open = body.IndexOf("engine.OpenFile(", StringComparison.Ordinal);

        Assert.True(release >= 0,
            $"{path}: LoadStemsCore no longer disposes the outgoing stem set as 'DisposeStemEntries(outgoing)'. "
            + "If it was renamed, repoint this lint; if the release was removed, a project switch is back to "
            + "holding two full stem sets of decoded audio at once.");
        Assert.True(open >= 0,
            $"{path}: LoadStemsCore no longer opens stems with 'engine.OpenFile(' — repoint this lint.");
        Assert.True(release < open,
            $"{path}: LoadStemsCore opens the incoming stem set before releasing the outgoing one. "
            + "That doubles the peak audio memory of a project switch and can push the incoming stems past "
            + "the engine-wide preload budget, permanently demoting them to the streaming path.");
    }

    /// <summary>
    ///     The detach and the dispose must be separated by the lock: detaching under
    ///     <c>_gate</c> is what makes the unlocked dispose safe, and disposing UNDER the
    ///     gate is what the phase split exists to avoid.
    /// </summary>
    [Fact]
    public void LoadStemsCore_DetachesTheOutgoingSetUnderTheGate_ThenDisposesItUnlocked()
    {
        var (path, body) = LoadStemsCoreBody();

        var detach = body.IndexOf("outgoing = _stems.Values.ToArray()", StringComparison.Ordinal);
        var release = body.IndexOf("DisposeStemEntries(outgoing)", StringComparison.Ordinal);

        Assert.True(detach >= 0,
            $"{path}: LoadStemsCore no longer detaches the outgoing stem set into 'outgoing' before disposing it. "
            + "Disposing straight out of _stems would either hold _gate across the native teardown or leave a "
            + "window where another caller reads a disposed source.");
        Assert.True(detach < release, $"{path}: the outgoing stem set is disposed before it is detached.");

        // The dispose must sit outside the lock block that performed the detach.
        var betweenBraces = body.Substring(detach, release - detach);
        Assert.Contains("}", betweenBraces);
    }

    internal static (string Path, string Body) LoadStemsCoreBody()
    {
        var file = Directory
            .EnumerateFiles(Path.Combine(TestPaths.RepoRoot(), "DotCharter.Tone", "Audio"),
                "JuceAudioPlaybackService*.cs")
            .FirstOrDefault(f => File.ReadAllText(f).Contains("private void LoadStemsCore(", StringComparison.Ordinal));

        Assert.False(file is null,
            "No JuceAudioPlaybackService partial declares LoadStemsCore. It was renamed or moved out of "
            + "DotCharter.Services/Audio — repoint this lint at wherever the stem-set swap now lives.");

        var text = SourceText.StripComments(File.ReadAllText(file!));
        var start = text.IndexOf("private void LoadStemsCore(", StringComparison.Ordinal);
        Assert.True(start >= 0, $"{file}: LoadStemsCore survived the file scan but not the comment strip.");

        // The lint must not be satisfied by prose. Everything it matches has to be real
        // code, so the body is taken from the comment-stripped text and stops at the next
        // member declaration.
        var next = text.IndexOf("\n    public ", start, StringComparison.Ordinal);
        var end = next > start ? next : text.Length;
        return (Path.GetFileName(file!), text[start..end]);
    }
}
