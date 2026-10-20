namespace DotCharter.Tone.Online;

/// <summary>
///     The ONE place in <c>Online/</c> <b>and <c>Update/</c></b> where an
///     <see cref="HttpClient" /> is built. Every client in either folder goes
///     through <see cref="CreateClient" />, which is linted by
///     <c>OnlineHttpPolicyTests</c> — a bare <c>new HttpClient()</c> anywhere
///     under those folders fails the gate. <c>Update/</c> is covered too: it has
///     the same exposure to a host we do not control, with more consequence,
///     because its download goes to the user's disk.
///     Two things it exists to guarantee:
///     <list type="number">
///         <item>
///             <b>One connection pool for the process, with a bounded
///             lifetime.</b> The DI-registered clients are singletons, but
///             <c>MusicBrainz.MusicBrainzClient</c> can be constructed per
///             lookup, so a private handler each meant a fresh connection pool
///             (and a fresh crop of TIME_WAIT sockets) per lookup. A shared <see cref="SocketsHttpHandler" /> passed with
///             <c>disposeHandler: false</c> makes each client cheap and keeps
///             <c>Dispose</c> honest. <see cref="SocketsHttpHandler.PooledConnectionLifetime" />
///             is the other half: a process-lifetime pool never notices a DNS
///             change, which is the classic singleton-HttpClient bug.
///         </item>
///         <item>
///             <b>A hard cap on how many bytes a response may cost us.</b>
///             Every client here reads its body with
///             <c>ReadAsStringAsync</c>, which is unbounded — a hostile or
///             broken server answering a leaderboard fetch with a multi-gigabyte
///             body would be materialized into memory in full before any of our
///             code ran. <see cref="HttpClient.MaxResponseContentBufferSize" />
///             stops that inside the buffering step: an over-cap body (declared
///             OR streamed) raises <see cref="HttpRequestException" /> with
///             <see cref="HttpRequestError.ConfigurationLimitExceeded" />, which
///             every client already funnels into a typed
///             <see cref="OnlineError.Network" /> failure. Detection is by that
///             enum and never by message text — the message is localized.
///         </item>
///     </list>
///     The cap does NOT apply to a streamed download
///     (<c>HttpCompletionOption.ResponseHeadersRead</c>): nothing is buffered
///     there, and <see cref="Tone3000.Tone3000Client.DownloadModelAsync" />
///     carries its own explicit byte cap.
/// </summary>
public static class OnlineHttp
{
    /// <summary>
    ///     Largest response body any <c>Online/</c> client will buffer. A
    ///     100-row leaderboard page is a few tens of KB and a 25-tone TONE3000
    ///     search page well under a megabyte, so this is roughly an order of
    ///     magnitude of headroom over anything legitimate.
    /// </summary>
    public const long MaxResponseBytes = 8L * 1024 * 1024;

    /// <summary>
    ///     Shared across every real (non-test) client in <c>Online/</c>. The two
    ///     <c>Update/</c> clients pass their OWN handler instead — they need
    ///     redirects followed, there is one of each per process, and a private
    ///     pool therefore costs nothing. They still take the cap, which is the
    ///     half that matters for them.
    /// </summary>
    private static readonly Lazy<SocketsHttpHandler> SharedHandler = new(
        () => new SocketsHttpHandler
        {
            // A singleton HttpClient over a process-lifetime pool never re-resolves
            // DNS; recycling connections every two minutes is the documented fix.
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
            MaxConnectionsPerServer = 8
        },
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    ///     Builds a client with <paramref name="timeout" /> and the response-size
    ///     cap applied. <paramref name="handler" /> is the tests' seam — when it
    ///     is supplied the client owns it (each test gets its own scripted
    ///     handler); when it is null the process-wide pooled handler is shared
    ///     and deliberately NOT disposed with the client.
    /// </summary>
    public static HttpClient CreateClient(
        TimeSpan timeout,
        HttpMessageHandler? handler = null,
        long maxResponseBytes = MaxResponseBytes)
    {
        var client = handler == null
            ? new HttpClient(SharedHandler.Value, disposeHandler: false)
            : new HttpClient(handler);
        client.Timeout = timeout;
        client.MaxResponseContentBufferSize = Math.Clamp(maxResponseBytes, 1024L, int.MaxValue);
        return client;
    }

    /// <summary>
    ///     True when <paramref name="ex" /> is the response-size cap tripping.
    ///     Matched on <see cref="HttpRequestException.HttpRequestError" />, never
    ///     on the message — that string is localized, so a German install would
    ///     silently stop matching.
    /// </summary>
    public static bool IsResponseTooLarge(Exception? ex) =>
        ex is HttpRequestException { HttpRequestError: HttpRequestError.ConfigurationLimitExceeded };

    /// <summary>
    ///     A user-facing reason for a transport failure. Only the size cap gets a
    ///     rewrite: its framework message is a localized sentence about buffer
    ///     sizes that reads as a bug in DotCharter rather than as a bad response.
    /// </summary>
    public static string Describe(Exception ex) =>
        IsResponseTooLarge(ex)
            ? $"The server's response was over the {MaxResponseBytes:N0}-byte size cap and was refused."
            : ex.Message;
}
