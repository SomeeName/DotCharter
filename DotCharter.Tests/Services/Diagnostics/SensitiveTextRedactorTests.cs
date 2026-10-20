using DotCharter.Services.Diagnostics;

namespace DotCharter.Tests.Services.Diagnostics;

/// <summary>
///     Coverage for the ONE redactor. Was <c>CrashReportRedactorTests</c> until
///     2026-09-04, when the diagnostics bundle's second, weaker redactor was
///     folded into this one; the cases below the "folded in" banner are the ones
///     that only the diagnostics side used to have, plus the two holes the merge
///     closed (no user-name pass on the bundle, and an ANCHORED key/value regex
///     that could not see a secret inside a timestamped log line).
/// </summary>
public sealed class SensitiveTextRedactorTests
{
    [Fact]
    public void Redact_ReplacesWindowsUserPathSegment()
    {
        var input = @"Loading from C:\Users\Alex\AppData\Roaming\DotCharter\appconfig.json failed.";

        var result = SensitiveTextRedactor.Redact(input, userName: "Alex");

        Assert.DoesNotContain("Alex", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"C:\Users\<user>\AppData", result);
    }

    [Fact]
    public void Redact_ReplacesForeignWindowsUserFolder_EvenWithoutUserNameHint()
    {
        // A path from another machine appears in a shared log; the folder name
        // is not the current OS user, so only the path rule can catch it.
        var input = @"peer path D:\Users\SomeoneElse\charts\song.psarc";

        var result = SensitiveTextRedactor.Redact(input, userName: "Alex");

        Assert.DoesNotContain("SomeoneElse", result);
        Assert.Contains(@"D:\Users\<user>\charts", result);
    }

    [Fact]
    public void Redact_ReplacesUnixHomePath()
    {
        var input = "opening /home/alex/.config/DotCharter/logs/dotcharter.log";

        var result = SensitiveTextRedactor.Redact(input, userName: null);

        Assert.Contains("/home/<user>/.config", result);
        Assert.DoesNotContain("/home/alex/", result);
    }

    [Fact]
    public void Redact_ScrubsUserNameOutsidePaths()
    {
        var input = "Welcome back, Alex! Your session resumed.";

        var result = SensitiveTextRedactor.Redact(input, userName: "Alex");

        Assert.Equal("Welcome back, <user>! Your session resumed.", result);
    }

    [Fact]
    public void Redact_DoesNotScrubVeryShortUserName()
    {
        // A 1-char account name would shred unrelated text; guarded off.
        var input = "a and b and c";

        var result = SensitiveTextRedactor.Redact(input, userName: "a");

        Assert.Equal(input, result);
    }

    [Theory]
    [InlineData("password=hunter2")]
    [InlineData("Password: hunter2")]
    [InlineData("\"secret\": \"hunter2\"")]
    [InlineData("api_key=hunter2")]
    [InlineData("client_secret = hunter2")]
    [InlineData("AccessToken=hunter2")]
    public void Redact_ScrubsCredentialKeyValuePairs(string input)
    {
        var result = SensitiveTextRedactor.Redact(input);

        Assert.DoesNotContain("hunter2", result);
        Assert.Contains(SensitiveTextRedactor.Redacted, result);
    }

    [Fact]
    public void Redact_ScrubsBearerToken_InAuthorizationHeader()
    {
        // The Authorization key-value rule and the Bearer rule together fully
        // scrub the value (the exact placeholder layout is not contractual).
        var input = "Authorization: Bearer abc.def.ghijklmnop";

        var result = SensitiveTextRedactor.Redact(input);

        Assert.DoesNotContain("abc.def.ghijklmnop", result);
        Assert.Contains(SensitiveTextRedactor.Redacted, result);
    }

    [Fact]
    public void Redact_ScrubsBareBearerToken()
    {
        var input = "sending Bearer abc.def.ghijklmnop now";

        var result = SensitiveTextRedactor.Redact(input);

        Assert.DoesNotContain("abc.def.ghijklmnop", result);
        Assert.Contains("Bearer <redacted>", result);
    }

    [Fact]
    public void Redact_ScrubsJwt()
    {
        const string jwt = FakeSecrets.Jwt;
        var input = "token issued " + jwt;

        var result = SensitiveTextRedactor.Redact(input);

        Assert.DoesNotContain(jwt, result);
        Assert.Contains("<redacted-jwt>", result);
    }

    [Fact]
    public void Redact_ScrubsGitHubTokenPrefix()
    {
        const string input = "using " + FakeSecrets.GitHubPat;

        var result = SensitiveTextRedactor.Redact(input);

        Assert.DoesNotContain(FakeSecrets.GitHubPat, result);
        Assert.Contains("<redacted-token>", result);
    }

    [Fact]
    public void Redact_LeavesOrdinaryStackTraceWordsIntact()
    {
        // "CancellationToken" must NOT trip the "token" key rule (camelCase guard),
        // and a normal parameter list carries no separator+value.
        var input = "   at DotCharter.Foo.Bar(CancellationToken token, String path)";

        var result = SensitiveTextRedactor.Redact(input, userName: "Alex");

        Assert.Equal(input, result);
    }

    [Fact]
    public void Redact_IsIdempotent()
    {
        var input = @"user C:\Users\Alex\x password=hunter2 Bearer abc.def.ghi";

        var once = SensitiveTextRedactor.Redact(input, "Alex");
        var twice = SensitiveTextRedactor.Redact(once, "Alex");

        Assert.Equal(once, twice);
    }

    [Fact]
    public void Redact_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, SensitiveTextRedactor.Redact(null));
        Assert.Equal(string.Empty, SensitiveTextRedactor.Redact(string.Empty));
    }

    // ---- folded in from the retired DiagnosticsRedactor ---------------------

    [Theory]
    [InlineData(FakeSecrets.GitHubPat)]
    [InlineData(FakeSecrets.GitHubFineGrainedPat)]
    [InlineData(FakeSecrets.SlackToken)]
    [InlineData(FakeSecrets.AwsAccessKeyId)]
    [InlineData(FakeSecrets.OpenAiKey)]
    [InlineData(FakeSecrets.Jwt)]
    public void Redact_ScrubsEveryTokenShapeEitherRedactorKnew(string secret)
    {
        // github_pat_ and AKIA were known ONLY to the diagnostics redactor; the
        // crash one had never heard of either.
        var result = SensitiveTextRedactor.Redact($"connecting with {secret} now");

        Assert.DoesNotContain(secret, result);
        Assert.Contains("redacted", result, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The regression that made the bundle leak worse than a path leak: the
    ///     diagnostics key/value regex was anchored with <c>^</c>, so in a real
    ///     Serilog line the key group bound to the TIMESTAMP, the line was judged
    ///     non-sensitive, and the pass that exists to catch embedded secrets did
    ///     nothing at all.
    /// </summary>
    [Theory]
    [InlineData("2026-09-04 12:00:00.000 +02:00 [ERRO] [Tone3000] refresh failed refreshToken=FAKE-refresh-0000")]
    [InlineData("2026-09-04 12:00:00.000 +02:00 [INFO] [Tone3000] auth ok accessToken=FAKE-refresh-0000 done")]
    [InlineData("""2026-09-04 12:00:00.000 +02:00 [WARN] payload {"apiKey": "FAKE-refresh-0000"}""")]
    public void ASecretInTheMiddleOfALogLineIsScrubbed(string line)
    {
        var result = SensitiveTextRedactor.Redact(line);

        Assert.DoesNotContain("FAKE-refresh-0000", result);
        Assert.Contains(SensitiveTextRedactor.Redacted, result);
        // The timestamp is not a key/value pair and must survive intact.
        Assert.Contains("2026-09-04 12:00:00.000 +02:00", result);
    }

    /// <summary>
    ///     <c>AppConfig</c> declares <c>Tone3000ClientId</c> (an older config still carries
    ///     <c>GitHubOAuthClientId</c> and <c>SpotifyClientId</c>), and the bundle carries <c>appconfig.json</c>
    ///     verbatim. A word-boundary key match cannot see the <c>ClientId</c> in
    ///     <c>Tone3000ClientId</c> (the preceding character is a digit) — which is
    ///     why the merged redactor matches key fragments by CONTAINS.
    /// </summary>
    [Theory]
    [InlineData(""""  "SpotifyClientId": "FAKE-client-0000","""")]
    [InlineData(""""  "Tone3000ClientId": "FAKE-client-0000","""")]
    [InlineData(""""  "GitHubOAuthClientId": "FAKE-client-0000","""")]
    [InlineData("Tone3000ApiKey=FAKE-client-0000")]
    public void AClientIdEmbeddedInALongerKeyIsStillScrubbed(string line)
    {
        var result = SensitiveTextRedactor.Redact(line);

        Assert.DoesNotContain("FAKE-client-0000", result);
        Assert.Contains(SensitiveTextRedactor.Redacted, result);
    }

    [Theory]
    [InlineData(""""  "themePresetId": "midnight","""", false)]
    [InlineData("sampleRate = 48000", false)]
    [InlineData("password = hunter2", true)]
    public void AnInnocentKeyValueLineIsLeftAlone(string line, bool shouldRedact)
    {
        var result = SensitiveTextRedactor.Redact(line);

        if (shouldRedact) Assert.Contains(SensitiveTextRedactor.Redacted, result);
        else Assert.Equal(line, result);
    }

    /// <summary>A redacted JSON line stays parseable-ish: the trailing comma survives.</summary>
    [Fact]
    public void Redact_KeepsJsonTrailingComma()
    {
        var result = SensitiveTextRedactor.Redact(""""  "token": "FAKE-token-0000","""");

        Assert.EndsWith(",", result);
        Assert.DoesNotContain("FAKE-token-0000", result);
    }
}
