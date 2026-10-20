using System.Net;
using System.Text;
using DotCharter.Tone.Online;
using DotCharter.Tone.Online.Tone3000;

namespace DotCharter.Tests.Services.Online;

/// <summary>
/// Tests for <see cref="Tone3000Client" /> against a scripted
/// <see cref="HttpMessageHandler" /> (no sockets): request shaping, the
/// schema-tolerant JSON mapping, the throw-free typed-result contract and the
/// download path's auth gate + size cap.
/// </summary>
public sealed class Tone3000ClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public readonly List<HttpRequestMessage> Requests = [];

        public ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        /// <summary>Never answers — the request completes only when the caller's
        /// own budget cancels it. Used to prove a timeout is TYPED, not thrown.</summary>
        public bool Hang { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (Hang) await Task.Delay(Timeout.Infinite, cancellationToken);
            return _respond(request);
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private const string SearchBody = """
        {
          "tones": [
            {
              "id": 41,
              "title": "Plexi Crunch",
              "user": { "username": "capturist" },
              "gear_type": "amp",
              "license": { "name": "CC-BY", "url": "https://creativecommons.org/licenses/by/4.0/" },
              "url": "https://www.tone3000.com/tones/41",
              "description": "A hot plexi",
              "formats": ["nam"],
              "sizes": ["standard", "nano"],
              "downloads": 1234
            },
            { "no_id_no_title": true },
            {
              "id": 42,
              "title": "Song X Lead",
              "author": "shredder",
              "type": "amp-cab",
              "license": "CC0"
            }
          ],
          "page": 2,
          "total": 57
        }
        """;

    [Fact]
    public async Task Search_ParsesTolerantly_AndShapesQuery()
    {
        var handler = new ScriptedHandler(_ => Json(SearchBody));
        using var client = new Tone3000Client(handler: handler);

        var result = await client.SearchAsync(new Tone3000SearchQuery(
            "tone for song x", Tone3000GearType.AmpCab, Format: "nam", Size: "standard",
            Sort: Tone3000Sort.DownloadsAllTime, Page: 2), Ct);

        Assert.True(result.Ok);
        var page = result.Value!;
        Assert.Equal(2, page.Tones.Count); // the id-less element is skipped, not fatal
        Assert.Equal(2, page.Page);
        Assert.Equal(57, page.TotalCount);

        var first = page.Tones[0];
        Assert.Equal(41, first.Id);
        Assert.Equal("Plexi Crunch", first.Title);
        Assert.Equal("capturist", first.Author);          // nested user object
        Assert.Equal("amp", first.GearType);
        Assert.Equal("CC-BY", first.License);             // license object → name
        Assert.Equal("https://creativecommons.org/licenses/by/4.0/", first.LicenseUrl);
        Assert.Equal(["standard", "nano"], first.Sizes);
        Assert.Equal(1234, first.Downloads);

        var second = page.Tones[1];
        Assert.Equal("shredder", second.Author);          // flat author string
        Assert.Equal("amp-cab", second.GearType);         // "type" spelling
        Assert.Equal("CC0", second.License);              // flat license string

        var url = handler.Requests.Single().RequestUri!.AbsoluteUri;
        Assert.Contains("/api/v1/tones/search?", url);
        Assert.Contains("query=tone%20for%20song%20x", url);
        Assert.Contains("gears=amp-cab", url);
        Assert.Contains("format=nam", url);
        Assert.Contains("sizes=standard", url);
        Assert.Contains("sort=downloads-all-time", url);
        Assert.Contains("page=2", url);
    }

    // ── the query-string contract ────────────────────────────────────────────
    // The parameter NAMES are the documented contract (tone3000.com/api +
    // github.com/tone-3000/api `searchTones`). The server ignores unknown
    // parameters, so a wrong spelling does not fail — it silently searches for
    // nothing with no filters, which is exactly what "search doesn't work"
    // looked like. These pin the names, not just the values.

    [Fact]
    public void SearchQueryString_UsesTheDocumentedParameterNames()
    {
        var qs = Tone3000Wire.BuildSearchQueryString(new Tone3000SearchQuery(
            "fender", Tone3000GearType.AmpCab, Format: "nam", Size: "lite",
            Sort: Tone3000Sort.Trending, Page: 3, PerPage: 20));

        Assert.Equal(
            "query=fender&page=3&page_size=20&sort=trending&gears=amp-cab&sizes=lite&format=nam", qs);

        // …and NOT the old spellings, which the server drops on the floor.
        var names = qs.Split('&').Select(p => p.Split('=')[0]).ToList();
        Assert.Equal(["query", "page", "page_size", "sort", "gears", "sizes", "format"], names);
        Assert.DoesNotContain("q", names);
        Assert.DoesNotContain("per_page", names);
        Assert.DoesNotContain("gear_type", names);
        Assert.DoesNotContain("size", names);
    }

    [Fact]
    public void SearchQueryString_OmitsEveryEmptyFilter_AndTheServerDefaultSort()
    {
        // An empty `gears=`/`format=` is a filter VALUE, not "no filter"; and
        // omitting `sort` is what makes the server pick best-match with a query
        // and trending without one.
        var qs = Tone3000Wire.BuildSearchQueryString(new Tone3000SearchQuery(string.Empty));

        Assert.Equal("page=1&page_size=24", qs);
    }

    [Theory]
    [InlineData(Tone3000Architecture.A1, "architecture=1")]
    [InlineData(Tone3000Architecture.A2, "architecture=2")]
    [InlineData(Tone3000Architecture.Custom, "architecture=custom")]
    public void SearchQueryString_SendsTheArchitectureFilter(Tone3000Architecture arch, string expected)
    {
        Assert.Contains(expected,
            Tone3000Wire.BuildSearchQueryString(new Tone3000SearchQuery("x", Architecture: arch)));
    }

    [Fact]
    public void SearchQueryString_DefaultArchitecture_OmitsTheParameter()
    {
        // Omitting is NOT "all architectures" — it is the server's legacy
        // A1 + Custom set, which is exactly why A2 captures were unfindable.
        // Sending nothing is still the right default; the UI offers A2.
        Assert.DoesNotContain("architecture",
            Tone3000Wire.BuildSearchQueryString(new Tone3000SearchQuery("x")));
    }

    [Fact]
    public void SearchQueryString_ClampsPageSizeToTheServerMaximum()
    {
        // page_size max is 25 — a bigger ask is a 400, not more results.
        Assert.Contains($"page_size={Tone3000Wire.MaxPageSize}",
            Tone3000Wire.BuildSearchQueryString(new Tone3000SearchQuery("x", PerPage: 100)));
        Assert.Contains("page_size=1",
            Tone3000Wire.BuildSearchQueryString(new Tone3000SearchQuery("x", PerPage: 0)));
        Assert.Contains("page=1",
            Tone3000Wire.BuildSearchQueryString(new Tone3000SearchQuery("x", Page: -4)));
    }

    // ── legibility: search must leave a trace either way ─────────────────────
    // The pre-fix client logged NOTHING for search at ANY level, so an owner
    // looking at an empty panel had no log line to act on — downloads were
    // visible, search was invisible.

    [Fact]
    public async Task Search_Success_LogsWhatItAskedFor_AndWhatCameBack()
    {
        var logger = new DotCharter.Tests.Infrastructure.FakeAppLogger();
        var handler = new ScriptedHandler(_ => Json(SearchBody));
        using var client = new Tone3000Client(logger: logger, handler: handler);

        await client.SearchAsync(new Tone3000SearchQuery("plexi", Tone3000GearType.Amp,
            Architecture: Tone3000Architecture.A2), Ct);

        var line = Assert.Single(logger.Entries, e => e.Level == "INFO");
        Assert.Contains("query=plexi", line.Message);
        Assert.Contains("gears=amp", line.Message);
        Assert.Contains("architecture=2", line.Message);  // the filter is IN the line
        Assert.Contains("2 tone(s)", line.Message);       // and so is the outcome
    }

    [Fact]
    public async Task Search_Rejected_LogsAWarningWithTheStatusAndTheServersReason()
    {
        var logger = new DotCharter.Tests.Infrastructure.FakeAppLogger();
        var handler = new ScriptedHandler(_ =>
            Json("""{ "error": "invalid architecture" }""", HttpStatusCode.BadRequest));
        using var client = new Tone3000Client(logger: logger, handler: handler);

        var result = await client.SearchAsync(new Tone3000SearchQuery("plexi"), Ct);

        Assert.False(result.Ok);
        Assert.True(logger.HasEntryContaining("WARN", "400"));
        Assert.True(logger.HasEntryContaining("WARN", "invalid architecture"));
        Assert.True(logger.HasEntryContaining("WARN", "tones/search"));
        Assert.DoesNotContain(logger.Entries, e => e.Level == "INFO");
    }

    [Fact]
    public async Task Search_NetworkFailure_LogsAWarningNotADebugLine()
    {
        // Debug is off in a default install, which is why a dead connection
        // produced an empty panel and a silent log.
        var logger = new DotCharter.Tests.Infrastructure.FakeAppLogger();
        var handler = new ScriptedHandler(_ => throw new HttpRequestException("dns down"));
        using var client = new Tone3000Client(logger: logger, handler: handler);

        await client.SearchAsync(new Tone3000SearchQuery("plexi"), Ct);

        Assert.True(logger.HasEntryContaining("WARN", "dns down"));
    }

    [Fact]
    public async Task Search_WithSession_SendsBearer()
    {
        var handler = new ScriptedHandler(_ => Json(SearchBody));
        using var client = new Tone3000Client(
            accessTokenProvider: _ => Task.FromResult<string?>("tok-abc"), handler: handler);

        var result = await client.SearchAsync(new Tone3000SearchQuery("x"), Ct);

        Assert.True(result.Ok);
        var auth = handler.Requests.Single().Headers.Authorization;
        Assert.Equal("Bearer", auth!.Scheme);
        Assert.Equal("tok-abc", auth.Parameter);
    }

    [Fact]
    public async Task Search_WithoutSession_OmitsAuthorizationHeader()
    {
        // No provider (and provider returning null) must send NO Authorization
        // header — never a malformed/empty one.
        var handler = new ScriptedHandler(_ => Json(SearchBody));
        using var noProvider = new Tone3000Client(handler: handler);
        using var nullToken = new Tone3000Client(
            accessTokenProvider: _ => Task.FromResult<string?>(null), handler: handler);

        await noProvider.SearchAsync(new Tone3000SearchQuery("x"), Ct);
        await nullToken.SearchAsync(new Tone3000SearchQuery("x"), Ct);

        Assert.All(handler.Requests, r => Assert.Null(r.Headers.Authorization));
    }

    [Fact]
    public async Task Search_Unauthorized_MapsNotSignedIn()
    {
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent(
                """{ "error": "missing/invalid authorization header" }""",
                Encoding.UTF8, "application/json")
        });
        using var client = new Tone3000Client(handler: handler);

        var result = await client.SearchAsync(new Tone3000SearchQuery("x"), Ct);

        Assert.False(result.Ok);
        Assert.Equal(OnlineError.NotSignedIn, result.Error);
    }

    [Fact]
    public async Task Search_RootArrayBody_IsAccepted()
    {
        var handler = new ScriptedHandler(_ => Json("""[{ "id": 7, "title": "Bare" }]"""));
        using var client = new Tone3000Client(handler: handler);

        var result = await client.SearchAsync(new Tone3000SearchQuery("bare"), Ct);

        Assert.True(result.Ok);
        Assert.Single(result.Value!.Tones);
        Assert.Equal(7, result.Value.Tones[0].Id);
    }

    [Fact]
    public async Task Search_ServerError_FailsTypedWithoutThrowing()
    {
        var handler = new ScriptedHandler(_ =>
            Json("""{ "error": "boom" }""", HttpStatusCode.InternalServerError));
        using var client = new Tone3000Client(handler: handler);

        var result = await client.SearchAsync(new Tone3000SearchQuery("x"), Ct);

        Assert.False(result.Ok);
        Assert.Equal(OnlineError.Server, result.Error);
        Assert.Equal("boom", result.Message);
    }

    [Fact]
    public async Task Search_NonJsonBody_FailsTypedServer()
    {
        var handler = new ScriptedHandler(_ => Json("<html>proxy</html>"));
        using var client = new Tone3000Client(handler: handler);

        var result = await client.SearchAsync(new Tone3000SearchQuery("x"), Ct);

        Assert.False(result.Ok);
        Assert.Equal(OnlineError.Server, result.Error);
    }

    // ── the response shape the live API actually returns ─────────────────────
    // Modelled on the documented `PaginatedResponse<Tone[]>` / `Model` types
    // (tone3000.com/api → Types). The tolerant probes above keep older/renamed
    // spellings working; these pin the CURRENT ones, which is where the mapping
    // had silently drifted (blank gear chips, zero downloads, no architecture).

    private const string LiveShapeSearchBody = """
        {
          "data": [
            {
              "id": 82929,
              "user_id": "u-1",
              "user": { "id": "u-1", "username": "capturist", "url": "https://t3k/u/1" },
              "title": "Fender Twin Pack",
              "description": "A clean platform",
              "gear": "amp",
              "format": "nam",
              "license": "cc-by",
              "sizes": ["standard", "lite"],
              "makes": [{ "name": "Fender" }],
              "tags": [{ "name": "clean" }],
              "models_count": 4,
              "a1_models_count": 2,
              "a2_models_count": 2,
              "irs_count": 0,
              "custom_models_count": 0,
              "downloads_count": 1234,
              "favorites_count": 7,
              "url": "https://www.tone3000.com/tones/82929"
            }
          ],
          "page": 1,
          "page_size": 24,
          "total": 57,
          "total_pages": 3
        }
        """;

    [Fact]
    public async Task Search_MapsTheCurrentResponseShape()
    {
        var handler = new ScriptedHandler(_ => Json(LiveShapeSearchBody));
        using var client = new Tone3000Client(handler: handler);

        var result = await client.SearchAsync(new Tone3000SearchQuery("fender"), Ct);

        Assert.True(result.Ok);
        var tone = Assert.Single(result.Value!.Tones);
        Assert.Equal(82929, tone.Id);
        Assert.Equal("Fender Twin Pack", tone.Title);
        Assert.Equal("capturist", tone.Author);   // nested `user.username`
        Assert.Equal("amp", tone.GearType);       // `gear` — was probed as gear_type/type only
        Assert.Equal("cc-by", tone.License);
        Assert.Equal(["nam"], tone.Formats);      // scalar `format` folded to a list
        Assert.Equal(["standard", "lite"], tone.Sizes);
        Assert.Equal(1234, tone.Downloads);       // `downloads_count`
        Assert.Equal(2, tone.A1ModelCount);
        Assert.Equal(2, tone.A2ModelCount);
        Assert.True(tone.HasA2);
        Assert.True(tone.HasLegacyArchitecture);
        Assert.Equal(1, result.Value.Page);
        Assert.Equal(57, result.Value.TotalCount);
    }

    [Fact]
    public async Task GetModels_MapsArchitectureVersion()
    {
        var handler = new ScriptedHandler(_ => Json("""
            {
              "data": [
                {
                  "id": 9, "tone_id": 82929, "user_id": 1, "name": "Standard A2",
                  "size": "standard", "architecture_version": "2",
                  "model_url": "https://www.tone3000.com/api/v1/models/9/file",
                  "created_at": "2026-01-01", "updated_at": "2026-01-01"
                },
                {
                  "id": 10, "tone_id": 82929, "name": "Lite A1",
                  "size": "lite", "architecture_version": "1",
                  "model_url": "https://www.tone3000.com/api/v1/models/10/file"
                }
              ],
              "page": 1, "page_size": 24, "total": 2, "total_pages": 1
            }
            """));
        using var client = new Tone3000Client(handler: handler);

        var result = await client.GetModelsAsync(82929, Ct);

        Assert.True(result.Ok);
        var models = result.Value!;
        Assert.Equal(["2", "1"], models.Select(m => m.Architecture));
        Assert.Equal(["standard", "lite"], models.Select(m => m.SizeTier));
        // The API carries no per-model format — empty means UNKNOWN.
        Assert.All(models, m => Assert.Equal(string.Empty, m.Format));
    }

    [Fact]
    public void ParseSearchPage_UnrecognizedShape_DegradesToNoResults_NeverThrows()
    {
        // A schema change must read as "nothing matched", not as an exception
        // the caller turns into a scary error row.
        Assert.Empty(Tone3000Json.ParseSearchPage("""{ "unexpected": { "nested": 1 } }""", 1).Tones);
        Assert.Empty(Tone3000Json.ParseSearchPage("""{ "data": "not-an-array" }""", 1).Tones);
        Assert.Empty(Tone3000Json.ParseSearchPage("""{ "data": [1, 2, "three", null] }""", 1).Tones);
        Assert.Empty(Tone3000Json.ParseSearchPage("[]", 1).Tones);
        Assert.Empty(Tone3000Json.ParseModels("""{ "data": [{ "id": 1 }] }""", 41));
    }

    [Fact]
    public async Task GetModels_ParsesEntries_AndSkipsLinklessOnes()
    {
        var handler = new ScriptedHandler(_ => Json("""
            {
              "models": [
                {
                  "id": 9, "tone_id": 41, "name": "Standard",
                  "format": "NAM", "size": "Standard", "architecture": "wavenet",
                  "model_url": "https://cdn.tone3000.com/models/9.nam", "file_size": 12345
                },
                { "id": 10, "name": "broken — no url" }
              ]
            }
            """));
        using var client = new Tone3000Client(handler: handler);

        var result = await client.GetModelsAsync(41, Ct);

        Assert.True(result.Ok);
        var model = Assert.Single(result.Value!);
        Assert.Equal(9, model.Id);
        Assert.Equal(41, model.ToneId);
        Assert.Equal("nam", model.Format);      // normalized lower-case
        Assert.Equal("standard", model.SizeTier);
        Assert.Equal("https://cdn.tone3000.com/models/9.nam", model.ModelUrl);
        Assert.Equal(12345, model.FileBytes);

        Assert.Contains("/api/v1/models?tone_id=41",
            handler.Requests.Single().RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task GetTone_WrappedBody_Parses()
    {
        var handler = new ScriptedHandler(_ =>
            Json("""{ "tone": { "id": 41, "title": "Plexi", "license": "T3K" } }"""));
        using var client = new Tone3000Client(handler: handler);

        var result = await client.GetToneAsync(41, Ct);

        Assert.True(result.Ok);
        Assert.Equal("Plexi", result.Value!.Title);
        Assert.Equal("T3K", result.Value.License);
    }

    private static Tone3000Model Model(string url) =>
        new(9, 41, "Standard", "nam", "standard", "wavenet", url, 0);

    [Fact]
    public async Task Download_WithoutToken_FailsNotSignedIn_NeverHitsNetwork()
    {
        var handler = new ScriptedHandler(_ => Json("should not be reached"));
        using var client = new Tone3000Client(handler: handler);
        using var dest = new MemoryStream();

        var result = await client.DownloadModelAsync(
            Model("https://cdn.tone3000.com/models/9.nam"), dest, maxBytes: 1024, ct: Ct);

        Assert.False(result.Ok);
        Assert.Equal(OnlineError.NotSignedIn, result.Error);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Download_StreamsBytes_AndSendsBearer()
    {
        var payload = new byte[2048];
        new Random(42).NextBytes(payload);
        var handler = new ScriptedHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
        using var client = new Tone3000Client(
            accessTokenProvider: _ => Task.FromResult<string?>("tok-123"), handler: handler);
        using var dest = new MemoryStream();

        var result = await client.DownloadModelAsync(
            Model("https://cdn.tone3000.com/models/9.nam"), dest, maxBytes: 1 << 20, ct: Ct);

        Assert.True(result.Ok);
        Assert.Equal(payload.Length, result.Value);
        Assert.Equal(payload, dest.ToArray());
        var auth = handler.Requests.Single().Headers.Authorization;
        Assert.Equal("Bearer", auth!.Scheme);
        Assert.Equal("tok-123", auth.Parameter);
    }

    [Fact]
    public async Task Download_ExceedingCap_FailsTyped()
    {
        var handler = new ScriptedHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[4096]) });
        using var client = new Tone3000Client(
            accessTokenProvider: _ => Task.FromResult<string?>("tok"), handler: handler);
        using var dest = new MemoryStream();

        var result = await client.DownloadModelAsync(
            Model("https://cdn.tone3000.com/models/9.nam"), dest, maxBytes: 100, ct: Ct);

        Assert.False(result.Ok);
        Assert.Equal(OnlineError.InvalidRequest, result.Error);
    }

    [Fact]
    public async Task Download_Unauthorized_MapsNotSignedIn()
    {
        // A stale token that the server rejects with 401 maps to NotSignedIn
        // (the caller must re-authenticate), same as anonymous search.
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        using var client = new Tone3000Client(
            accessTokenProvider: _ => Task.FromResult<string?>("stale"), handler: handler);
        using var dest = new MemoryStream();

        var result = await client.DownloadModelAsync(
            Model("https://cdn.tone3000.com/models/9.nam"), dest, maxBytes: 1024, ct: Ct);

        Assert.False(result.Ok);
        Assert.Equal(OnlineError.NotSignedIn, result.Error);
    }

    [Fact]
    public async Task NetworkFailure_FailsTypedWithoutThrowing()
    {
        var handler = new ScriptedHandler(_ => throw new HttpRequestException("dns down"));
        using var client = new Tone3000Client(handler: handler);

        var result = await client.SearchAsync(new Tone3000SearchQuery("x"), Ct);

        Assert.False(result.Ok);
        Assert.Equal(OnlineError.Network, result.Error);
    }

    // ── "no matches" must not be what a signed-out user is told ──────────────
    // The live API is 401 for every anonymous request, so a 200-with-nothing to
    // a request we sent WITHOUT a token cannot be a genuine empty result — it is
    // a proxy, a captive portal or a schema change. Reporting it as success put
    // "no captures matched" in front of a user whose real problem was being
    // signed out, which is indistinguishable from a working, empty search.

    [Fact]
    public async Task Search_Anonymous_EmptyResult_IsReportedAsSignedOut_NotAsNoMatches()
    {
        var handler = new ScriptedHandler(_ => Json("""{ "data": [], "page": 1, "total": 0 }"""));
        var logger = new DotCharter.Tests.Infrastructure.FakeAppLogger();
        using var client = new Tone3000Client(logger: logger, handler: handler);

        var result = await client.SearchAsync(new Tone3000SearchQuery("plexi"), Ct);

        Assert.False(result.Ok);
        Assert.Equal(OnlineError.NotSignedIn, result.Error);
        Assert.Contains("sign-in", result.Message, StringComparison.OrdinalIgnoreCase);
        // It still went out — this is a verdict on the ANSWER, not a refusal to
        // ask, so a future anonymous-capable API keeps working.
        Assert.Single(handler.Requests);
        Assert.True(logger.HasEntryContaining("WARN", "anonymous"));
    }

    [Fact]
    public async Task Search_SignedIn_EmptyResult_IsStillAGenuineEmptyResult()
    {
        var handler = new ScriptedHandler(_ => Json("""{ "data": [], "page": 1, "total": 0 }"""));
        using var client = new Tone3000Client(
            accessTokenProvider: _ => Task.FromResult<string?>("tok"), handler: handler);

        var result = await client.SearchAsync(new Tone3000SearchQuery("zzzzz"), Ct);

        Assert.True(result.Ok);
        Assert.Empty(result.Value!.Tones);
    }

    [Fact]
    public async Task Search_Anonymous_WithResults_IsStillASuccess()
    {
        // Only the EMPTY anonymous answer is suspicious; a body with tones in it
        // is self-evidently a working search and must not be second-guessed.
        var handler = new ScriptedHandler(_ => Json(SearchBody));
        using var client = new Tone3000Client(handler: handler);

        var result = await client.SearchAsync(new Tone3000SearchQuery("x"), Ct);

        Assert.True(result.Ok);
        Assert.Equal(2, result.Value!.Tones.Count);
    }

    // ── the download does not share the metadata timeout ─────────────────────
    // HttpClient.Timeout is a deadline for the WHOLE exchange, including reading
    // the response stream — so one 30 s value on the shared client silently gave
    // a capture download (capped at 128 MB) thirty seconds to complete, and
    // reported the miss as an opaque network error.

    [Fact]
    public async Task ApiCall_HasItsOwnBudget_AndTimingOutIsTypedNotThrown()
    {
        var handler = new ScriptedHandler(_ => throw new InvalidOperationException("unreachable"))
        {
            Hang = true
        };
        using var client = new Tone3000Client(handler: handler, apiTimeout: TimeSpan.Zero);

        var result = await client.SearchAsync(new Tone3000SearchQuery("x"), Ct);

        Assert.False(result.Ok);
        Assert.Equal(OnlineError.Network, result.Error);
        Assert.Contains("timed out", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Download_HasItsOwnBudget_AndTimingOutIsTypedNotThrown()
    {
        var handler = new ScriptedHandler(_ => throw new InvalidOperationException("unreachable"))
        {
            Hang = true
        };
        using var client = new Tone3000Client(
            accessTokenProvider: _ => Task.FromResult<string?>("tok"),
            handler: handler, downloadTimeout: TimeSpan.Zero);
        using var dest = new MemoryStream();

        var result = await client.DownloadModelAsync(
            Model("https://cdn.tone3000.com/models/9.nam"), dest, maxBytes: 1 << 20, ct: Ct);

        Assert.False(result.Ok);
        Assert.Equal(OnlineError.Network, result.Error);
        Assert.Contains("timed out", result.Message, StringComparison.OrdinalIgnoreCase);
        // Not "Cancelled." — the caller cancelled nothing, and telling them they
        // did sends them looking for a click they never made.
        Assert.DoesNotContain("cancel", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     A SOURCE lint, because the regression it guards cannot be observed
    ///     from behaviour: with a scripted handler that answers instantly, no
    ///     timeout of any size ever fires, so a download wrongly wired to the
    ///     metadata budget still passes every functional test. What broke it in
    ///     production was one <c>HttpClient.Timeout</c> shared by both — that
    ///     property is a deadline for the WHOLE exchange including the response
    ///     stream, so a 128 MB capture had thirty seconds to arrive.
    /// </summary>
    [Fact]
    public void TheDownloadAndTheMetadataCallsUseSeparateBudgets()
    {
        var src = File.ReadAllText(RepoPath("DotCharter.Tone/Online/Tone3000/Tone3000Client.cs"));

        Assert.Contains("OnlineHttp.CreateClient(Timeout.InfiniteTimeSpan", src, StringComparison.Ordinal);
        Assert.Contains("CancelAfter(_downloadTimeout)", src, StringComparison.Ordinal);
        Assert.Contains("CancelAfter(_apiTimeout)", src, StringComparison.Ordinal);

        // A 128 MB cap over a domestic uplink is minutes, not seconds.
        Assert.True(Tone3000Client.DefaultDownloadTimeout > Tone3000Client.DefaultApiTimeout * 4,
            "The download budget must not collapse back towards the metadata one.");
    }

    private static string RepoPath(string relative) =>
        Path.Combine(TestPaths.RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar));

    // ── a server that quotes our own credential back ─────────────────────────

    [Fact]
    public void Scrub_RemovesTheTokenFromServerSuppliedText_AndKeepsTheRest()
    {
        Assert.Equal("bad *** here", Tone3000Client.Scrub("bad tok-secret here", "tok-secret"));
        Assert.Equal("nothing to do", Tone3000Client.Scrub("nothing to do", "tok-secret"));
        Assert.Equal("anonymous", Tone3000Client.Scrub("anonymous", null));
        Assert.Equal("anonymous", Tone3000Client.Scrub("anonymous", string.Empty));
    }
}
