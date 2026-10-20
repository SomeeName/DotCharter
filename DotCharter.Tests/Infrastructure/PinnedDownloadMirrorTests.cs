using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     <c>tools/release/PinnedDownload.psm1</c>'s mirror loop (#449), run as itself
///     under <c>pwsh</c> with a FAKE downloader — no network. A release once stopped
///     because SourceForge served an HTML page for the pinned LAME tarball; every
///     pinned source now lists mirrors held to the same SHA-256, and these cases
///     pin that a wrong-bytes mirror is skipped, never cached, and that no mirror
///     can weaken the pin. A missing <c>pwsh</c> is a skip, never a failure.
/// </summary>
public sealed partial class PinnedDownloadMirrorTests : IDisposable
{
    private readonly string _scratch = Path.Combine(
        Path.GetTempPath(), "dotcharter-pinned-download-" + Guid.NewGuid().ToString("N"));

    public PinnedDownloadMirrorTests() => Directory.CreateDirectory(_scratch);

    public void Dispose() => BestEffortDelete.Directory(_scratch);

    [Fact]
    public void A_mirror_serving_wrong_bytes_is_skipped_and_never_reaches_the_cache()
    {
        var result = Run("""{ "https://a.invalid/x": "<html>mirror page</html>", "https://b.invalid/x": "the pinned bytes" }""",
            ["https://a.invalid/x", "https://b.invalid/x"]);

        Assert.Equal("https://b.invalid/x", result.GetProperty("served").GetString());
        Assert.Equal("the pinned bytes", result.GetProperty("content").GetString());
        Assert.Equal(["https://a.invalid/x", "https://b.invalid/x"], Strings(result, "calls"));
        // While the second mirror downloads, the first one's bytes are already gone.
        Assert.Equal([false, false], result.GetProperty("destExistedAtCall").EnumerateArray().Select(e => e.GetBoolean()));
        Assert.Equal(["file.tar.gz"], Strings(result, "files"));
    }

    [Fact]
    public void A_mirror_that_fails_outright_is_skipped()
    {
        var result = Run("""{ "https://a.invalid/x": "!404", "https://b.invalid/x": "the pinned bytes" }""",
            ["https://a.invalid/x", "https://b.invalid/x"]);

        Assert.Equal("https://b.invalid/x", result.GetProperty("served").GetString());
        Assert.Equal(["file.tar.gz"], Strings(result, "files"));
    }

    [Fact]
    public void When_no_mirror_serves_the_pinned_bytes_it_throws_and_caches_nothing()
    {
        var result = Run("""{ "https://a.invalid/x": "<html>one</html>", "https://b.invalid/x": "!404" }""",
            ["https://a.invalid/x", "https://b.invalid/x"]);

        var error = result.GetProperty("error").GetString()!;
        Assert.Contains("No mirror served file.tar.gz", error, StringComparison.Ordinal);
        Assert.Contains("https://a.invalid/x : SHA-256", error, StringComparison.Ordinal);
        Assert.Contains("https://b.invalid/x : 404", error, StringComparison.Ordinal);
        Assert.Empty(Strings(result, "files"));
    }

    [Fact]
    public void A_cached_file_matching_the_pin_downloads_nothing()
    {
        var result = Run("""{ "https://a.invalid/x": "<html>never asked</html>" }""", ["https://a.invalid/x"],
            "the pinned bytes");

        Assert.Equal("cache", result.GetProperty("served").GetString());
        Assert.Empty(Strings(result, "calls"));
        Assert.Equal("the pinned bytes", result.GetProperty("content").GetString());
    }

    [Fact]
    public void A_cached_file_failing_the_pin_is_deleted_even_when_no_mirror_replaces_it()
    {
        var result = Run("""{ "https://a.invalid/x": "<html>still wrong</html>" }""", ["https://a.invalid/x"],
            "stale wrong bytes");

        Assert.NotNull(result.GetProperty("error").GetString());
        Assert.Empty(Strings(result, "files"));
    }

    /// <summary>
    ///     The build script downloads ONLY through the mirror loop, and LAME tries
    ///     Debian's byte-identical tarball before SourceForge.
    /// </summary>
    [Fact]
    public void The_ffmpeg_build_downloads_every_pin_through_the_mirror_loop()
    {
        var build = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "tools", "release", "Build-MinimalFfmpeg.ps1"));

        Assert.DoesNotContain("Invoke-WebRequest", build, StringComparison.Ordinal);
        Assert.Contains("Save-PinnedFile -Urls $Msys2BaseUrls -Dest $sfx -Sha256 $Msys2BaseSha256", build, StringComparison.Ordinal);
        Assert.Contains("Save-PinnedFile -Urls $s.Urls -Dest (Join-Path $dl $s.File) -Sha256 $s.Sha256", build, StringComparison.Ordinal);

        var lame = LameUrls().Match(build);
        Assert.True(lame.Success, "Build-MinimalFfmpeg.ps1 has no lame-3.100 source entry with a Urls list.");
        Assert.Equal(
            ["https://deb.debian.org/debian/pool/main/l/lame/lame_3.100.orig.tar.gz",
             "https://downloads.sourceforge.net/project/lame/lame/3.100/lame-3.100.tar.gz"],
            Regex.Matches(lame.Groups["urls"].Value, "'(https://[^']+)'").Select(m => m.Groups[1].Value));
    }

    private static IEnumerable<string> Strings(JsonElement result, string name) =>
        result.GetProperty(name).EnumerateArray().Select(e => e.GetString()!);

    /// <summary>
    ///     Calls <c>Save-PinnedFile</c> on <paramref name="urls" />, pinned to the SHA-256
    ///     of "the pinned bytes". <paramref name="servedJson" /> maps each URL to the text
    ///     the fake downloader writes, or <c>!404</c> to throw. Returns what happened.
    /// </summary>
    private JsonElement Run(string servedJson, string[] urls, string? cached = null)
    {
        var cache = Path.Combine(_scratch, "cache");
        Directory.CreateDirectory(cache);
        var dest = Path.Combine(cache, "file.tar.gz");
        if (cached is not null) File.WriteAllText(dest, cached);
        var resultFile = Path.Combine(_scratch, "result.json");

        var module = Path.Combine(TestPaths.RepoRoot(), "tools", "release", "PinnedDownload.psm1");
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            Import-Module '{{Escape(module)}}' -Force
            $served = '{{Escape(servedJson)}}' | ConvertFrom-Json -AsHashtable
            $dest = '{{Escape(dest)}}'
            $calls = [System.Collections.Generic.List[string]]::new()
            $destExisted = [System.Collections.Generic.List[bool]]::new()
            $fake = {
                param([string]$Url, [string]$OutFile)
                $calls.Add($Url)
                $destExisted.Add((Test-Path -LiteralPath $dest))
                if ($served[$Url] -eq '!404') { throw '404 Not Found' }
                [System.IO.File]::WriteAllText($OutFile, $served[$Url])
            }
            $pin = (Get-FileHash -Algorithm SHA256 -InputStream ([System.IO.MemoryStream]::new([System.Text.Encoding]::UTF8.GetBytes('the pinned bytes')))).Hash
            $result = [ordered]@{ served = $null; error = $null }
            try { $result.served = Save-PinnedFile -Urls @({{string.Join(", ", urls.Select(u => $"'{Escape(u)}'"))}}) -Dest $dest -Sha256 $pin -Download $fake 6>$null 3>$null }
            catch { $result.error = $_.Exception.Message }
            $result.calls = @($calls)
            $result.destExistedAtCall = @($destExisted)
            $result.files = @(Get-ChildItem -LiteralPath (Split-Path $dest) -Force | ForEach-Object Name)
            $result.content = if (Test-Path -LiteralPath $dest) { [System.IO.File]::ReadAllText($dest) } else { $null }
            $result | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath '{{Escape(resultFile)}}' -Encoding utf8
            """;
        var file = Path.Combine(_scratch, "case-" + Guid.NewGuid().ToString("N") + ".ps1");
        File.WriteAllText(file, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var info = new ProcessStartInfo("pwsh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-File", file })
            info.ArgumentList.Add(argument);

        Process? process;
        try
        {
            process = Process.Start(info);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Assert.Skip("pwsh (PowerShell 7+) is not on PATH, so PinnedDownload.psm1 cannot be executed here.");
            return default;
        }

        Assert.NotNull(process);
        var stdoutTask = process!.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = process.StandardError.ReadToEnd();
        // A deadlock detector for a hung child, never a budget.
        Assert.True(process.WaitForExit(120_000), "pwsh did not finish within 120 s.");
        var stdout = stdoutTask.GetAwaiter().GetResult();
        Assert.True(process.ExitCode == 0, $"pwsh exited {process.ExitCode}.{Environment.NewLine}{stderr}{Environment.NewLine}{stdout}");

        using var document = JsonDocument.Parse(File.ReadAllText(resultFile));
        return document.RootElement.Clone();
    }

    private static string Escape(string text) => text.Replace("'", "''", StringComparison.Ordinal);

    [GeneratedRegex(@"File = 'lame-3\.100\.tar\.gz';[^\n]*Urls = @\((?<urls>[^)]*)\)")]
    private static partial Regex LameUrls();
}
