namespace DotCharter.Services.Update;

/// <summary>
///     The payload was staged and validated but the updater process could not be
///     started — a job-object breakaway refused, an antivirus block on a
///     generated <c>.cmd</c>, a missing <c>cmd.exe</c>.
///     <para>
///         <b>This is its own type so the caller cannot treat it as "the update
///         failed, carry on".</b> Two things must follow from it and neither is
///         optional: the app must NOT exit (an exit here is the reported bug —
///         the app closes, nothing is copied, nothing relaunches), and the staged
///         payload must be kept so the next start can offer to finish it.
///     </para>
/// </summary>
public sealed class UpdaterLaunchException : Exception
{
    public UpdaterLaunchException(string message, Exception inner) : base(message, inner)
    {
    }
}
