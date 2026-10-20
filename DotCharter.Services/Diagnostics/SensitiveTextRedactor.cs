using System.Text.RegularExpressions;

namespace DotCharter.Services.Diagnostics;

/// <summary>
///     The ONE redaction pass in the app. Every artifact a user is invited to
///     attach to a bug report — the crash bundle
///     (<c>Infrastructure/CrashReporting/CrashReportBuilder</c>) and the
///     diagnostics zip (<see cref="DiagnosticsBundleExporter" />: system-info,
///     <c>appconfig.json</c>, the log tails) — goes through this and nothing
///     else.
///     <list type="bullet">
///         <item>the OS user name, anywhere (paths, "Welcome Alex", …);</item>
///         <item>the user segment of Windows (<c>C:\Users\NAME</c>) and Unix
///               (<c>/home/NAME</c>, <c>/Users/NAME</c>) home paths — including
///               paths from OTHER machines that appear in a shared log;</item>
///         <item>JWTs and well-known token prefixes (GitHub PAT + OAuth, Slack,
///               AWS access key ids, OpenAI);</item>
///         <item><c>Bearer &lt;token&gt;</c> authorization values;</item>
///         <item><c>key=value</c> / <c>"key": "value"</c> pairs whose key
///               CONTAINS a sensitive fragment, matched <b>un-anchored</b>;</item>
///         <item>the Rocksmith keys of <c>appconfig.json</c> (<c>PsarcToc</c>,
/// <c>SngPc</c>, <c>SngMac</c>) and the old key file's
///               <c>psarc-toc</c> / <c>sng-pc</c> / <c>sng-mac</c> lines, whole
///               value to the closing quote or the end of the line — spaces
///               included, so a key pasted as spaced hex pairs goes too. An EMPTY
///               value stays <c>""</c>: "no key configured" is worth seeing;</item>
///         <item>standalone ≥40-char hex runs, as a key-shaped fallback.</item>
///     </list>
///     <para>
///         <b>Why one type.</b> Two separate redactors — <c>DiagnosticsRedactor</c>
///         and <c>CrashReportRedactor</c> — drift apart in BOTH
///         directions: the diagnostics one knew <c>github_pat_…</c> and
///         <c>AKIA…</c> and the crash one did not; the crash one had the user
///         name / home-path pass and the diagnostics one had none, so the
///         "redacted bundle" shipped <c>C:\Users\&lt;name&gt;</c> in
///         <c>system-info.txt</c>, in <c>appconfig.json</c>'s recent-project
///         paths and in 10 MB of log tail. Worse, the diagnostics key/value
///         regex was ANCHORED at the start of the line, so in a Serilog line
///         (<c>&lt;timestamp&gt; [ERRO] … refreshToken=abc</c>)
///         the key group bound to the timestamp and the pass that exists
///         to catch embedded secrets caught none of them.
///         <c>SharedRedactorLintTests</c> fails the gate if a second one
///         reappears.
///     </para>
///     <para>
///         Deliberately conservative: over-redacting a harmless value beats
///         leaking a real one. The pass is idempotent — the character classes
///         exclude <c>&lt;</c> and <c>&gt;</c> so an already-inserted
///         placeholder is never re-consumed.
///     </para>
///     Credential FILES never enter a bundle in the first place
///     (<see cref="Infrastructure.Backup.ConfigBackupRules.IsCredential" />);
///     this is the content-level safety net for the files that do.
/// </summary>
public static class SensitiveTextRedactor
{
    public const string Redacted = "<redacted>";
    public const string UserPlaceholder = "<user>";

    /// <summary>
    ///     Key fragments (compared case- and separator-insensitively, by
    ///     CONTAINS) that mark a key/value pair's value as a secret.
    ///     <para>
    ///         Contains rather than a word alternation because
    ///         <c>AppConfig</c> really declares <c>Tone3000ClientId</c> (and older
    ///         configs still carry <c>GitHubOAuthClientId</c> and
    ///         <c>SpotifyClientId</c>) — a
    ///         `\b`-style word match cannot see the <c>ClientId</c> inside
    ///         <c>Tone3000ClientId</c> (the preceding char is a digit), and the
    ///         bundle carries <c>appconfig.json</c> verbatim.
    ///     </para>
    ///     <para>
    ///         "anonkey" stays although v0.60 removed <c>AppConfig.OnlineAnonKey</c>:
    ///         a bundle is collected from a config file an OLDER build wrote, and
    ///         that file still carries the key until the next save rewrites it.
    ///     </para>
    /// </summary>
    private static readonly string[] SensitiveKeyFragments =
    [
        "token", "secret", "password", "passwd", "pwd", "apikey", "credential",
        "privatekey", "clientsecret", "connectionstring", "sessionkey",
        "refreshtoken", "accesstoken", "accesskey", "authtoken", "authorization",
        "clientid", "anonkey"
    ];

    // NOTE: char classes deliberately exclude '<' and '>' so an already-inserted
    // placeholder is never re-consumed (keeps the pass idempotent).
    private static readonly Regex WindowsUserPath = new(
        @"(?<p>[A-Za-z]:[\\/]+Users[\\/]+)[^\\/\r\n""'<>|]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex UnixHomePath = new(
        @"(?<p>/(?:home|Users)/)[^/\r\n""'<>|]+",
        RegexOptions.Compiled);

    private static readonly Regex Jwt = new(
        @"eyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}",
        RegexOptions.Compiled);

    // GitHub PAT/OAuth (ghp_/gho_/ghu_/ghs_/ghr_), fine-grained GitHub PAT
    // (github_pat_…), Slack (xoxb-…), AWS access key id (AKIA…), OpenAI (sk-…).
    private static readonly Regex TokenPrefix = new(
        @"\b(?:github_pat_[A-Za-z0-9_]{20,}|gh[pousr]_[A-Za-z0-9]{20,}|xox[baprs]-[A-Za-z0-9-]{10,}|AKIA[0-9A-Z]{16}|sk-[A-Za-z0-9]{20,})\b",
        RegexOptions.Compiled);

    private static readonly Regex Bearer = new(
        @"(?<k>[Bb]earer\s+)[A-Za-z0-9._\-]+",
        RegexOptions.Compiled);

    /// <summary>
    ///     <c>key=value</c> / <c>"key": "value"</c>, matched ANYWHERE in a line
    ///     rather than only at its start, so a secret embedded in a timestamped
    ///     log line is caught. The key must start with a letter and be at least
    ///     two characters, which keeps a Serilog timestamp (<c>12:00</c>) and a
    ///     Windows drive letter (<c>C:\Users\…</c>) from reading as keys.
    ///     Sensitivity is decided by <see cref="IsSensitiveKey" />, not by the
    ///     pattern.
    /// </summary>
    private static readonly Regex KeyValuePair = new(
        """(?<![A-Za-z0-9_])(?<k>[A-Za-z][A-Za-z0-9_.\-]{1,63})(?<sep>["']?\s*[:=]\s*["']?)(?<v>[^\s"'`,;}\]\)&<>]+)""",
        RegexOptions.Compiled);

    /// <summary>
    /// The Rocksmith keys: their names are not secret-shaped, and a value
    ///     may hold spaces, which would end <see cref="KeyValuePair" />'s value
    ///     early. The value runs to the closing quote or the end of the line; it
    ///     may not start with whitespace (so an empty <c>""</c> stays visible) or
    ///     with <c>&lt;</c> (so an inserted placeholder is left alone).
    /// </summary>
    private static readonly Regex RocksmithKey = new(
        """(?<![A-Za-z0-9_\-])(?<k>PsarcToc|SngPc|SngMac|psarc-toc|sng-pc|sng-mac)(?<sep>["']?\s*[:=][ \t]*["']?)(?<v>[^"'\s<][^"'\r\n]*)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex LongHexRun = new(
        // Standalone long hex runs that look like keys (>= 40 chars), e.g. a hex
        // API key. Kept conservative so ordinary hashes in log lines are the main
        // casualty (acceptable — they carry no user data anyway).
        @"\b[A-Fa-f0-9]{40,}\b",
        RegexOptions.Compiled);

    /// <summary>
    ///     Returns <paramref name="text" /> with identifying and credential-ish
    ///     substrings replaced by placeholders. <paramref name="userName" />, when
    ///     supplied, is the OS account name to scrub explicitly (case-insensitive).
    ///     Never throws; returns an empty string for null/empty input.
    /// </summary>
    public static string Redact(string? text, string? userName = null)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var s = text;

        // 1. The literal user name, anywhere (paths, "Welcome Alex", etc.).
        //    Guard against 1-char names which would shred unrelated text.
        if (!string.IsNullOrWhiteSpace(userName) && userName.Trim().Length >= 2)
            s = Regex.Replace(s, Regex.Escape(userName.Trim()), UserPlaceholder, RegexOptions.IgnoreCase);

        // 2. Home-directory path segments (covers other machines' paths in logs).
        s = WindowsUserPath.Replace(s, "${p}" + UserPlaceholder);
        s = UnixHomePath.Replace(s, "${p}" + UserPlaceholder);

        // 3. Structured secrets. Order matters: token-shaped values first, so an
        //    "Authorization: Bearer <jwt>" is fully scrubbed even though the
        //    key=value value-terminator stops at the first space.
        s = Jwt.Replace(s, "<redacted-jwt>");
        s = TokenPrefix.Replace(s, "<redacted-token>");
        s = Bearer.Replace(s, "${k}" + Redacted);
        s = RocksmithKey.Replace(s, "${k}${sep}" + Redacted);
        s = KeyValuePair.Replace(s, RedactWhenKeyIsSensitive);
        s = LongHexRun.Replace(s, "<redacted-hex>");

        return s;
    }

    private static string RedactWhenKeyIsSensitive(Match match)
    {
        return IsSensitiveKey(match.Groups["k"].Value)
            ? match.Groups["k"].Value + match.Groups["sep"].Value + Redacted
            : match.Value;
    }

    private static bool IsSensitiveKey(string key)
    {
        Span<char> buffer = stackalloc char[key.Length];
        var n = 0;
        foreach (var c in key)
        {
            if (char.IsLetterOrDigit(c))
                buffer[n++] = char.ToLowerInvariant(c);
        }

        var normalized = new string(buffer[..n]);
        foreach (var fragment in SensitiveKeyFragments)
        {
            if (normalized.Contains(fragment, StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}
