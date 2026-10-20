using DotCharter.Services.Parsing.Eof;

namespace DotCharter.Tests.Services.Parsing.Eof;

/// <summary>
///     Importing one <c>.eof</c> decoded its LZSS container TWICE inside a single
///     user gesture — once for the import dialog's track preview
///     (<c>GetAvailableTracks</c>) and once for the import itself
///     (<c>ImportTracksWithMetadata</c>). The decoder is a byte-at-a-time
///     sliding-window loop over a payload that expands ~8.5×.
///     <para>
///         The cache is keyed by (path, length, last-write ticks) precisely so it
///         cannot serve a stale chart: a file rewritten under us re-decodes. Both
///         halves are pinned here — the saving and the invalidation — because a
///         cache that only proves the saving is the dangerous half.
///     </para>
/// </summary>
public sealed class EofDecodedFileCacheTests
{
    [Fact]
    public void PreviewThenImport_OfTheSameFile_DecodesOnce()
    {
        var cache = new EofDecodedFileCache();
        var path = EofExporterTests.WriteTempEof(EofExportTestCharts.BuildRepresentativeProject());
        try
        {
            var importer = new EofImporter(cache);

            var tracks = importer.GetAvailableTracks(path);
            var imported = importer.ImportTracksWithMetadata(path, [9]);

            // Both halves really ran…
            Assert.Equal(3, tracks.Count);
            Assert.Single(imported.Arrangements);
            // …on ONE decode.
            Assert.Equal(1, cache.DecodeCount);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AFileRewrittenUnderTheCache_IsDecodedAgain()
    {
        var cache = new EofDecodedFileCache();
        var path = EofExporterTests.WriteTempEof(EofExportTestCharts.BuildRepresentativeProject());
        try
        {
            var importer = new EofImporter(cache);
            importer.GetAvailableTracks(path);
            Assert.Equal(1, cache.DecodeCount);

            // Same bytes, same length — only the stamp moves, which is exactly
            // the case a (path, length) key would get wrong.
            File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(5));

            var tracks = importer.GetAvailableTracks(path);

            Assert.Equal(3, tracks.Count);
            Assert.Equal(2, cache.DecodeCount);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TheCacheIsBounded_AndEvictsTheLeastRecentlyUsed()
    {
        // The payloads are multi-megabyte; this exists to make one gesture decode
        // once, not to hold a session's worth of charts.
        var cache = new EofDecodedFileCache();
        var decodes = 0;
        byte[] Decode(byte[] raw)
        {
            decodes++;
            return raw;
        }

        var paths = new List<string>();
        try
        {
            for (var i = 0; i < EofDecodedFileCache.Capacity + 1; i++)
            {
                var path = Path.Combine(Path.GetTempPath(), $"dotcharter-eofcache-{Guid.NewGuid():N}.bin");
                File.WriteAllBytes(path, [(byte)i]);
                paths.Add(path);
                cache.Read(path, Decode);
            }

            Assert.Equal(EofDecodedFileCache.Capacity + 1, decodes);

            // The newest entry is still resident…
            cache.Read(paths[^1], Decode);
            Assert.Equal(EofDecodedFileCache.Capacity + 1, decodes);

            // …and the oldest was evicted.
            cache.Read(paths[0], Decode);
            Assert.Equal(EofDecodedFileCache.Capacity + 2, decodes);
        }
        finally
        {
            foreach (var path in paths) File.Delete(path);
        }
    }

    [Fact]
    public void TheImporter_NeverDecodesAroundTheCache()
    {
        // A second `AllegroLzssDecoder.Decode` call site in the importer would
        // reintroduce the double decode without failing anything above.
        var source = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(),
            "DotCharter.Services", "Parsing", "Eof", "EofImporter.cs"));

        Assert.DoesNotContain("AllegroLzssDecoder", source);
        Assert.DoesNotContain("File.ReadAllBytes", source);
        Assert.Contains("_decodedFiles.Read(filePath)", source);
    }
}
