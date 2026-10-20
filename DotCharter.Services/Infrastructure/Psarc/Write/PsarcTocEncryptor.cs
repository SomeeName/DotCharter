using System.Security.Cryptography;

namespace DotCharter.Services.Infrastructure.Psarc.Write;

/// <summary>
/// AES-CFB encryption of the PSARC table of contents for <see cref="PsarcWriter"/>: the
/// WRITE half only; nothing here undoes the encryption. The key is the
/// user's (<see cref="RocksmithKeys"/>). Reference: Rocksmith2014.NET PSARC
/// Cryptography.fs.
/// </summary>
internal static class PsarcTocEncryptor
{
    /// <summary>Encrypts the plain TOC under the user's PSARC key.</summary>
    public static byte[] Encrypt(ReadOnlySpan<byte> plainToc) =>
        Encrypt(plainToc, RocksmithKeys.Current?.TocKey
                          ?? throw new InvalidOperationException("No PSARC key: write a plain package (encryptToc: false)."));

    /// <summary>
    /// Encrypts the plain TOC under <paramref name="key"/>. Returns exactly
    /// <paramref name="plainToc"/>.Length bytes — the zero padding the CFB transform appends
    /// is discarded, matching the reference implementation (Rocksmith2014.NET PSARC.fs
    /// `updateToc`). A pure function of its arguments.
    /// </summary>
    public static byte[] Encrypt(ReadOnlySpan<byte> plainToc, byte[] key)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.CFB;
        aes.BlockSize = 128;
        aes.FeedbackSize = 128;
        aes.Padding = PaddingMode.Zeros;
        var encrypted = aes.EncryptCfb(plainToc, new byte[16], PaddingMode.Zeros, 128);
        return encrypted.Length == plainToc.Length ? encrypted : encrypted[..plainToc.Length];
    }
}
