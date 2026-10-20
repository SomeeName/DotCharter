using System.Buffers.Binary;

namespace DotCharter.Tests.Services.Export;

/// <summary>
///     Validates the SoundBankWriter port against the layout of the F#
///     reference (Rocksmith2014.NET SoundBank.generate): chunk order, BKHD
///     header fields, DIDX/DATA contents and readability through the
///     in-repo SoundBankReader.
/// </summary>
public partial class SoundBankWriterTests
{
    private const int MainDataLength = 51200; // F# reference: non-preview banks embed 51200 bytes
    private const int PreviewDataLength = 72000; // F# reference: preview banks embed 72000 bytes

    private static MemoryStream PatternedStream(int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = (byte)(i % 251);
        return new MemoryStream(bytes);
    }

    private static List<(string Name, byte[] Data)> ReadChunks(byte[] bank)
    {
        var chunks = new List<(string, byte[])>();
        var position = 0;
        while (position < bank.Length)
        {
            var name = System.Text.Encoding.ASCII.GetString(bank, position, 4);
            var length = BinaryPrimitives.ReadInt32LittleEndian(bank.AsSpan(position + 4));
            var data = bank.AsSpan(position + 8, length).ToArray();
            chunks.Add((name, data));
            position += 8 + length;
        }

        Assert.Equal(bank.Length, position); // no trailing garbage
        return chunks;
    }

    private static string? FindEofTestData()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "TestData", "EOF");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        return null;
    }
}
