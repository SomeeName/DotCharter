namespace DotCharter.Tests.Services.Diagnostics;

/// <summary>
///     Credential-SHAPED test values for the redactor and the code that must
///     never log a secret. Each one has the exact shape the redactor (and a
///     secret scanner) looks for, and each says FAKE or EXAMPLE in its body.
///     The source splits every value at its prefix, so the file never holds a
///     contiguous token: a secret scanner run over the published tree finds
///     nothing here, and the test still sees the whole token because the
///     halves are joined at compile time (they stay <c>const</c>, usable in
///     <c>[InlineData]</c>). <c>FakeSecretFixtureTests</c> keeps it that way.
/// </summary>
internal static class FakeSecrets
{
    /// <summary>GitHub classic PAT shape: <c>ghp_</c> + 36 alphanumerics.</summary>
    public const string GitHubPat = "gh" + "p_" + "FAKEFAKEFAKEFAKEFAKEFAKEFAKEFAKE0000";

    /// <summary>GitHub fine-grained PAT shape: <c>github_pat_</c> + 20 or more.</summary>
    public const string GitHubFineGrainedPat = "github" + "_pat_" + "FAKE_EXAMPLE_FAKE_EXAMPLE_0000";

    /// <summary>Slack bot token shape: <c>xoxb-</c> + 10 or more.</summary>
    public const string SlackToken = "xo" + "xb-" + "FAKE-EXAMPLE-0000";

    /// <summary>AWS access key id shape: <c>AKIA</c> + 16 upper-case alphanumerics.</summary>
    public const string AwsAccessKeyId = "AK" + "IA" + "FAKEEXAMPLE00000";

    /// <summary>OpenAI-style key shape: <c>sk-</c> + 20 or more alphanumerics.</summary>
    public const string OpenAiKey = "sk" + "-" + "FAKEexampleFAKEexample0000";

    /// <summary>Three-part JWT shape: <c>eyJ…</c>.<c>…</c>.<c>…</c>.</summary>
    public const string Jwt = "ey" + "J" + "FAKEheader" + "." + "FAKEpayload" + "." + "FAKEsignature";

    /// <summary>Every value above, for the "stays obviously fake" lint.</summary>
    public static readonly string[] All =
        [GitHubPat, GitHubFineGrainedPat, SlackToken, AwsAccessKeyId, OpenAiKey, Jwt];
}
