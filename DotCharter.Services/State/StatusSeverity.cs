namespace DotCharter.Services.State;

/// <summary>
///     How the ONE status-bar line should read — carried beside
///     <see cref="ProjectState.MainDisplayMessage" /> so the rail can draw a
///     failed save differently from a finished one.
///     <para>
///         Before this existed roughly 225 writers set the same plain string,
///         so "Project saved" and "Save failed: access denied" were the same
///         faint mono text; a save that failed inside a <c>catch</c> was
///         indistinguishable from one that worked unless the user READ the
///         line. <see cref="ProjectState.SetMessage(string, StatusSeverity)" />
///         is additive — the one-argument form and the
///         <see cref="ProjectState.MainDisplayMessage" /> setter both mean
///         <see cref="Info" /> — so the ~225 existing writers compile
///         unchanged, and only the failure and completion paths name a level.
///     </para>
///     <para>
///         <c>UI/StatusSeverityLintTests</c> is what keeps the migration honest:
///         a status write inside a <c>catch</c> block that does not carry
///         <see cref="Error" /> fails the default gate.
///     </para>
/// </summary>
public enum StatusSeverity
{
    /// <summary>Neutral progress or state ("Loaded chart", "No chart loaded").</summary>
    Info = 0,

    /// <summary>An operation the user asked for finished ("Project saved to …").</summary>
    Success = 1,

    /// <summary>It worked, but not the way it was asked to (partial result,
    /// something skipped, a capability the engine could not honour).</summary>
    Warning = 2,

    /// <summary>It did not happen. Every <c>catch</c> path that writes the line
    /// carries this; the rail keeps it lit until the next write instead of
    /// letting it fade among the neutral messages.</summary>
    Error = 3
}
