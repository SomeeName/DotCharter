using System.Text;
using System.Text.RegularExpressions;
using DotCharter.Services.Diagnostics;

namespace DotCharter.Tests.Services.Diagnostics;

/// <summary>
///     The repository is published (#750, #759), and a secret scanner run over
///     it must find nothing. The redactor and the never-log-a-secret tests need
///     credential-SHAPED values, so those live in <see cref="FakeSecrets" />:
///     split at the prefix in the source, FAKE in the body. This lint keeps any
///     tracked text file from holding a contiguous credential shape again, and
///     any tracked file from being a key or certificate container. The patterns
///     are assembled from pieces so this file never matches itself.
///     <c>.gitleaks.toml</c> carries the allow-list for domain data (gear keys)
///     that gitleaks' generic rule mistakes for a key.
/// </summary>
public sealed class FakeSecretFixtureTests
{
    private static readonly Regex[] CredentialShapes =
    [
        new(@"\b" + "github" + "_pat_" + @"[A-Za-z0-9_]{20,}", RegexOptions.CultureInvariant),
        new(@"\b" + "gh" + @"[pousr]_[A-Za-z0-9]{30,}", RegexOptions.CultureInvariant),
        new(@"\b" + "AK" + @"IA[0-9A-Z]{16}\b", RegexOptions.CultureInvariant),
        new(@"\b" + "xo" + @"x[baprs]-[A-Za-z0-9-]{10,}", RegexOptions.CultureInvariant),
        new(@"\b" + "sk" + @"-ant-[a-z]+[0-9]{2}-[A-Za-z0-9_-]{20,}", RegexOptions.CultureInvariant),
        new(@"\b" + "sk" + @"-(?:proj-)?[A-Za-z0-9]{20,}\b", RegexOptions.CultureInvariant),
        new(@"\b" + "AI" + @"za[0-9A-Za-z_-]{35}", RegexOptions.CultureInvariant),
        new("ey" + @"J[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}", RegexOptions.CultureInvariant),
        new("-----" + "BEGIN" + @"[ A-Z]*PRIVATE KEY-----", RegexOptions.CultureInvariant)
    ];

    private static readonly string[] KeyContainerExtensions = [".pfx", ".p12", ".pem", ".key", ".snk", ".jks"];

    [Fact]
    public void Every_fake_secret_says_it_is_fake_and_still_has_a_real_shape()
    {
        Assert.NotEmpty(FakeSecrets.All);
        foreach (var secret in FakeSecrets.All)
        {
            Assert.True(secret.Contains("FAKE", StringComparison.Ordinal) || secret.Contains("EXAMPLE", StringComparison.Ordinal),
                $"'{secret}' does not say FAKE or EXAMPLE.");
            Assert.True(CredentialShapes.Any(p => p.IsMatch(secret)), $"'{secret}' no longer has a credential shape.");
            Assert.DoesNotContain(secret, SensitiveTextRedactor.Redact($"using {secret} now"));
        }
    }

    /// <summary>The probe twin: every shape fires on a target built from
    /// pieces, and ordinary words with the same prefixes stay legal.</summary>
    [Fact]
    public void Every_shape_matches_its_target_and_nothing_ordinary()
    {
        string[] targets =
        [
            .. FakeSecrets.All,
            "sk" + "-ant-api03-" + "FAKEexampleFAKEexample0000",
            "AI" + "za" + new string('F', 35),
            "-----" + "BEGIN RSA PRIVATE KEY-----"
        ];
        foreach (var pattern in CredentialShapes)
            Assert.True(targets.Any(pattern.IsMatch), $"Shape '{pattern}' has no probe target.");

        string[] ordinary =
        [
            "ghp_test_token", "gho_FAKE_after_502", "sk-ant-test-key", "task-0123456789abcdefghijkl",
            "CancellationToken token", "Bearer abc.def.ghi", "AKIA", "eyJ.x.y"
        ];
        foreach (var text in ordinary)
            Assert.False(CredentialShapes.Any(p => p.IsMatch(text)), $"An ordinary string is flagged: '{text}'.");
    }

    [Fact]
    public void No_tracked_file_holds_a_contiguous_credential_or_a_key_container()
    {
        var root = TestPaths.RepoRoot();
        Assert.SkipUnless(Path.Exists(Path.Combine(root, ".git")),
            "Not a git checkout (no .git) — the tracked-file list is unknowable here.");

        var tracked = PersonalDataLintTests.TrackedFiles(root);
        // The sentinel the scan must reach: the redactor's own tests.
        Assert.Contains("DotCharter.Tests/Services/Diagnostics/SensitiveTextRedactorTests.cs", tracked);

        var hits = new List<string>();
        var scanned = 0;
        foreach (var relative in tracked)
        {
            if (KeyContainerExtensions.Contains(Path.GetExtension(relative), StringComparer.OrdinalIgnoreCase))
                hits.Add($"{relative}: a key or certificate container");

            var path = Path.Combine(root, relative);
            if (!File.Exists(path)) continue; // the submodule gitlink, or a file deleted in the work tree

            var bytes = File.ReadAllBytes(path);
            if (PersonalDataLintTests.IsBinary(bytes)) continue;

            scanned++;
            var lines = Encoding.UTF8.GetString(bytes).Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (CredentialShapes.Any(p => p.IsMatch(lines[i])))
                    hits.Add($"{relative}:{i + 1}");
            }
        }

        Assert.True(scanned > 1000, $"Only {scanned} tracked text files scanned.");
        Assert.True(hits.Count == 0,
            "A credential-shaped value in a tracked file. If it is a test fixture, use (or add to) " +
            "DotCharter.Tests/Services/Diagnostics/FakeSecrets.cs; if it is REAL, rotate it first:\n" +
            string.Join('\n', hits));
    }
}
