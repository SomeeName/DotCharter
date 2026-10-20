using System.Text.RegularExpressions;

namespace DotCharter.Tests.Services.Audio;

/// <summary>
///     Concurrency audit #15: <c>JuceAudioPlaybackService._disposing</c> was a
///     plain <c>bool</c> read from the position timer, the watchdog, the health
///     monitor and the sandbox threads under a comment promising "one volatile
///     read", and <c>Dispose</c> was a check-then-set, so two concurrent Disposes
///     both ran the whole teardown. Reads the WHOLE partial set, so a split
///     cannot make it vacuous.
/// </summary>
public sealed class PlaybackServiceDisposeGuardTests
{
    [Fact]
    public void TheDisposingFlag_IsAnInterlockedExchange_AndEveryReaderGoesThroughTheVolatileProperty()
    {
        var files = PlaybackServiceSourceFiles.All();
        Assert.True(files.Length >= 5, "the partial set moved; repoint this lint");
        var set = string.Join("\n", files.Select(f => SourceText.StripComments(File.ReadAllText(f))));

        Assert.DoesNotContain("bool _disposing", set);
        Assert.Contains("private bool IsDisposing => Volatile.Read(ref _disposingFlag) != 0;", set);
        Assert.Contains("if (Interlocked.Exchange(ref _disposingFlag, 1) != 0) return;", set);

        // The flag itself is touched only by the property and the exchange.
        var raw = Regex.Matches(set, @"_disposingFlag").Count;
        Assert.Equal(3, raw); // the field, the property's Volatile.Read, Dispose's Interlocked.Exchange

        // …and the readers really read it: the guards this repo already pins
        // (the position tick, the watchdog, the sandbox) all go through it.
        Assert.True(Regex.Matches(set, @"\bIsDisposing\b").Count >= 8, "the readers are gone");
    }
}
