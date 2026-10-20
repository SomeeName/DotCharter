using System.Globalization;
using System.Text.RegularExpressions;

namespace DotCharter.Tests.Services.Audio;

/// <summary>
///     The per-seat drop tune crosses the ABI (v31), so both sides are read here
///     rather than trusted — the same gate <c>EqNativeMirrorTests</c> is, for the
///     same reason: the C++ side is a submodule of this repository and can simply
///     be opened.
///
///     <para>
///         <b><c>DCA_ABI_VERSION</c> and <c>NativeMethods.ExpectedAbiVersion</c> are
///         compared for EQUALITY when an engine opens</b>, so they move together or
///         every engine on the machine refuses to start. That failure is total and
///         it looks like "the app has no audio", not like a forgotten constant —
///         which is exactly why it belongs in the default gate rather than in the
///         JuceSharp suite that needs the built DLL to run.
///     </para>
/// </summary>
public sealed class DropTuneNativeMirrorTests
{
    [Fact]
    public void TheAbiVersion_AgreesBetweenTheHeaderAndTheWrapper()
    {
        var header = NativeSource(Path.Combine("include", "dotcharter_audio.h"));
        var wrapper = WrapperSource(Path.Combine("Interop", "NativeMethods.cs"));

        var native = Regex.Match(header, @"#define\s+DCA_ABI_VERSION\s+(\d+)");
        Assert.True(native.Success, "DCA_ABI_VERSION is gone from the ABI header.");

        var managed = Regex.Match(wrapper, @"ExpectedAbiVersion\s*=\s*(\d+)\s*;");
        Assert.True(managed.Success, "NativeMethods.ExpectedAbiVersion is gone from the wrapper.");

        Assert.Equal(int.Parse(native.Groups[1].Value, CultureInfo.InvariantCulture),
                     int.Parse(managed.Groups[1].Value, CultureInfo.InvariantCulture));
    }

    /// <summary>
    ///     The per-seat drop tune needed the bump, so the version must be at
    ///     least the one that introduced it. A LOWER number with these exports
    ///     present means somebody rolled the version back and left the symbols.
    /// </summary>
    [Fact]
    public void TheAbiVersion_IsAtLeastTheOneThatAddedThePerSeatDropTune()
    {
        var header = NativeSource(Path.Combine("include", "dotcharter_audio.h"));
        var native = int.Parse(Regex.Match(header, @"#define\s+DCA_ABI_VERSION\s+(\d+)").Groups[1].Value,
            CultureInfo.InvariantCulture);

        Assert.True(native >= 31,
            $"DCA_ABI_VERSION={native}, expected >= 31 once dca_engine_set_player_input_pitch_shift exists.");
    }

    /// <summary>
    ///     Both new exports are DECLARED in the ABI header and BOUND in the
    ///     wrapper, with the entry-point names matching.
    ///     <para>
    ///         The submodule guide's hardest-won rule is behind this:
    ///         <c>src/JuceSharp.Native/src/abi/</c> is NOT in the build, so an
    ///         export added there compiles perfectly cleanly and does not exist in
    ///         the DLL. New engine exports go in <c>src/dotcharter_audio.cpp</c>,
    ///         which is what this asserts.
    ///     </para>
    /// </summary>
    [Theory]
    [InlineData("dca_engine_set_player_input_pitch_shift")]
    [InlineData("dca_engine_set_player_input_pitch_shift_mode")]
    public void EachPerSeatDropTuneExport_IsDeclared_Implemented_AndBound(string export)
    {
        var header = NativeSource(Path.Combine("include", "dotcharter_audio.h"));
        var impl = NativeSource(Path.Combine("src", "dotcharter_audio.cpp"));
        var wrapper = WrapperSource(Path.Combine("Interop", "NativeMethods.cs"));

        // The header's declaration. `_mode` contains the shorter name, so the
        // match is anchored on what follows it.
        Assert.Matches($@"DCA_CALL\s+{Regex.Escape(export)}\s*\(", header);
        // …and the definition, in the file that is actually COMPILED.
        Assert.Matches($@"DCA_CALL\s+{Regex.Escape(export)}\s*\(", impl);
        // …and the P/Invoke binding.
        Assert.Contains($"EntryPoint = \"{export}\"", wrapper, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The seat index is REJECTED, never clamped — the v28 node-id rule, and
    ///     for the same reason: a clamp would silently detune somebody else's
    ///     guitar while the UI said it had moved the seat you asked for.
    /// </summary>
    [Fact]
    public void AnOutOfRangeSeat_IsRejectedByTheExport_NotClamped()
    {
        var impl = NativeSource(Path.Combine("src", "dotcharter_audio.cpp"));
        var body = impl[impl.IndexOf("dca_engine_set_player_input_pitch_shift(", StringComparison.Ordinal)..];
        var scope = body[..Math.Min(body.Length, 1200)];

        Assert.Contains("validPlayer((int) player)", scope, StringComparison.Ordinal);
        Assert.Contains("DCA_ERR_INVALID_ARG", scope, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The shifter runs INSIDE the per-player loop. Above it — where it sat
    ///     until v31 — one pedal detuned every seat, and that is not visible in
    ///     any managed assertion: the whole failure is which side of the split
    ///     three lines of C++ live on.
    /// </summary>
    [Fact]
    public void TheShifter_RunsInsideThePerPlayerLoop_NotAheadOfTheSplit()
    {
        var engine = NativeSource(Path.Combine("src", "AudioEngine.h"));

        var loop = engine.IndexOf("for (int p = 0; p < players; ++p)", StringComparison.Ordinal);
        Assert.True(loop > 0, "the per-player rig loop is gone from the audio callback.");

        var shift = engine.IndexOf("shifter.process(ptrs.data()", StringComparison.Ordinal);
        Assert.True(shift > loop,
            "the drop-tune shifter runs BEFORE the per-player split again — one pedal is detuning every seat.");
    }

    /// <summary>
    ///     The load-bearing channel rule survived the per-seat rewrite: a seat
    ///     shifts exactly the signal ITS capture ring consumes. Taking ch0/ch1
    ///     blindly on a bigger interface shifted the wrong inputs AND handed the
    ///     ring a 2-channel buffer whose channel index clamped down, so note
    ///     detection silently listened to a different physical input than the one
    ///     being shifted.
    /// </summary>
    [Fact]
    public void EachSeatShiftsItsOwnCaptureChannel_NotChannelsZeroAndOne()
    {
        var engine = NativeSource(Path.Combine("src", "AudioEngine.h"));
        var loop = engine[engine.IndexOf("for (int p = 0; p < players; ++p)", StringComparison.Ordinal)..];
        var scope = loop[..Math.Min(loop.Length, 8000)];

        // The seat's OWN channel, and the seat's OWN shifter/scratch.
        Assert.Contains("owner_.inputCaptureChannel_[(size_t) p]", scope, StringComparison.Ordinal);
        Assert.Contains("owner_.dropTune_[(size_t) p]", scope, StringComparison.Ordinal);
        Assert.Contains("owner_.dropScratch_[(size_t) p]", scope, StringComparison.Ordinal);
        // The stereo branch is reserved for the ONE-player case, which is what
        // keeps a solo player's signal path byte-for-byte what it was.
        Assert.Contains("players == 1 && numInputChannels <= 2", scope, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Every seat's shifter is prepared at device start, not lazily. Raising
    ///     the player count is a MESSAGE-thread call (a co-op run starting), so a
    ///     shifter first prepared on the audio thread would be an allocation on
    ///     the audio thread — the one thing `juce_sharp_rtcheck` exists to catch.
    /// </summary>
    [Fact]
    public void EverySeatsShifter_IsPreparedAtDeviceStart()
    {
        var engine = NativeSource(Path.Combine("src", "AudioEngine.h"));

        Assert.Contains("owner_.dropTune_[(size_t) p].prepare(", engine, StringComparison.Ordinal);
        Assert.Matches(@"for \(int p = 0; p < kMaxPlayers; \+\+p\)\s*\{\s*\r?\n\s*owner_\.dropTune_", engine);
    }

    // ----- helpers -----------------------------------------------------------

    private static string NativeSource(string relative) =>
        ReadUnder(Path.Combine("JuceSharp", "src", "JuceSharp.Native"), relative);

    private static string WrapperSource(string relative) =>
        ReadUnder(Path.Combine("JuceSharp", "src", "JuceSharp"), relative);

    private static string ReadUnder(string project, string relative)
    {
        var path = Path.Combine(TestPaths.RepoRoot(), project, relative);
        Assert.True(File.Exists(path),
            $"'{path}' is missing. The JuceSharp submodule has to be checked out for this repository to build " +
            "at all, so this is a broken checkout rather than an optional fixture.");
        return File.ReadAllText(path);
    }
}
