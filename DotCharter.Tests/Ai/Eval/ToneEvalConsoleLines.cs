namespace DotCharter.Tests.Ai.Eval;

/// <summary>
///     The console lines a MACHINE reads back out of a live eval run.
///     <para>
///         A run writes its artifacts to <c>artifacts/ai-eval/&lt;timestamp&gt;/</c>,
///         and the timestamp is only known to the process that made it. That is
///         fine for a human reading the console and useless to
///         <c>tools/ai-eval/Compare-Models.ps1</c>, which drives thirteen runs and
///         has to find each one's <c>results.json</c>. So the directory is printed
///         on a line of its own, with a fixed prefix that lives HERE — one place,
///         named by both the producer and the parser — rather than as a format
///         string repeated in a test and a regex.
///     </para>
/// </summary>
public static class ToneEvalConsoleLines
{
    /// <summary>The prefix <c>Compare-Models.ps1</c> greps for. Changing it breaks
    /// the comparison harness, which is why <c>ToneEvalCompareScriptTests</c> pins
    /// the script's own pattern against this constant.</summary>
    public const string ArtifactPrefix = "[ai-eval] artifacts: ";

    /// <summary>The artifact-directory line, verbatim.</summary>
    /// <param name="directory">The run's artifact directory.</param>
    /// <returns>The single line to print.</returns>
    public static string Artifacts(string directory) => ArtifactPrefix + directory;
}
