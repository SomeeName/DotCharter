using DotCharter.Services.Session;
using System.Text.RegularExpressions;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     A caught exception is logged WITH the exception: <c>Warning(cat, "what failed", ex)</c>, not
///     <c>Warning(cat, $"what failed: {ex.Message}")</c>, which drops the type, the stack and the inner
///     exception and leaves a user log undiagnosable. A NEW call of the second form fails here. The allow-list (empty now;
///     an entry needs the exact count and a reason, and may only shrink):
///     every site passes the exception.
/// </summary>
public sealed class ExceptionLoggingLintTests
{
    /// <summary>File (repo-relative, '/') to the number of message-only exception log calls it still has, each with a reason. Lower it, never raise it.</summary>
    private static readonly Dictionary<string, int> Allowed = new(StringComparer.Ordinal);

    private static readonly Regex MessageOnlyPattern = new(
        @"(?:Warning|Error)\(\s*AppLogCategory\.\w+,\s*\$""(?:[^""{]|\{[^}]*\})*?\{\w+\.Message\}(?:[^""{]|\{[^}]*\})*""\s*\)",
        RegexOptions.CultureInvariant);

    [Fact]
    public void Caught_exceptions_are_logged_with_the_exception_not_only_its_message()
    {
        var root = TestPaths.RepoRoot();
        var offenders = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dir in Directory.EnumerateDirectories(root, "DotCharter.*").Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(dir);
            if (name == "DotCharter.Tests" || !File.Exists(Path.Combine(dir, name + ".csproj"))) continue;
            foreach (var file in UiSourceTree.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (rel.Contains("/obj/", StringComparison.Ordinal) || rel.Contains("/bin/", StringComparison.Ordinal)) continue;
                var count = Count(UiSourceTree.ReadAllText(file));
                if (count == 0) continue;
                seen.Add(rel);
                var allowed = Allowed.GetValueOrDefault(rel);
                if (count > allowed)
                    offenders.Add($"{rel}: {count} log call(s) pass only {{ex.Message}}, allowed {allowed} - pass the exception: Warning(cat, \"what failed\", ex)");
                else if (count < allowed)
                    offenders.Add($"{rel}: {count} left, allowed {allowed} - lower the allow-list entry to {count} to bank the fix");
            }
        }

        offenders.AddRange(Allowed.Keys.Where(k => !seen.Contains(k))
            .Select(k => $"{k}: allow-listed but has none - delete the entry"));
        Assert.True(offenders.Count == 0, "exception logging:\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void Probe_the_pattern_flags_message_only_calls_and_accepts_the_exception_overload()
    {
        Assert.Equal(1, Count("_logger?.Warning(AppLogCategory.Audio, $\"Stem failed: {ex.Message}\");"));
        Assert.Equal(1, Count("ex => _logger?.Error(AppLogCategory.Audio, $\"Load '{p}' failed: {e.Message}\"))"));
        Assert.Equal(1, Count("ex => _logger?.Warning(AppLogCategory.Audio, $\"Editor {(open ? \"show\" : \"hide\")} failed: {ex.Message}\"))"));
        Assert.Equal(0, Count("_logger?.Warning(AppLogCategory.Audio, $\"Stem failed for '{p}'\", ex);"));
        Assert.Equal(0, Count("_logger?.Error(AppLogCategory.Audio, $\"Stem failed: {ex.Message}\", ex);"));
    }

    [Fact]
    public void A_converted_site_passes_the_exception_to_the_logger()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dc403-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var stale = Path.Combine(dir, "song_1" + ProjectAutoBackup.StagedSuffix);
            using (new FileStream(stale, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                var logger = new FakeAppLogger();
                ProjectAutoBackup.PruneBackups(dir, "song", 3, logger);
                var entry = Assert.Single(logger.Entries, e => e.Level == "WARN");
                Assert.NotNull(entry.Exception);
                Assert.DoesNotContain(entry.Exception!.Message, entry.Message, StringComparison.Ordinal);
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static int Count(string source) => MessageOnlyPattern.Count(source);
}
