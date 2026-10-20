using System.Net;
using System.Reflection;
using System.Text;
using DotCharter.Tone.Online;
using DotCharter.Services.Online.MusicBrainz;
using DotCharter.Tone.Online.Tone3000;
using DotCharter.Services.Update;

namespace DotCharter.Tests.Services.Online;

/// <summary>
///     Every HTTP client in <c>Online/</c> and <c>Update/</c> must come from
///     <see cref="OnlineHttp.CreateClient" />, and the reason is two shipped
///     hazards rather than tidiness.
///     <para>
///         <b>An unbounded body.</b> Every client here reads its response with
///         <c>ReadAsStringAsync</c>, which has no cap: a hostile or broken
///         server answering a leaderboard fetch with a multi-gigabyte body was
///         materialized into memory in full before a single line of our code
///         ran. The factory sets <see cref="HttpClient.MaxResponseContentBufferSize" />
///         so the buffering step refuses it and every client's existing
///         catch-all turns it into a typed failure.
///     </para>
///     <para>
///         <b>A private connection pool per client.</b>
///         <see cref="MusicBrainzClient" /> may be constructed per lookup, not once — a bare
///         <c>new HttpClient()</c> there is the textbook socket-exhaustion
///         shape. The factory hands out one shared, lifetime-bounded
///         <c>SocketsHttpHandler</c> instead.
///     </para>
///     <para>
///         <b><c>Update/</c> is governed too, from 0.45.</b> Its two clients read
///         GitHub's release JSON and payload manifest with <c>ReadAsStringAsync</c>
///         and had no cap at all. They pass their OWN handler to the factory
///         rather than taking the shared pool — the update client must follow
///         redirects, and there is one of each per process — so what the factory
///         gives them is the cap, which is the half that was missing.
///     </para>
/// </summary>
public sealed class OnlineHttpPolicyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── (a) the source lint ──────────────────────────────────────────────────

    /// <summary>
    ///     The folders whose HTTP clients must come from the factory. It is a
    ///     FOLDER scan and not a file list for the reason <c>LanPortRangeTests</c>
    ///     already argued: a file list only ever covers the code that existed when
    ///     someone wrote it, and the next client is added to a new file.
    ///     <para>
    ///         <c>Update/</c> joined in 0.45. It had exactly the same exposure and
    ///         no lint at all: two clients reading GitHub's answers with
    ///         <c>ReadAsStringAsync</c> and no cap of any kind.
    ///     </para>
    /// </summary>
    private static readonly string[] GovernedFolders =
        ["DotCharter.Services/Online", "DotCharter.Tone/Online", "DotCharter.Services/Update"];

    /// <summary>
    ///     <c>OnlineHttp.cs</c> is the one sanctioned <c>new HttpClient(</c>
    ///     site. Anything else in a governed folder has skipped both guarantees
    ///     above, and neither failure is visible in a passing unit test — the cap
    ///     only matters against a body no fixture produces, and the pool only
    ///     matters after a few hundred saves.
    /// </summary>
    [Fact]
    public void OnlineHttpIsTheOnlyPlaceAnHttpClientIsConstructed()
    {
        var offenders = new List<string>();

        foreach (var folder in GovernedFolders)
        {
            foreach (var file in Directory.EnumerateFiles(
                     RepoPath(folder), "*.cs", SearchOption.AllDirectories))
            {
                if (string.Equals(Path.GetFileName(file), "OnlineHttp.cs", StringComparison.Ordinal)) continue;

                var src = StripLineComments(File.ReadAllText(file));
                if (src.Contains("new HttpClient(", StringComparison.Ordinal))
                    offenders.Add(Path.GetFileName(file));
            }
        }

        Assert.True(offenders.Count == 0,
            "These files build an HttpClient directly, skipping the response-size cap and the shared " +
            $"connection pool — route them through OnlineHttp.CreateClient: {string.Join(", ", offenders)}.");
    }

    /// <summary>
    ///     The four clients OUTSIDE the governed folders, and why they are not
    ///     simply moved inside it. Each one STREAMS a large body — a 340 MB
    ///     alignment model, a demucs checkpoint, a CUDA runtime, a Songsterr
    ///     tab — so <c>OnlineHttp</c>'s 8 MB buffering cap is the wrong policy for
    ///     them and routing them through the factory would break the download it
    ///     is meant to protect.
    ///     <para>
    ///         The OTHER half of the factory's job still applies, and none of them
    ///         had it: a process-lifetime connection pool never re-resolves DNS.
    ///         So the rule for these is narrower — whatever handler they build must
    ///         bound <c>PooledConnectionLifetime</c>, which means
    ///         <c>SocketsHttpHandler</c>, because the setting does not exist on
    ///         <c>HttpClientHandler</c>.
    ///     </para>
    /// </summary>
    private static readonly string[] UngovernedHttpClientFiles =
    [
        "DotCharter.Services/Audio/Lyrics/LyricModelAssetFetcher.cs",
        // StemModelManager.cs was the fourth of these until 2026-09-11: it no
        // longer builds a client at all. Every stem-model fetch goes through
        // JuceSharp's OnnxModelCatalog.EnsureAsync over VerifiedDownloader,
        // which resumes, fails over between mirrors and verifies before the
        // rename — the app's own copy did none of the three. Pinned from the
        // other side by StemModelDownloadIntegrityTests; do not put it back on
        // this list without putting an HttpClient back with it.
    ];

    public static TheoryData<string> UngovernedHttpClients => [.. UngovernedHttpClientFiles];

    [Theory]
    [MemberData(nameof(UngovernedHttpClients))]
    public void ADownloadClientOutsideTheGovernedFoldersStillBoundsItsConnectionPool(string relativePath)
    {
        var path = RepoPath(relativePath);
        Assert.True(File.Exists(path), $"{relativePath} has moved — repoint this lint.");

        var src = StripLineComments(File.ReadAllText(path));

        Assert.True(src.Contains("PooledConnectionLifetime", StringComparison.Ordinal),
            $"{Path.GetFileName(path)} builds an HttpClient whose connection pool lives as long as the " +
            "process, so it never re-resolves DNS — the classic singleton-HttpClient bug. Bound it with " +
            "SocketsHttpHandler.PooledConnectionLifetime.");

        // The DEFAULT handler, not a test seam: HttpClientHandler cannot carry
        // that setting at all, so its presence here means the bound is on
        // something else (or on nothing).
        Assert.False(src.Contains("new HttpClientHandler", StringComparison.Ordinal),
            $"{Path.GetFileName(path)} still builds an HttpClientHandler — PooledConnectionLifetime does " +
            "not exist on it, so the bound above cannot be doing anything. Use SocketsHttpHandler.");
    }

    /// <summary>Keeps the list honest from the other side: no client may appear
    /// outside BOTH the governed folders and the list above. A new download
    /// client is added to a new file, and the whole point of a scan over a list
    /// is that the list is the thing that rots.</summary>
    [Fact]
    public void NoHttpClientIsBuiltOutsideTheGovernedFoldersAndTheKnownDownloadClients()
    {
        var known = UngovernedHttpClientFiles
            .Select(Path.GetFileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var offenders = new[] { "DotCharter.Services", "DotCharter.Tone" }
            .SelectMany(p => Directory.EnumerateFiles(RepoPath(p), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f => !GovernedFolders.Any(g =>
                f.StartsWith(RepoPath(g) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            .Where(f => !known.Contains(Path.GetFileName(f)))
            .Where(f => StripLineComments(File.ReadAllText(f))
                .Contains("new HttpClient(", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToArray();

        Assert.True(offenders.Length == 0,
            "These files build an HttpClient outside every rule that exists — either route them through " +
            "OnlineHttp.CreateClient, or add them to UngovernedHttpClients (which requires a bounded " +
            $"connection pool): {string.Join(", ", offenders)}.");
    }

    /// <summary>
    ///     Keeps the folder list from silently shrinking to nothing: each governed
    ///     folder must exist and must actually contain a client that reaches the
    ///     factory, or the scan above is passing because it found no code.
    /// </summary>
    [Fact]
    public void EveryGovernedFolderExistsAndActuallyUsesTheFactory()
    {
        foreach (var folder in GovernedFolders)
        {
            var path = RepoPath(folder);
            Assert.True(Directory.Exists(path), $"{folder} does not exist — repoint this lint.");

            var reaches = Directory
                .EnumerateFiles(path, "*.cs", SearchOption.AllDirectories)
                .Any(f => StripLineComments(File.ReadAllText(f))
                    .Contains("OnlineHttp.CreateClient", StringComparison.Ordinal));

            Assert.True(reaches,
                $"Nothing in {folder} builds a client through OnlineHttp.CreateClient — either the folder " +
                "stopped making HTTP calls (drop it from GovernedFolders) or a client slipped past the lint.");
        }
    }

    /// <summary>
    ///     The two <c>Update/</c> clients specifically: they were the shipped gap,
    ///     and both read GitHub's response bodies into strings. This asserts the
    ///     cap is live on a REAL constructed instance, not that the source
    ///     mentions the factory.
    /// </summary>
    [Fact]
    public void TheUpdateClients_CarryTheResponseSizeCap()
    {
        using var updates = new GitHubUpdateService(
            new Version(0, 1, 0), handler: new NeverCalledHandler(),
            installDir: Path.GetTempPath(), downloadDir: Path.GetTempPath());
        using var auth = new GitHubAuthService(
            new GitHubTokenStore(Path.GetTempPath()), handler: new NeverCalledHandler());

        foreach (var client in new[] { HttpClientOf(updates), HttpClientOf(auth) })
            Assert.Equal(OnlineHttp.MaxResponseBytes, client.MaxResponseContentBufferSize);
    }

    /// <summary>The clients are private fields; the cap is only observable on the
    /// instance, so this reaches for it rather than asserting on source text.</summary>
    private static HttpClient HttpClientOf(object service)
    {
        var field = service.GetType()
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(f => f.FieldType == typeof(HttpClient));
        return (HttpClient)field.GetValue(service)!;
    }

    /// <summary>Keeps the lint above from passing vacuously: the sanctioned site
    /// must still be the thing that constructs, and must still apply the cap.</summary>
    [Fact]
    public void TheFactoryStillConstructsAndStillCaps()
    {
        var src = File.ReadAllText(RepoPath("DotCharter.Tone/Online/OnlineHttp.cs"));
        Assert.Contains("new HttpClient(", src, StringComparison.Ordinal);
        Assert.Contains("MaxResponseContentBufferSize", src, StringComparison.Ordinal);
        Assert.Contains("PooledConnectionLifetime", src, StringComparison.Ordinal);
    }

    // ── (b) the cap actually bites ───────────────────────────────────────────

    [Fact]
    public void TheFactoryAppliesTheCapAndAFloor()
    {
        using var capped = OnlineHttp.CreateClient(TimeSpan.FromSeconds(5), new NeverCalledHandler());
        Assert.Equal(OnlineHttp.MaxResponseBytes, capped.MaxResponseContentBufferSize);

        // A nonsense cap must not disable the protection by underflowing.
        using var silly = OnlineHttp.CreateClient(
            TimeSpan.FromSeconds(5), new NeverCalledHandler(), maxResponseBytes: 0);
        Assert.True(silly.MaxResponseContentBufferSize >= 1024);
    }

    /// <summary>
    ///     The framework signals the cap through
    ///     <see cref="HttpRequestError.ConfigurationLimitExceeded" />. We match
    ///     on that enum and never on the message, which is localized — this
    ///     repo's own build output is German, so a message match would have
    ///     stopped working on the developer's machine first.
    /// </summary>
    [Fact]
    public async Task AnOversizedBody_IsRefusedByTheTransport_AndIsRecognizable()
    {
        using var client = OnlineHttp.CreateClient(
            TimeSpan.FromSeconds(30), new FixedBodyHandler(new string('x', 4096)), maxResponseBytes: 1024);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetAsync("https://example.invalid/big", Ct));

        Assert.True(OnlineHttp.IsResponseTooLarge(ex));
        Assert.Contains("size cap", OnlineHttp.Describe(ex), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("size cap", OnlineHttp.Describe(new HttpRequestException("dns down")),
            StringComparison.OrdinalIgnoreCase);
    }

    // ── (c) end to end: a real over-cap body through a real client ───────────

    /// <summary>
    ///     The only test here that materializes a genuinely oversized body, and
    ///     it goes through the client with the widest attack surface (the search
    ///     endpoint is the one place a third-party server chooses the response).
    ///     The body is SHAPED like a real search page, so a client that somehow
    ///     read it would parse it happily — the assertion proves the cap refused
    ///     it, not that the JSON was odd.
    /// </summary>
    [Fact]
    public async Task Tone3000_OversizedSearchBody_FailsTypedRatherThanThrowing()
    {
        var oversized = $$"""
                          { "data": [{ "id": 1, "title": "{{new string(
                              'p', (int)OnlineHttp.MaxResponseBytes + (1 << 20))}}" }],
                            "page": 1, "total": 1 }
                          """;
        using var client = new Tone3000Client(
            accessTokenProvider: _ => Task.FromResult<string?>("tok"),
            handler: new FixedBodyHandler(oversized));

        var result = await client.SearchAsync(new Tone3000SearchQuery("x"), Ct);

        Assert.False(result.Ok);
        Assert.Equal(OnlineError.Network, result.Error);
        Assert.Contains("size cap", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── (d) every other client MAPS the refusal, rather than leaking it ──────
    // The transport-level refusal is proved once, above and in the factory test.
    // What each client owes is that the resulting exception becomes a typed
    // failure with a stated reason instead of escaping the throw-free contract —
    // so these hand it the exact exception the transport raises, at no cost.

    private static HttpRequestException CapTripped() =>
        new(HttpRequestError.ConfigurationLimitExceeded,
            "Cannot write more bytes to the buffer than the configured maximum buffer size.");

    [Fact]
    public async Task MusicBrainz_OversizedBody_FailsTypedRatherThanThrowing()
    {
        using var client = new MusicBrainzClient(
            handler: new ThrowingHandler(CapTripped), delay: (_, _) => Task.CompletedTask);

        var result = await client.FindLatinNamesAsync("バンド", "曲", "", Ct);

        Assert.False(result.Ok);
        Assert.Equal(OnlineError.Network, result.Error);
        Assert.Contains("size cap", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── fixtures ─────────────────────────────────────────────────────────────

    private sealed class FixedBodyHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
    }

    private sealed class ThrowingHandler(Func<Exception> fault) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw fault();
    }

    private sealed class NeverCalledHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
    }

    // ── source-locating helpers (same idiom as LanPortRangeTests) ────────────

    private static string StripLineComments(string src)
    {
        var sb = new StringBuilder(src.Length);
        foreach (var line in src.Split('\n'))
        {
            var idx = line.IndexOf("//", StringComparison.Ordinal);
            sb.Append(idx >= 0 ? line[..idx] : line).Append('\n');
        }

        return sb.ToString();
    }

    private static string RepoPath(string relative) =>
        Path.Combine(TestPaths.RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar));
}
