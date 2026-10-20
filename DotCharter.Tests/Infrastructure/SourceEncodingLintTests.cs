using System.Text;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     Every source, markup, resource, doc, JSON and PowerShell file under the
///     <c>DotCharter.*</c> projects and <c>docs/</c> is valid UTF-8. A Windows-1252
///     dash or quote pasted into a comment builds clean and then shows as mojibake
///     in every diff, review and generated doc; the failure names the file and line.
/// </summary>
public sealed class SourceEncodingLintTests
{
    private static readonly string[] CheckedExtensions = [".cs", ".axaml", ".resx", ".md", ".json", ".ps1"];

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    [Fact]
    public void EverySourceFile_IsValidUtf8()
    {
        var root = TestPaths.RepoRoot();
        var failures = new List<string>();
        var scanned = 0;

        foreach (var file in SourceFiles(root))
        {
            scanned++;
            var bytes = File.ReadAllBytes(file);
            try
            {
                StrictUtf8.GetString(bytes);
            }
            catch (DecoderFallbackException ex)
            {
                var offset = ex.Index;
                var line = 1;
                for (var i = 0; i < offset && i < bytes.Length; i++)
                {
                    if (bytes[i] == (byte)'\n')
                    {
                        line++;
                    }
                }

                failures.Add($"{Path.GetRelativePath(root, file).Replace('\\', '/')}:{line}");
            }
        }

        Assert.True(scanned > 1000, $"only {scanned} files scanned; the scan is broken");
        Assert.True(failures.Count == 0,
            "These files contain bytes that are not UTF-8 (usually a Windows-1252 dash or quote pasted " +
            "into a comment). Re-type the character or save the file as UTF-8:\n  " +
            string.Join("\n  ", failures));
    }

    private static IEnumerable<string> SourceFiles(string root)
    {
        foreach (var project in Directory.EnumerateDirectories(root, "DotCharter.*").Append(Path.Combine(root, "docs")))
        {
            foreach (var file in Directory.EnumerateFiles(project, "*.*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (relative.Contains("/bin/", StringComparison.Ordinal) || relative.Contains("/obj/", StringComparison.Ordinal))
                {
                    continue;
                }

                if (CheckedExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                {
                    yield return file;
                }
            }
        }
    }
}
