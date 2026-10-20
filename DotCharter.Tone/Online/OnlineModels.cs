namespace DotCharter.Tone.Online;

/// <summary>Why an online call did not produce a value.</summary>
public enum OnlineError
{
    None = 0,

    /// <summary>No credentials configured for the service being called
    /// (e.g. no TONE3000 client id).</summary>
    NotConfigured,

    /// <summary>The operation needs an account and nobody is signed in.</summary>
    NotSignedIn,

    /// <summary>DNS / socket / timeout — the service was unreachable.</summary>
    Network,

    /// <summary>Credentials rejected, or the session could not be refreshed.</summary>
    Unauthorized,

    /// <summary>The service answered with a non-success status.</summary>
    Server,

    /// <summary>The request was rejected as invalid.</summary>
    InvalidRequest
}

/// <summary>
///     Typed outcome of every call under <c>Online/</c>. Online code never
///     throws to its callers — offline, misconfigured and server-error states
///     all surface here so gameplay and UI can degrade gracefully.
/// </summary>
public sealed record OnlineResult<T>(bool Ok, T? Value, OnlineError Error, string Message)
{
    public static OnlineResult<T> Success(T value) => new(true, value, OnlineError.None, string.Empty);

    public static OnlineResult<T> Fail(OnlineError error, string message = "") =>
        new(false, default, error, message);
}
