using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using DotCharter.Services.Export.Sng.Model;

namespace DotCharter.Services.Export.Sng;

/// <summary>
/// The SNG container around a plain body: the WRITE half only, zlib then
///     AES-CTR encryption. Nothing here undoes a pack.
///     Reference: Rocksmith2014.NET SNG.fs <c>pack</c> and Cryptography.fs.
/// </summary>
internal static class SngPacker
{
    private const int Magic = 0x4A;
    private const int TrailerBytes = 56;

    /// <summary>
    ///     The packed (encrypted) SNG: magic 0x4A, flags 3, the 16-byte counter IV, then
    ///     the plain length prefix + zlib payload under AES-CTR with <paramref name="key" />,
    ///     and 56 trailing zero bytes the game ignores. A pure function of its arguments.
    /// </summary>
    public static byte[] Pack(byte[] plain, byte[] key, byte[] iv)
    {
        if (iv.Length != 16)
            throw new ArgumentException("SNG IV must be 16 bytes.", nameof(iv));

        using var payload = new MemoryStream();
        using (var payloadWriter = new BinaryWriter(payload, Encoding.UTF8, true))
            payloadWriter.Write(plain.Length);
        using (var zlib = new ZLibStream(payload, CompressionLevel.SmallestSize, true))
            zlib.Write(plain);

        using var output = new MemoryStream();
        using (var header = new BinaryWriter(output, Encoding.UTF8, true))
        {
            header.Write(Magic);
            header.Write(SngFlags.Compressed | SngFlags.Encrypted);
            header.Write(iv);
        }

        output.Write(EncryptCtr(payload.ToArray(), key, iv));
        output.Write(new byte[TrailerBytes]);
        return output.ToArray();
    }

    /// <summary>
    /// The PLAIN SNG the CDLC enabler's plain-PSARC patch loads: magic 0x4A, flags
    ///     <see cref="SngFlags.Compressed" /> alone, a zero 16-byte IV slot, the same length
    ///     prefix + zlib payload unencrypted, and 56 zero bytes. No key is involved.
    /// </summary>
    public static byte[] PackPlain(byte[] plain)
    {
        using var output = new MemoryStream();
        using (var header = new BinaryWriter(output, Encoding.UTF8, true))
        {
            header.Write(Magic);
            header.Write(SngFlags.Compressed);
            header.Write(new byte[16]);
            header.Write(plain.Length);
        }

        using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, true))
            zlib.Write(plain);
        output.Write(new byte[TrailerBytes]);
        return output.ToArray();
    }

    /// <summary>AES-CTR over <paramref name="data" />: a big-endian 128-bit counter starting
    /// at the IV, each block XORed with the AES-ECB encryption of the counter.</summary>
    private static byte[] EncryptCtr(byte[] data, byte[] key, byte[] iv)
    {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        using var transform = aes.CreateEncryptor(key, null);

        var counter = (byte[])iv.Clone();
        var keyStream = new byte[16];
        var result = new byte[data.Length];
        for (var at = 0; at < data.Length; at += 16)
        {
            _ = transform.TransformBlock(counter, 0, 16, keyStream, 0);
            IncrementCounter(counter);
            var n = Math.Min(16, data.Length - at);
            for (var i = 0; i < n; i++) result[at + i] = (byte)(data[at + i] ^ keyStream[i]);
        }

        return result;
    }

    private static void IncrementCounter(byte[] counter)
    {
        for (var i = counter.Length - 1; i >= 0; i--)
        {
            counter[i]++;
            if (counter[i] != 0) return;
        }
    }
}
