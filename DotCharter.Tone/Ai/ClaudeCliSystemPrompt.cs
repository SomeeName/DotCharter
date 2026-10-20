using System.Text;

namespace DotCharter.Tone.Ai;

/// <summary>
///     Whether the installed <c>claude</c> binary can take its system prompt from a
///     FILE — the whole of W9, because it decides where the ~35,000-character
///     grounding goes.
///
///     <para>
///         <b>The CLI's option parser is NOT strict: an unknown flag is accepted
///         silently.</b> So "it ran and did not error" proves nothing about a flag,
///         and this is asked of <c>--help</c> instead, once per process. A machine
///         whose CLI does not advertise it keeps the old stdin layout rather than
///         passing a flag that would be dropped on the floor along with the prompt
///         it carries.
///     </para>
/// </summary>
public interface IClaudeCliCapabilityProbe
{
    /// <summary>True when this binary takes <c>--system-prompt-file</c>. Cached for
    /// the life of the process — a user who upgrades the CLI restarts the app, which
    /// is the same deal <see cref="ClaudeCliLocator" /> makes for the PATH walk.
    /// Never throws: a probe that cannot run answers false, which is the old
    /// layout.</summary>
    Task<bool> SupportsSystemPromptFileAsync(string executablePath, CancellationToken ct);
}

/// <summary>The real probe: one <c>claude --help</c> per process, read for the flag.</summary>
public sealed class ClaudeCliCapabilityProbe : IClaudeCliCapabilityProbe
{
    /// <summary>The literal option this feature needs.</summary>
    public const string SystemPromptFileFlag = "--system-prompt-file";

    /// <summary>
    ///     The SHORTHAND form 2.1.273's help actually prints. The flag has no entry
    ///     of its own in the option list on that version — it appears only inside
    ///     <c>--bare</c>'s description, as
    ///     <c>"Explicitly provide context via: --system-prompt[-file],
    ///     --append-system-prompt[-file], --add-dir"</c> — and it WORKS
    ///     (verified on 2.1.273: a file saying "answer BANANA" produced
    ///     BANANA, and a 25k-token file came back with 24,334 cache READ tokens on
    ///     the next run). Accepting only the literal spelling would therefore find
    ///     nothing and leave the feature permanently off on the machine it was
    ///     written for.
    /// </summary>
    public const string SystemPromptFileShorthand = "--system-prompt[-file]";

    /// <summary>How long a <c>--help</c> may take. It is a local Node start-up, not
    /// a network call.</summary>
    public static readonly TimeSpan HelpTimeout = TimeSpan.FromSeconds(30);

    private readonly IAppLogger? _logger;
    private readonly IClaudeCliRunner _runner;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _workingDirectory;
    private bool? _answer;

    /// <summary>Creates the probe over the same process seam the provider uses.</summary>
    public ClaudeCliCapabilityProbe(
        IClaudeCliRunner runner, IAppLogger? logger = null, string? workingDirectory = null)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _logger = logger;
        _workingDirectory = workingDirectory ?? Path.GetTempPath();
    }

    private static ClaudeCliCapabilityProbe? _shared;

    /// <summary>
    ///     The probe to use for <paramref name="runner" />.
    ///     <para>
    ///         A provider is minted PER REQUEST (the factory does), so a probe owned
    ///         by the provider would spawn one <c>claude --help</c> per design — the
    ///         opposite of "once per process". The REAL runner therefore gets one
    ///         shared probe for the life of the process; anything else (a test's fake)
    ///         gets its own, so a fake can never answer for the real binary nor the
    ///         other way round.
    ///     </para>
    /// </summary>
    internal static IClaudeCliCapabilityProbe For(
        IClaudeCliRunner runner, IAppLogger? logger, string? workingDirectory)
    {
        if (runner is not ClaudeCliRunner) return new ClaudeCliCapabilityProbe(runner, logger, workingDirectory);
        return _shared ??= new ClaudeCliCapabilityProbe(runner, logger, workingDirectory);
    }

    /// <summary>
    ///     Whether <paramref name="help" /> advertises the flag, in either of the two
    ///     spellings the CLI has used. PURE, so the parsing rule is unit-tested
    ///     against captured help text rather than only through a spawn.
    /// </summary>
    public static bool HelpAdvertisesSystemPromptFile(string? help)
        => !string.IsNullOrWhiteSpace(help)
           && (help.Contains(SystemPromptFileFlag, StringComparison.Ordinal)
               || help.Contains(SystemPromptFileShorthand, StringComparison.Ordinal));

    /// <inheritdoc />
    public async Task<bool> SupportsSystemPromptFileAsync(string executablePath, CancellationToken ct)
    {
        if (_answer is { } cached) return cached;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_answer is { } raced) return raced;

            bool answer;
            try
            {
                var outcome = await _runner.RunAsync(
                    new ClaudeCliRequest(
                        executablePath, ["--help"], string.Empty, _workingDirectory, HelpTimeout),
                    ct).ConfigureAwait(false);

                answer = !outcome.TimedOut
                         && HelpAdvertisesSystemPromptFile(outcome.StandardOutput + "\n" + outcome.StandardError);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is ToneDesignException or IOException)
            {
                // A probe that cannot run is not a reason to fail a design; it is a
                // reason to take the layout that works everywhere.
                _logger?.Debug(AppLogCategory.Application,
                    $"Claude Code CLI capability probe failed ({ex.GetType().Name}); using the stdin layout.");
                answer = false;
            }

            _answer = answer;
            _logger?.Info(AppLogCategory.Application,
                answer
                    ? "Claude Code CLI: --system-prompt-file is available; the grounding goes in the " +
                      "SYSTEM prompt, where the CLI's own cache breakpoint covers it."
                    : "Claude Code CLI: --system-prompt-file is not advertised; the grounding stays on stdin " +
                      "(no cross-request prompt-cache reuse when the request text changes).");
            return answer;
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>
///     The grounding, written to a file the CLI can be pointed at.
///
///     <para>
///         <b>One file per CONTENT, not per request.</b> The name carries a hash of
///         the bytes, so two designs with the same catalogue and the same capture
///         library reuse one file — and a library that gained a capture writes a new
///         one instead of silently reusing a stale prompt. Stale siblings are deleted
///         on the first write of a process, which is also the cleanup for a run that
///         crashed before it could tidy up.
///     </para>
///     <para>
///         <b>The framing goes in the FILE's head, not on a second flag.</b>
///         <c>--system-prompt-file</c> REPLACES the CLI's own coding-agent prompt,
///         exactly as the short <c>--system-prompt</c> did before it, so the framing
///         has to travel with the grounding or it is lost.
///     </para>
/// </summary>
public static class ClaudeCliSystemPrompt
{
    /// <summary>The prefix every file this type writes shares; the cleanup sweep
    /// keys off it and so must never match anything else under the folder.</summary>
    public const string FileNamePrefix = "dc-tone-system-";

    private static int _sweptThisProcess;

    /// <summary>The exact bytes of the system-prompt file for
    /// <paramref name="prompt" />: the framing, then the grounding. PURE, so the
    /// content — and therefore the hash the file is named after — is testable
    /// without touching the disk.</summary>
    public static string Compose(ToneDesignPrompt prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        var sb = new StringBuilder(prompt.System.Length + 512);
        sb.AppendLine(ClaudeCliToneModel.FramingPrompt);
        sb.AppendLine();
        sb.Append(prompt.System);
        return sb.ToString();
    }

    /// <summary>The file name for <paramref name="content" />: the prefix plus an
    /// FNV-1a hash of the bytes. Same grounding ⇒ same name ⇒ one file and one cache
    /// prefix; different grounding ⇒ a different file, never an overwrite that a
    /// concurrent design could read half of.</summary>
    public static string FileNameFor(string content)
    {
        var hash = 14695981039346656037UL;
        foreach (var b in Encoding.UTF8.GetBytes(content ?? ""))
        {
            hash ^= b;
            hash *= 1099511628211UL;
        }

        return $"{FileNamePrefix}{hash:x16}.txt";
    }

    /// <summary>
    ///     Writes (or reuses) the system-prompt file for <paramref name="prompt" />
    ///     under <paramref name="directory" /> and returns its absolute path, or null
    ///     when it could not be written.
    ///     <para>
    ///         Null is a normal outcome, not an error: the caller falls back to the
    ///         stdin layout, which needs no filesystem at all. A design must never
    ///         fail because a temp file could not be created.
    ///     </para>
    /// </summary>
    public static string? TryWrite(string? directory, ToneDesignPrompt prompt, IAppLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        if (string.IsNullOrWhiteSpace(directory)) return null;

        try
        {
            var content = Compose(prompt);
            var path = Path.Combine(directory, FileNameFor(content));

            Directory.CreateDirectory(directory);

            // Once per process: drop every prompt file that is not the one we are
            // about to use. A changed catalogue leaves the old file behind, and a
            // crash leaves it behind forever.
            if (Interlocked.Exchange(ref _sweptThisProcess, 1) == 0)
                Sweep(directory, keep: Path.GetFileName(path));

            // Same content ⇒ same bytes; rewriting would only risk a concurrent
            // design reading a half-written file.
            if (!File.Exists(path)) File.WriteAllText(path, content);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or ArgumentException or NotSupportedException)
        {
            logger?.Debug(AppLogCategory.Application,
                $"Claude Code CLI: could not write the system-prompt file ({ex.GetType().Name}); using stdin.");
            return null;
        }
    }

    /// <summary>Tests only: lets a case exercise the once-per-process sweep.</summary>
    internal static void ResetSweepForTesting() => Interlocked.Exchange(ref _sweptThisProcess, 0);

    private static void Sweep(string directory, string keep)
    {
        try
        {
            foreach (var stale in Directory.EnumerateFiles(directory, FileNamePrefix + "*.txt"))
            {
                if (string.Equals(Path.GetFileName(stale), keep, StringComparison.OrdinalIgnoreCase)) continue;
                try { File.Delete(stale); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // Nothing to clean, or not allowed to. Neither is worth a word.
        }
    }
}
