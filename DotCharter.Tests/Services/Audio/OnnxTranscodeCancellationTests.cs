using System.Text.RegularExpressions;

namespace DotCharter.Tests.Services.Audio;

/// <summary>
/// The ONNX backend's transcode PRE-PASS must honour the run's token.
///
/// <para><c>SeparateAsync</c> threaded <c>ct</c> through inference but not through
/// the pre-pass that turns a non-WAV song into a temp WAV, whose two halves are a
/// synchronous full-file JUCE decode and an ffmpeg run bounded only by the
/// five-minute <c>TranscodeTimeout</c>. Pressing Cancel on the status pill did
/// nothing observable until the phase ended — up to five minutes of a dead
/// affordance on a wedged ffmpeg.</para>
///
/// <para><b>Why this is a source lint.</b> The behaviour lives behind a real ONNX
/// session and a real ffmpeg, so the only default-gate reach into it is the
/// structure. The half that CAN be driven is driven, in
/// <c>Services/Export/MediaProcessTests.Run_CancelledMidFlight_…</c>, which starts
/// a real child and cancels it; this class pins that the backend actually hands
/// the token to it, and that neither decoder converts a cancel into "unavailable"
/// (which the composite reads as "this backend declined" and answers by starting
/// the NEXT one — on the work the user just stopped).</para>
/// </summary>
public sealed class OnnxTranscodeCancellationTests
{
    private static string Source()
    {
        var path = Path.Combine(TestPaths.RepoRoot(), "DotCharter.Services", "Audio", "StemSeparation",
            "Onnx", "OnnxStemSeparationService.cs");
        Assert.True(File.Exists(path), path);
        return File.ReadAllText(path);
    }

    /// <summary>The body of one method, from its signature to the next
    /// column-4 <c>private</c>/<c>public</c>/<c>internal</c> member.</summary>
    private static string Body(string source, string signatureFragment)
    {
        var start = source.IndexOf(signatureFragment, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{signatureFragment}' not found — the lint would pass vacuously.");
        var next = Regex.Match(source[(start + signatureFragment.Length)..],
            @"\n    (private|public|internal|protected)\s");
        return next.Success
            ? source.Substring(start, signatureFragment.Length + next.Index)
            : source[start..];
    }

    [Fact]
    public void The_transcode_pre_pass_takes_the_runs_token_and_the_run_hands_it_over()
    {
        var src = Source();

        Assert.Contains("private string? TranscodeToWav(string inputAudioPath, CancellationToken ct", src,
            StringComparison.Ordinal);
        // …and SeparateAsync actually passes it. A parameter nobody supplies is the
        // same dead affordance one level in.
        Assert.Contains("TranscodeToWav(inputAudioPath, ct)", src, StringComparison.Ordinal);
    }

    [Fact]
    public void The_ffmpeg_branch_gives_MediaProcess_the_token_not_only_the_timeout()
    {
        var body = Body(Source(), "private string? TranscodeToWav(");

        Assert.Contains("MediaProcess.Run(", body, StringComparison.Ordinal);
        Assert.Contains("TranscodeTimeout,", body, StringComparison.Ordinal);
        Assert.Contains("ct);", body, StringComparison.Ordinal);
    }

    [Fact]
    public void The_native_decode_has_a_cancellation_point_on_each_side_of_the_blocking_call()
    {
        var body = Body(Source(), "private bool TryNativeDecodeToWav(");

        Assert.Contains("CancellationToken ct", body, StringComparison.Ordinal);
        // The decode itself is a synchronous native call with no abort channel, so
        // "before" and "after" are the only cancellation points that exist: refuse
        // to start one already cancelled, drop the result of one cancelled while it ran.
        Assert.Equal(2, Regex.Matches(body, @"ct\.ThrowIfCancellationRequested\(\)").Count);
    }

    [Theory]
    [InlineData("private string? TranscodeToWav(")]
    [InlineData("private bool TryNativeDecodeToWav(")]
    public void Neither_decoder_reports_a_cancel_as_a_decode_failure(string signature)
    {
        var body = Body(Source(), signature);

        // A catch-all that swallowed the OperationCanceledException would turn a
        // cancel into null / false — reported to the user as "could not read this
        // file", and read by CompositeStemSeparationService as "this backend
        // declined", which starts the next backend on the cancelled work.
        var cancelArm = body.IndexOf("catch (OperationCanceledException)", StringComparison.Ordinal);
        Assert.True(cancelArm >= 0, $"{signature} has no OperationCanceledException arm.");

        var catchAll = body.IndexOf("catch (Exception", StringComparison.Ordinal);
        Assert.True(catchAll >= 0, $"{signature} has no general catch — has the shape changed?");
        Assert.True(cancelArm < catchAll,
            $"{signature}'s general catch precedes its cancel arm, so the cancel is swallowed.");
        Assert.Contains("throw;", body[cancelArm..catchAll], StringComparison.Ordinal);
    }
}
