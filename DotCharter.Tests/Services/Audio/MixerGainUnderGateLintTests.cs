using System.Text.RegularExpressions;

namespace DotCharter.Tests.Services.Audio;

/// <summary>
///     v0.17.1 fix N3: the transport snapshots
///     <c>_stems</c> under <c>_gate</c>, and the <c>Volume</c> setter — which
///     had been calling <c>ApplyGain()</c> WITHOUT the lock its own contract
///     demands — now takes it. That fix had no test because <c>ApplyGain</c>
///     drives a live JUCE engine; the CONTRACT does not need one. This reads
///     the mixer partial and refuses any <c>ApplyGain()</c> call that is not
///     on a line holding <c>lock (_gate)</c>. (The other partials' calls sit
///     inside multi-line gate blocks that <c>PlaybackGateHoldGuardTests</c>
///     already walks.)
/// </summary>
public sealed class MixerGainUnderGateLintTests
{
    [Fact]
    public void Every_ApplyGain_call_in_the_mixer_partial_holds_the_gate()
    {
        var path = Path.Combine(TestPaths.RepoRoot(), "DotCharter.Tone", "Audio", "JuceAudioPlaybackService.Mixer.cs");
        var lines = File.ReadAllLines(path);

        var calls = lines.Select((text, i) => (text, line: i + 1))
            .Where(l => l.text.Contains("ApplyGain();", StringComparison.Ordinal))
            .ToList();
        // The private method's own body mentions the name; only CALL sites count.
        Assert.True(calls.Count >= 2, "the mixer partial no longer calls ApplyGain — the lint is looking at the wrong file");

        var unguarded = calls.Where(c => !c.text.Contains("lock (_gate)", StringComparison.Ordinal)).ToList();
        Assert.True(unguarded.Count == 0,
            "ApplyGain() iterates _stems and its contract says the caller holds _gate. Unguarded call(s): " +
            string.Join(", ", unguarded.Select(u => $"line {u.line}: {u.text.Trim()}")));
    }

    [Fact]
    public void ApplyGain_still_documents_the_contract_it_is_linted_against()
    {
        var path = Path.Combine(TestPaths.RepoRoot(), "DotCharter.Tone", "Audio", "JuceAudioPlaybackService.Mixer.cs");
        var source = File.ReadAllText(path);
        var body = source[source.IndexOf("private void ApplyGain()", StringComparison.Ordinal)..];
        Assert.Matches(new Regex(@"Caller must hold _gate", RegexOptions.IgnoreCase), body[..Math.Min(400, body.Length)]);
    }
}
