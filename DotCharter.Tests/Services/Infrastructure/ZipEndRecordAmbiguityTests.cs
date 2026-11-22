using System.Buffers.Binary;
using System.IO.Compression;
using DotCharter.Services.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Infrastructure;

/// <summary>Small adversarial comments cannot make preflight choose a different end record.</summary>
public sealed class ZipEndRecordAmbiguityTests
{
    [Theory]
    [InlineData("oversized-later")]
    [InlineData("zip64-locator")]
    public void OversizedSelectedEndRecord_IsRefused(string fixture)
    {
        var bytes = Archive(fixture);
        Assert.InRange(bytes.Length, 1, 1024);
        using var stream = new MemoryStream(bytes);
        Assert.Equal(65000, BoundedZipExtractor.TryReadDeclaredEntryCount(stream));
        Assert.Equal(ZipExtractionRefusal.TooManyEntries,
            ZipDirectoryBudget.Check(stream, BoundedZipExtractor.DefaultMaxEntries));
    }

    [Theory]
    [InlineData("small-comment")]
    [InlineData("truncated-edge")]
    public void CommentCandidates_MatchWhatRuntimeActuallyOpens(string fixture)
        => ZipEndRecordSelectionTests.AssertMatchesRuntime(Archive(fixture), 16);

    private static byte[] Archive(string fixture)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        using (var entry = zip.CreateEntry("x").Open()) entry.WriteByte(1);
        var original = stream.ToArray();
        var first = original.Length - 22;
        var comment = new byte[fixture == "truncated-edge" ? 4 : fixture == "zip64-locator" ? 43 : 23];
        var second = fixture == "zip64-locator" ? 20 : 0;
        if (fixture == "zip64-locator")
        {
            BinaryPrimitives.WriteUInt32LittleEndian(comment, 0x07064b50);
            BinaryPrimitives.WriteUInt32LittleEndian(comment.AsSpan(16), 1);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(comment.AsSpan(second), 0x06054b50);
        if (fixture != "truncated-edge")
        {
            var count = fixture == "small-comment" ? (ushort)1 : (ushort)65000;
            BinaryPrimitives.WriteUInt16LittleEndian(comment.AsSpan(second + 8), count);
            BinaryPrimitives.WriteUInt16LittleEndian(comment.AsSpan(second + 10), count);
            // Zero declared comment length plus a trailing byte reproduces the library mismatch.
        }
        BinaryPrimitives.WriteUInt16LittleEndian(original.AsSpan(first + 20), (ushort)comment.Length);
        return [.. original, .. comment];
    }
}
