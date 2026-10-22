using System.Text;
using System.Text.RegularExpressions;
using DotCharter.Services.Export.Audio;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Infrastructure;

/// <summary>
///     Every child process this app reads text from speaks UTF-8 (ffprobe emits
///     UTF-8 JSON, yt-dlp UTF-8 titles). .NET does NOT
///     assume that: with no <c>StandardOutputEncoding</c> a redirected pipe is
///     decoded with the CONSOLE code page, which on Windows is a legacy
///     single-byte page — so every non-ASCII byte came back as mojibake.
///     <para>
///     Owner report 2026-08-21: a Japanese album / artist / title imported
///     mangled (<c>å…«æœˆ</c>-shaped, not tofu and not question marks). All
///     SEVEN redirect sites in the service layer were missing the encoding. It
///     reads as intermittent because a GUI process without a console resolves a
///     different code page than one with, so the same build can import the same
///     file correctly on one launch and mangled on the next.
///     </para>
/// </summary>
public sealed class ChildProcessEncodingTests
{
    /// <summary>A Japanese artist and title — the reported case.</summary>
    private const string NonAscii = "ヨルシカ 八月、某、月明かり";

    /// <summary>
    ///     A floor, NOT the guard — and knowing which is which matters here.
    ///     Whether the un-fixed code fails depends on the ambient console code
    ///     page, so on a box whose console is already UTF-8 (this one, measured
    ///     2026-08-21) it passes with the encoding removed and proves nothing.
    ///     That environment-dependence IS the bug's signature: the same build
    ///     imports the same file correctly on one launch and mangled on the next.
    ///     <see cref="EveryRedirectedChildPipe_DeclaresItsEncoding" /> is the
    ///     deterministic guard, and it was verified to fail with the fix removed.
    /// </summary>
    [Fact]
    public void MediaProcess_ReadsAChildsUtf8OutputIntact()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "uses cmd /c type to emit exact bytes");

        // `type` copies the file's BYTES to stdout, so this measures the decode
        // and nothing else. Written without a BOM: a BOM would let the reader
        // detect UTF-8 on its own and the test would pass either way.
        var path = Path.Combine(Path.GetTempPath(), $"dotcharter-enc-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, NonAscii, new UTF8Encoding(false));
        try
        {
            var result = MediaProcess.Run("cmd", ["/c", "type", path], TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(NonAscii, result.StandardOutput.Trim());
        }
        finally
        {
            BestEffortDelete.File(path);
        }
    }

    /// <summary>
    ///     The mechanical half: a redirected pipe with no encoding is the bug, so
    ///     no site may leave one unset. Counted rather than merely "the file
    ///     mentions it once", because a file that starts TWO child processes
    ///     (the deleted Python demucs wrapper did) can carry the fix on only one.
    ///     <para>
    ///     <c>StandardInputEncoding</c> is deliberately NOT required: a
    ///     redirected stdin is a byte pipe as often as a text one. The run-video
    ///     exporter wrote RAW FRAME BYTES to <c>StandardInput.BaseStream</c>
    ///     until 0.59, and giving that an encoding would have corrupted the
    ///     video. Nothing under <c>Services/</c> redirects stdin today.
    ///     </para>
    /// </summary>
    [Fact]
    public void EveryRedirectedChildPipe_DeclaresItsEncoding()
    {
        var root = Path.Combine(TestPaths.RepoRoot(), "DotCharter.Services");
        var offenders = new List<string>();

        foreach (var file in new[] { root, Path.Combine(TestPaths.RepoRoot(), "DotCharter.Tone") }
                     .SelectMany(r => Directory.EnumerateFiles(r, "*.cs", SearchOption.AllDirectories)))
        {
            var text = File.ReadAllText(file);
            var name = Path.GetRelativePath(root, file);

            Check("RedirectStandardOutput", "StandardOutputEncoding");
            Check("RedirectStandardError", "StandardErrorEncoding");

            void Check(string redirect, string encoding)
            {
                var redirects = Regex.Matches(text, redirect + @"\s*=\s*true").Count;
                var encodings = Regex.Matches(text, encoding + @"\s*=\s*Encoding").Count;

                if (encodings < redirects)
                    offenders.Add($"{name}: {redirects}x {redirect}, only {encodings}x {encoding}");

                // …and the INVERSE, which is worse than the bug it guards:
                // setting an encoding for a pipe that is not redirected makes
                // Process.Start THROW, so the child never starts at all. The
                // first cut of this fix did exactly that to the VST sandbox.
                if (encodings > redirects)
                {
                    offenders.Add($"{name}: {encodings}x {encoding} but only {redirects}x {redirect} " +
                                  "— Process.Start throws when an encoding is set for a pipe that is not redirected");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "a redirected child pipe with no encoding is decoded with the console code page, " +
            "which mangles every non-ASCII byte:\n  " + string.Join("\n  ", offenders));
    }
}
