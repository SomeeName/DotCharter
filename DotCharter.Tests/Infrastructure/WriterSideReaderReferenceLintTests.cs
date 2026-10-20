using System.Text.RegularExpressions;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     #885 (PSARC split, epic #882): the package WRITER stays public and the READER
///     (PSARC/SNG reading and decryption) moves to an optional plugin, so the writer-side
///     folders — <c>DotCharter.Services/Export/</c> and
///     <c>DotCharter.Services/Infrastructure/Psarc/Write/</c> — must name no reader type and
///     import no reader namespace, not even in a doc comment (a <c>cref</c> to a type that is
///     gone breaks the build). The writer encrypts with <c>SngPacker</c> and
///     <c>PsarcTocEncryptor</c>; decrypting is the reader's. Comments count on purpose.
/// </summary>
public sealed class WriterSideReaderReferenceLintTests
{
    /// <summary>The reader's types and namespaces. A new reader type joins this list.</summary>
    private static readonly string[] ReaderNames =
    [
        "PsarcArchive", "PsarcTocDecryptor", "RocksmithSngReader", "SngDecryptor", "UnpackPackedSng",
        "SngToDomainMapper", "Rs7ChordColumns", "RsExtensionBlockReader", "Rs7ChordSidecarReader",
        "PsarcLibrarySource", "PsarcManifestParser", "PsarcToneReader"
    ];

    private static readonly Regex ReaderReference = new(
        @"\b(?:" + string.Join("|", ReaderNames) + @")\b|\bDotCharter\.Services\.Parsing\.Sng\b|\bParsing\.Sng\.",
        RegexOptions.Compiled);

    private static readonly string[] WriterFolders =
        [Path.Combine("DotCharter.Services", "Export"), Path.Combine("DotCharter.Services", "Infrastructure", "Psarc", "Write")];

    internal static IEnumerable<string> Offenders(string relativePath, string source) =>
        source.Split('\n')
            .Select((line, i) => (line, i))
            .Where(l => ReaderReference.IsMatch(l.line))
            .Select(l => $"{relativePath}:{l.i + 1}: {ReaderReference.Match(l.line).Value}");

    [Fact]
    public void Writer_side_folders_reference_no_reader_type()
    {
        var root = TestPaths.RepoRoot();
        var offenders = new List<string>();
        var scanned = 0;
        foreach (var folder in WriterFolders)
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, folder), "*.cs", SearchOption.AllDirectories))
            {
                scanned++;
                offenders.AddRange(Offenders(Path.GetRelativePath(root, file), File.ReadAllText(file)));
            }
        }

        Assert.True(scanned > 50, $"only {scanned} writer-side files scanned — the folders moved?");
        Assert.True(offenders.Count == 0, LintReport.Of(
            "A writer-side file (Export/, Infrastructure/Psarc/Write/) references the package reader",
            offenders,
            "Use the writer halves (SngPacker, PsarcTocEncryptor, RsExtensionBlockWriter, Rs7ChordSidecar, Export/Sng/Model); " +
            "a doc comment says \"the package reader\" instead of naming its type.",
            "docs/agents/services/parsing-psarc.md"));
    }

    /// <summary>Non-vacuity: each shape a leak takes is caught; prose about the reader is not.</summary>
    [Theory]
    [InlineData("using var archive = PsarcArchive.Open(path);")]
    [InlineData("/// <see cref=\"RocksmithSngReader\"/>")]
    [InlineData("using DotCharter.Services.Parsing.Sng;")]
    [InlineData("var sng = Parsing.Sng.RocksmithSngReader.ReadPackedPc(stream);")]
    [InlineData("SngDecryptor.Decrypt(input, output, key, iv);")]
    [InlineData("var block = RsExtensionBlockReader.TryRead(rest, sng, out _);")]
    public void The_pattern_catches_a_reader_reference(string line) =>
        Assert.Single(Offenders("x.cs", line));

    [Theory]
    [InlineData("/// the inverse of the package reader's SNG parser.")]
    [InlineData("var archive = PsarcWriter.Write(output, entries);")]
    [InlineData("using DotCharter.Services.Export.Sng.Model;")]
    [InlineData("var packed = SngPacker.Pack(body, key, iv);")]
    public void The_pattern_leaves_writer_code_alone(string line) =>
        Assert.Empty(Offenders("x.cs", line));
}
