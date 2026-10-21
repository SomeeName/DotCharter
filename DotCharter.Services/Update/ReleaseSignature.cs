using System.Text;
using Org.BouncyCastle.Math.EC.Rfc8032;

namespace DotCharter.Services.Update;

/// <summary>
///     The publisher signature over a release's <c>SHA256SUMS.txt</c>: an
///     Ed25519 signature, published as the asset <see cref="AssetName" />, made with
///     a private key that never leaves the release machine and checked against the
///     public key pinned below.
///     <para>
///         <b>Why the digests alone were not enough.</b> The updater compares a
///         downloaded zip with <c>SHA256SUMS.txt</c> from the SAME release, so whoever
///         can publish a release (a stolen token, a compromised account) can publish
///         a matching pair. The signature is what the release channel cannot forge:
///         the key is not on GitHub.
///     </para>
///     <para>
///         <b>What is signed</b> is the exact bytes of <c>SHA256SUMS.txt</c>, which
///         carry every asset's name and SHA-256 AND a <see cref="ReleaseLinePrefix" />
///         line naming the release tag — so a signed manifest of an older release
///         cannot be replayed under a newer tag. Every hash the updater trusts (the
///         full zip's, <c>payload-manifest.json</c>'s, and through it every member a
///         partial update fetches) hangs off this one document.
///     </para>
///     <para>
///         <b>It fails CLOSED.</b> A missing, malformed or wrong-key signature, or a
///         manifest that names another release, refuses the update. Every release
///         from 1.1.0 on is signed by <c>tools/release/New-Release.ps1</c> (through
///         <c>tools/release/SignReleaseManifest.cs</c>), which itself refuses to run
///         without the key.
///     </para>
/// </summary>
public static class ReleaseSignature
{
    /// <summary>Asset name the release publishes the signature under.</summary>
    public const string AssetName = "SHA256SUMS.txt.sig";

    /// <summary>The line in <c>SHA256SUMS.txt</c> that binds it to one tag:
    /// <c># release: v1.1.0</c>. A comment, so <see cref="ReleaseChecksums.Parse" />
    /// skips it.</summary>
    public const string ReleaseLinePrefix = "# release: ";

    /// <summary>Largest signature asset read: a base64 Ed25519 signature is 88
    /// characters.</summary>
    public const int MaxSignatureBytes = 1024;

    /// <summary>An Ed25519 public or private key is 32 bytes.</summary>
    public const int KeyBytes = 32;

    /// <summary>An Ed25519 signature is 64 bytes.</summary>
    private const int SignatureBytes = 64;

    /// <summary>
    ///     The release-signing PUBLIC key (Ed25519, 32 bytes, base64). Its private
    ///     half lives outside the repository, on the release machine only
    ///     (<c>tools/release/README.md</c> § Release signing). Changing this value
    ///     means every install older than the change refuses every release signed
    ///     with the new key — rotate it only with a release signed by the OLD key
    ///     that ships the new one.
    /// </summary>
    public const string PinnedPublicKeyBase64 = "1CVe/DCVd7Pv6mU6K+TruPtG5W618wyis7Xw0ZLMxDk=";

    /// <summary>The pinned key, decoded.</summary>
    public static byte[] PinnedPublicKey() => Convert.FromBase64String(PinnedPublicKeyBase64);

    /// <summary>True when <paramref name="publicKey" /> is 32 bytes that decode to
    /// a valid Ed25519 point.</summary>
    public static bool IsValidPublicKey(byte[]? publicKey)
        => publicKey is { Length: KeyBytes } && Ed25519.ValidatePublicKeyFull(publicKey, 0);

    /// <summary>The <see cref="ReleaseLinePrefix" /> line for a tag.</summary>
    public static string ReleaseLine(string tag) => ReleaseLinePrefix + NormalizeTag(tag);

    /// <summary>
    ///     Checks a release's <c>SHA256SUMS.txt</c> bytes against its signature
    ///     asset and the tag it was fetched for. Null means it verifies; anything
    ///     else is the reason it does not, written for the log and the user.
    /// </summary>
    /// <param name="manifest">The exact bytes of <c>SHA256SUMS.txt</c>.</param>
    /// <param name="signatureAsset">The bytes of <see cref="AssetName" />, or null
    /// when the release publishes none.</param>
    /// <param name="tag">The release tag the manifest was fetched for.</param>
    /// <param name="publicKey">The key to verify with — the pinned one in the app.</param>
    public static string? Check(byte[] manifest, byte[]? signatureAsset, string tag, byte[] publicKey)
    {
        if (signatureAsset == null || signatureAsset.Length == 0)
            return $"the release publishes no {AssetName}, so nothing proves who built it";

        var signature = TryDecodeSignature(signatureAsset);
        if (signature == null)
            return $"{AssetName} is not a base64 Ed25519 signature";

        if (!IsValidPublicKey(publicKey))
            return "the pinned release-signing key is not a valid Ed25519 public key";

        bool verified;
        try
        {
            verified = Ed25519.Verify(signature, 0, publicKey, 0, manifest, 0, manifest.Length);
        }
        catch (ArgumentException)
        {
            verified = false;
        }

        if (!verified)
        {
            return $"{ReleaseChecksums.AssetName} does not match its signature under the pinned " +
                   "release-signing key — it was not published by DotCharter's release process";
        }

        var named = ReadReleaseLine(manifest);
        if (named == null)
            return $"the signed {ReleaseChecksums.AssetName} does not say which release it belongs to";
        if (!string.Equals(named, NormalizeTag(tag), StringComparison.OrdinalIgnoreCase))
            return $"the signed {ReleaseChecksums.AssetName} belongs to {named}, not {NormalizeTag(tag)}";

        return null;
    }

    /// <summary>Signs <paramref name="manifest" /> with a 32-byte Ed25519 private
    /// key (seed) and returns the asset text: base64 and a newline. Used by the
    /// release tool and the tests; the app only ever verifies.</summary>
    public static string Sign(byte[] manifest, byte[] privateKey)
    {
        if (privateKey is not { Length: KeyBytes })
            throw new ArgumentException("An Ed25519 private key is 32 bytes.", nameof(privateKey));

        var signature = new byte[SignatureBytes];
        Ed25519.Sign(privateKey, 0, manifest, 0, manifest.Length, signature, 0);
        return Convert.ToBase64String(signature) + "\n";
    }

    /// <summary>The public key of a 32-byte Ed25519 private key.</summary>
    public static byte[] PublicKeyOf(byte[] privateKey)
    {
        if (privateKey is not { Length: KeyBytes })
            throw new ArgumentException("An Ed25519 private key is 32 bytes.", nameof(privateKey));

        var publicKey = new byte[KeyBytes];
        Ed25519.GeneratePublicKey(privateKey, 0, publicKey, 0);
        return publicKey;
    }

    private static byte[]? TryDecodeSignature(byte[] asset)
    {
        if (asset.Length > MaxSignatureBytes) return null;
        try
        {
            var bytes = Convert.FromBase64String(Encoding.ASCII.GetString(asset).Trim());
            return bytes.Length == SignatureBytes ? bytes : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string? ReadReleaseLine(byte[] manifest)
    {
        string? named = null;
        foreach (var raw in Encoding.UTF8.GetString(manifest).Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith(ReleaseLinePrefix.TrimEnd(), StringComparison.Ordinal)) continue;

            // Two release lines is a manifest that says two things; believe neither.
            if (named != null) return null;
            named = NormalizeTag(line[ReleaseLinePrefix.TrimEnd().Length..].Trim());
        }

        return string.IsNullOrEmpty(named) ? null : named;
    }

    private static string NormalizeTag(string tag)
    {
        var trimmed = tag.Trim();
        return trimmed.Length == 0 ? trimmed : "v" + trimmed.TrimStart('v', 'V');
    }
}
