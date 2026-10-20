using System.Diagnostics;
using System.Text;

namespace DotCharter.Services.Export.Audio;

/// <summary>
///     Runs a redirected media tool (ffmpeg) to completion without deadlocking.
/// </summary>
/// <remarks>
///     A Windows anonymous pipe holds ~4 KB. A child that redirects stdout/stderr
///     and is never READ blocks forever writing into a full pipe, and the parent
///     blocks forever in <c>WaitForExit</c> — which, on the save path, wedges the
///     save worker for the rest of the session. So both pipes must be drained
///     concurrently with the wait, and the wait must be bounded.
/// </remarks>
internal static class MediaProcess
{
    /// <summary>Outcome of <see cref="Run" />. <paramref name="ExitCode" /> is
    /// null when the process had to be killed (timeout) or never started.
    /// <paramref name="StandardOutput" /> carries the drained stdout (empty for
    /// tools that only write to stderr; populated for e.g. ffprobe's JSON).</summary>
    internal readonly record struct MediaProcessResult(
        int? ExitCode, string StandardError, string StandardOutput = "")
    {
        public bool Succeeded => ExitCode == 0;
    }

    /// <summary>
    ///     Starts <paramref name="executable" />, drains stdout+stderr while it
    ///     runs and waits up to <paramref name="timeout" />. On expiry the whole
    ///     process tree is killed and a null exit code is returned. Throws what
    ///     <see cref="Process.Start(ProcessStartInfo)" /> throws (a missing or
    ///     broken executable) — callers decide how to degrade — and
    ///     <see cref="OperationCanceledException" /> when <paramref name="ct" />
    ///     fires.
    /// </summary>
    /// <remarks>
    ///     <paramref name="ct" /> kills the whole process tree when it fires, then
    ///     throws. It is optional, and the reason it exists is that the timeout
    ///     alone is a FIVE-MINUTE wall clock: a caller that offers the user a
    ///     Cancel button (the ONNX backend's transcode pre-pass) had a dead
    ///     affordance for up to that long on a wedged ffmpeg. The kill is what
    ///     makes the cancel real — abandoning the wait would leave an unreaped
    ///     child writing into a pipe nobody reads.
    /// </remarks>
    internal static MediaProcessResult Run(
        string executable, IReadOnlyList<string> arguments, TimeSpan timeout,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var startInfo = new ProcessStartInfo(executable)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            // The child speaks UTF-8 (ffprobe emits UTF-8 JSON). With no
            // explicit encoding .NET decodes a redirected pipe using the
            // CONSOLE code page, which on Windows is a legacy single-byte
            // page -- so every non-ASCII tag came back as mojibake. Owner
            // report: a Japanese album/artist/title imported
            // mangled. It reads as intermittent because a GUI process
            // without a console resolves a different page than one with.
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo);
        if (process is null) return new MediaProcessResult(null, string.Empty);

        // Async reads keep both pipes drained while we wait, so the child can
        // never block on a full buffer. Deliberately NOT given the token: a
        // cancelled READ leaves a still-writing child blocked on a full pipe with
        // nobody to reap it — the same reasoning as FfmpegStemEncoder.RunProcessAsync.
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEndAsync();

        // The kill unblocks the wait below by ending the process, so cancellation
        // needs no separate wake-up. Disposed before the drains are read.
        using (ct.Register(() =>
               {
                   try { process.Kill(entireProcessTree: true); }
                   catch { /* already gone */ }
               }))
        {
            if (process.WaitForExit(timeout))
            {
                // The overload above only waits for the process; this one also waits
                // for the redirected streams to close, so the drains are complete.
                process.WaitForExit();
                ct.ThrowIfCancellationRequested();
                return new MediaProcessResult(
                    process.ExitCode, SafeResult(stderr, TimeSpan.FromSeconds(30)),
                    SafeResult(stdout, TimeSpan.FromSeconds(30)));
            }
        }

        {
            try { process.Kill(entireProcessTree: true); }
            catch { /* already gone */ }

            // A grandchild can still hold the write end, so this stays bounded.
            var timedOut = new MediaProcessResult(
                null, SafeResult(stderr, TimeSpan.FromSeconds(5)), SafeResult(stdout, TimeSpan.FromSeconds(5)));
            ct.ThrowIfCancellationRequested();
            return timedOut;
        }
    }

    private static string SafeResult(Task<string> read, TimeSpan budget)
    {
        try
        {
            return read.Wait(budget) ? read.Result : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}
