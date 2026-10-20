using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Parsing.Psarc;

public sealed partial class PsarcWriterTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("dotcharter-psarc-").FullName;

    public void Dispose() => BestEffortDelete.Directory(_tempDir);

    private string TempPsarcPath() => Path.Combine(_tempDir, $"{Guid.NewGuid():N}.psarc");

    private static byte[] DeterministicBytes(int length, int seed)
    {
        // Deterministic but non-trivial content: repeats poorly, compresses some.
        var random = new Random(seed);
        var data = new byte[length];
        random.NextBytes(data);
        return data;
    }

    private static byte[] CompressibleBytes(int length)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++) data[i] = (byte)(i % 13);
        return data;
    }

    private static string? FindEofPsarcFixture()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "TestData", "EOF");
            if (Directory.Exists(candidate))
            {
                return Directory
                    .EnumerateFiles(candidate, "*_m.psarc", SearchOption.AllDirectories)
                    .OrderBy(p => new FileInfo(p).Length)
                    .FirstOrDefault();
            }

            dir = dir.Parent;
        }

        return null;
    }
}
