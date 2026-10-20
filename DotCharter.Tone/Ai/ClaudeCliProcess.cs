using System.Diagnostics;
using System.Text;

namespace DotCharter.Tone.Ai;

/// <summary>
///     Where the <c>claude</c> executable is, and how a child process of it is run.
///     Two tiny seams so <see cref="ClaudeCliToneModel" /> can be tested without a
///     binary and without spawning anything.
///     <para>
///         There is no existing process seam in <c>DotCharter.Services</c> to reuse:
///         every other spawn site (<c>MediaProcess</c>, <c>YtDlpAudioDownloader</c>,
///         <c>VstSandboxHost</c>) builds its own
///         <see cref="ProcessStartInfo" /> inline against a very different contract —
///         long-running, progress-reporting, or shared-memory. This one is
///         request/response with stdin and a deadline, which is why it is its own
///         pair of interfaces rather than a generalisation of theirs.
///     </para>
///     <para>
///         Containment needs nothing here:
///         <c>Infrastructure.Process.ChildProcessJob</c> puts the app itself in a
///         kill-on-close job and a Windows child INHERITS it, so a <c>claude</c>
///         left behind by a crash is killed by the kernel like every other child.
///     </para>
/// </summary>
public interface IClaudeCliLocator
{
    /// <summary>
    ///     The absolute path to the <c>claude</c> executable, or null when there is
    ///     none.
    /// </summary>
    /// <param name="explicitPath">
    ///     An operator-supplied path (<see cref="AiProviderSettings.BaseUrl" /> for
    ///     <see cref="AiProviderKind.ClaudeCli" />). When it names a file that
    ///     exists it WINS and no search happens; when it names one that does not,
    ///     the answer is null rather than a silent fall back to <c>PATH</c> — a
    ///     configured path that is quietly ignored is how a user ends up debugging
    ///     the wrong binary.
    /// </param>
    string? Locate(string? explicitPath);
}

/// <summary>The real locator: an explicit path, else a <c>PATH</c> walk, cached.</summary>
public sealed class ClaudeCliLocator : IClaudeCliLocator
{
    /// <summary>The names tried on <c>PATH</c>, in order. On Windows the npm
    /// install drops a <c>claude.cmd</c> shim next to a native <c>claude.exe</c>;
    /// the extension-less <c>claude</c> is the shell script and is what exists on
    /// Linux and macOS. The native <c>.exe</c> is preferred because a <c>.cmd</c>
    /// has to be run through <c>cmd.exe</c>, which adds a quoting layer this code
    /// has no business owning.</summary>
    public static IReadOnlyList<string> CandidateNames { get; } = OperatingSystem.IsWindows()
        ? ["claude.exe", "claude.cmd", "claude.bat", "claude"]
        : ["claude"];

    // Cached for the life of the process: the search is a directory walk over every
    // PATH entry and the Settings picker asks on every provider change. A user who
    // installs the CLI while the app is running restarts it; that is the same deal
    // every other tool-detection in this app makes.
    private static readonly Lazy<string?> PathLookup = new(SearchPath, isThreadSafe: true);

    private readonly IAppLogger? _logger;

    /// <summary>Creates the locator.</summary>
    /// <param name="logger">Optional app logger; the resolved path is logged once at Debug.</param>
    public ClaudeCliLocator(IAppLogger? logger = null) => _logger = logger;

    /// <inheritdoc />
    public string? Locate(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var trimmed = explicitPath.Trim().Trim('"');
            return File.Exists(trimmed) ? Path.GetFullPath(trimmed) : null;
        }

        var found = PathLookup.Value;
        _logger?.Debug(AppLogCategory.Application,
            $"Claude Code CLI lookup: {found ?? "not found on PATH"}.");
        return found;
    }

    /// <summary>Drops the cached <c>PATH</c> answer. Tests only — the shipping app
    /// never needs it, and nothing outside this assembly can reach it.</summary>
    internal static string? SearchPathUncached() => SearchPath();

    private static string? SearchPath()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return null;

        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string full;
            try
            {
                // A PATH entry can be anything a shell ever wrote there, including
                // characters that are illegal in a path. One bad entry must not
                // stop the walk.
                full = Path.GetFullPath(dir.Trim().Trim('"'));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            foreach (var name in CandidateNames)
            {
                try
                {
                    var candidate = Path.Combine(full, name);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    // Same reason as above; try the next name.
                }
            }
        }

        return null;
    }
}

/// <summary>One headless CLI invocation. <paramref name="StandardInput" /> carries
/// the whole prompt, because it is ~30k characters and a Windows command line stops
/// at 32,767.</summary>
/// <param name="ExecutablePath">Absolute path to the <c>claude</c> binary.</param>
/// <param name="Arguments">The argument list, already split — never a command string.</param>
/// <param name="StandardInput">What is written to the child's stdin, then closed.</param>
/// <param name="WorkingDirectory">The child's cwd.</param>
/// <param name="Timeout">Wall-clock deadline for the whole run.</param>
public sealed record ClaudeCliRequest(
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string StandardInput,
    string WorkingDirectory,
    TimeSpan Timeout);

/// <summary>What the child said. <paramref name="TimedOut" /> is distinct from a
/// non-zero exit: one is our deadline, the other is the CLI's own answer.</summary>
/// <param name="ExitCode">The process exit code; -1 when it was killed.</param>
/// <param name="StandardOutput">Everything the child wrote to stdout.</param>
/// <param name="StandardError">Everything the child wrote to stderr.</param>
/// <param name="TimedOut">True when <see cref="ClaudeCliRequest.Timeout" /> expired.</param>
public sealed record ClaudeCliOutcome(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut = false)
{
    private const int MaxLoggedStandardErrorChars = 400;

    /// <summary>
    ///     The child's own text for the log, TRUNCATED and stdout-last. The design
    ///     reply is on stdout, so a detail line that quoted it whole would put the
    ///     user's tone in the log; only its LENGTH is logged.
    /// </summary>
    public string ToRedactedLogLine()
    {
        var err = StandardError.Trim();
        if (err.Length > MaxLoggedStandardErrorChars)
        {
            err = string.Concat(err.AsSpan(0, MaxLoggedStandardErrorChars), "…");
        }

        return $"exit={ExitCode}, stderr={(err.Length == 0 ? "(empty)" : err)}, stdout={StandardOutput.Length} chars";
    }
}

/// <summary>Runs one <see cref="ClaudeCliRequest" />. The tests' seam.</summary>
public interface IClaudeCliRunner
{
    /// <summary>Runs the child to completion, its deadline, or cancellation.</summary>
    /// <param name="request">What to run.</param>
    /// <param name="ct">Cancels the run and kills the process TREE.</param>
    /// <returns>The child's exit code and captured output.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="ct" /> fired.</exception>
    /// <exception cref="ToneDesignException">The process could not be started at all.</exception>
    Task<ClaudeCliOutcome> RunAsync(ClaudeCliRequest request, CancellationToken ct);
}

/// <summary>
///     The real runner. Captures both streams, writes stdin and closes it, and on
///     cancellation or timeout kills the whole process TREE — <c>claude</c> is a
///     Node program that spawns its own children, and killing only the parent
///     leaves them holding the pipes this method is still reading.
/// </summary>
public sealed class ClaudeCliRunner : IClaudeCliRunner
{
    /// <inheritdoc />
    public async Task<ClaudeCliOutcome> RunAsync(ClaudeCliRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        var info = new ProcessStartInfo
        {
            FileName = request.ExecutablePath,
            WorkingDirectory = request.WorkingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in request.Arguments) info.ArgumentList.Add(argument);

        using var process = new System.Diagnostics.Process { StartInfo = info };

        try
        {
            if (!process.Start())
            {
                throw new ToneDesignException(
                    ToneDesignFault.NotConfigured,
                    "The Claude Code command could not be started.",
                    request.ExecutablePath);
            }
        }
        catch (ToneDesignException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ToneDesignException(
                ToneDesignFault.NotConfigured,
                "The Claude Code command could not be started. Check the path under Settings → AI.",
                $"{ex.GetType().Name}: {ex.Message}", ex);
        }

        // The deadline and the caller's cancel are ONE token from here on, so the
        // kill path is written once. Which of the two fired is read back off the
        // caller's token afterwards — a timeout is an answer, a cancel is not.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(request.Timeout);

        var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var stderr = process.StandardError.ReadToEndAsync(deadline.Token);

        try
        {
            // Stdin first and CLOSED: `claude -p` reads the prompt to EOF, so a
            // stream left open is a child that never answers and a deadline that
            // always expires.
            await process.StandardInput.WriteAsync(request.StandardInput.AsMemory(), deadline.Token)
                .ConfigureAwait(false);
            await process.StandardInput.FlushAsync(deadline.Token).ConfigureAwait(false);
            process.StandardInput.Close();

            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            var outText = await stdout.ConfigureAwait(false);
            var errText = await stderr.ConfigureAwait(false);
            return new ClaudeCliOutcome(process.ExitCode, outText, errText);
        }
        catch (OperationCanceledException)
        {
            KillTree(process);
            if (ct.IsCancellationRequested) throw;
            return new ClaudeCliOutcome(-1, Drain(stdout), Drain(stderr), TimedOut: true);
        }
        catch (IOException ex)
        {
            // A broken pipe: the child died while stdin was being written. Its own
            // exit code and stderr are the real diagnosis, so let the caller see
            // them rather than reporting the pipe.
            KillTree(process);
            return new ClaudeCliOutcome(
                process.HasExited ? process.ExitCode : -1,
                Drain(stdout),
                string.IsNullOrWhiteSpace(Drain(stderr)) ? ex.Message : Drain(stderr));
        }
    }

    /// <summary>Whatever a stream task already produced, never a throw and never a
    /// wait: this runs on the failure path, where blocking on a pipe the child may
    /// still hold is exactly the hang being escaped.</summary>
    private static string Drain(Task<string> stream)
        => stream.IsCompletedSuccessfully ? stream.Result : string.Empty;

    private static void KillTree(System.Diagnostics.Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException
                                      or System.ComponentModel.Win32Exception)
        {
            // Already gone, or the OS will not say. Either way there is nothing
            // left to do and this is the cancellation path.
        }
    }
}
