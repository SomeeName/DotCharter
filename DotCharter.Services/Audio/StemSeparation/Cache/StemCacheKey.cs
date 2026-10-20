using System.Security.Cryptography;
using System.Text;
using DotCharter.Services.Library;

namespace DotCharter.Services.Audio.StemSeparation.Cache;

/// <summary>
/// The derived cache key for a song's stems. Combines the logical
/// <see cref="StemCacheIdentity"/> with the source-audio
/// <see cref="StemAudioSignature"/> so that:
/// <list type="bullet">
///   <item>the same song (same identity + same audio) always maps to the same
///     cache entry — stems are generated once and never regenerated; and</item>
///   <item>a changed audio (different bytes/size), even under a reused SongKey,
///     produces a different key and therefore never serves stale stems.</item>
/// </list>
/// Pure and deterministic — no filesystem access.
/// </summary>
public sealed record StemCacheKey(string Canonical, string DirectoryToken)
{
    public static StemCacheKey Derive(StemCacheIdentity identity, StemAudioSignature signature)
    {
        ArgumentNullException.ThrowIfNull(identity);

        // Canonical form is intentionally human-readable so it can appear in
        // logs and the manifest; the DirectoryToken is the filesystem-safe hash.
        var canonical = Canonicalize(Basis(identity), signature);
        return new StemCacheKey(canonical, ShortHash(canonical));
    }

    /// <summary>
    /// The key for this identity + signature, or <c>null</c> when the pair cannot
    /// safely identify anything. THE ONLY DERIVATION A CACHE SHOULD CALL.
    ///
    /// <para>Two degenerate inputs are rejected. An EMPTY signature means
    /// <see cref="StemAudioSignature.ComputeFile"/> could not read the audio — it
    /// swallows every exception and returns <see cref="StemAudioSignature.None"/>,
    /// and a <c>File.Exists</c> guard does not cover it (an ACL-denied,
    /// AV-quarantined or exclusively-locked file exists and still fails to open).
    /// Keying anyway produced <c>…|b=0|h=</c>, which (a) MISSES the song's real
    /// archive and re-separates a song that is already cached — the invariant this
    /// whole subsystem exists to uphold — and (b) then saves a duplicate archive
    /// under the degenerate key that the correct key can never hit again. Worse,
    /// once that duplicate exists, ANY later moment where the audio is transiently
    /// unreadable HITS it, so a user who replaced the audio in place is served the
    /// old stems — defeating the content-signature invalidation the signature is
    /// for.</para>
    ///
    /// <para>An identity that NAMES NOTHING — no logical id and no source label —
    /// is the second refusal. Under the old path-folding basis it collapsed to the
    /// literal <c>"file:"</c>, so together with an unreadable signature the
    /// canonical key was the universal string <c>"file:|b=0|h="</c> and any two
    /// such songs shared one archive. The basis does not carry the path, so the
    /// collision is gone by construction — but "we were handed an identity for
    /// nothing at all" is still not a song, and keying it would cache under a call
    /// site that has lost track of what it is caching.</para>
    ///
    /// <para>Callers treat <c>null</c> as "do not look up, do not save" and log it
    /// once — never as a reason to key on something weaker.</para>
    /// </summary>
    public static StemCacheKey? TryDerive(StemCacheIdentity identity, StemAudioSignature signature)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (signature.IsEmpty) return null;
        if (NamesNothing(identity)) return null;

        var canonical = Canonicalize(Basis(identity), signature);
        return new StemCacheKey(canonical, ShortHash(canonical));
    }

    /// <summary>
    /// The key of a LIBRARY song whose stems are keyed on its SOURCE <c>.wem</c>,
    /// or <c>null</c> when the identity names no song or the WEM could not
    /// be signed.
    /// <para>The playable WAV is DECODED from the WEM, and two decoders (ww2ogg +
    /// NVorbis, and the native one) differ in a few LSBs. Keying on the WAV's bytes
    /// would orphan every archive the moment an evicted WAV is decoded again by a
    /// different decoder; the WEM's bytes are what the archive ships and do not
    /// depend on any decoder. The marker keeps the two forms apart:
    /// <c>psarc:…|wem|b=…|h=…</c> against the playable form <c>psarc:…|b=…|h=…</c>,
    /// which <see cref="AcceptsRecordedKey"/> still accepts for an ADOPTED archive
    /// (see <see cref="StemCacheCoordinator"/>).</para>
    /// </summary>
    public static StemCacheKey? TryDeriveForWem(StemCacheIdentity identity, StemAudioSignature wemSignature)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (wemSignature.IsEmpty) return null;
        if (string.IsNullOrWhiteSpace(identity.LogicalId)) return null;

        var canonical = Canonicalize(identity.LogicalId!.Trim() + WemMarker, wemSignature);
        return new StemCacheKey(canonical, ShortHash(canonical));
    }

    /// <summary>True for a key from <see cref="TryDeriveForWem"/>.</summary>
    public bool SignsSourceWem => Canonical.Contains(WemMarker + "|b=", StringComparison.Ordinal);

    /// <summary>
    /// Whether <paramref name="recordedKey"/> is this WEM key's song in the
    /// PLAYABLE-file form older builds wrote (the same logical id, no
    /// marker). False for any other key.
    /// </summary>
    public bool IsPlayableFormOfThisSong(string? recordedKey)
    {
        if (string.IsNullOrEmpty(recordedKey)) return false;
        var marker = Canonical.LastIndexOf(WemMarker + "|b=", StringComparison.Ordinal);
        if (marker < 0) return false;
        var basis = Canonical[..marker];
        return recordedKey.StartsWith(basis + "|b=", StringComparison.Ordinal);
    }

    /// <summary>
    /// This library key as older builds spelled it — the same key with
    /// the <c>psarc:</c> basis the stem cache hashed then — or <c>null</c> for a
    /// key that is not a <c>lib:</c> one (an own chart's has no earlier library form).
    /// <para>Kept ONLY so an archive filed under the old name can be found and
    /// RE-KEYED (<c>StemCacheStore.TryRekeyLegacyLibraryArchive</c>): a lookup never
    /// serves an archive under this key, it migrates it to <see langword="this" /> one.</para>
    /// </summary>
    public StemCacheKey? LegacyLibraryForm
    {
        get
        {
            if (!Canonical.StartsWith(LibrarySongIdentity.Prefix, StringComparison.Ordinal)) return null;
            var canonical = LibrarySongIdentity.LegacyPrefix + Canonical[LibrarySongIdentity.Prefix.Length..];
            return new StemCacheKey(canonical, ShortHash(canonical));
        }
    }

    /// <summary>
    /// The key this identity + signature produced under the builds that folded the
    /// audio's FULL PATH into an own-chart basis, or <c>null</c> when there is no
    /// such earlier form (a library identity was never path-keyed).
    /// <para>Kept ONLY so an existing archive can be adopted rather than stranded —
    /// see <c>StemCacheStore.TryAdoptSupersededArchive</c>. Changing the basis
    /// without this would orphan every own-chart archive on disk, which is the same
    /// data loss as re-separating, in a different shirt.</para>
    /// </summary>
    public static StemCacheKey? TryDeriveSuperseded(StemCacheIdentity identity, StemAudioSignature signature)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (signature.IsEmpty) return null;
        if (!string.IsNullOrWhiteSpace(identity.LogicalId)) return null;

        var path = NormalizePath(identity.SourceLabel);
        if (path.Length == 0) return null;

        var canonical = Canonicalize(FilePrefix + path, signature);
        return new StemCacheKey(canonical, ShortHash(canonical));
    }

    /// <summary>
    /// Whether an archive that RECORDS <paramref name="recordedKey"/> answers this
    /// key.
    /// <para>An EMPTY recorded key is accepted — that is defence in depth for an
    /// archive written before the field existed, never a migration; see
    /// <c>StemCacheStore.KeyMatches</c>.</para>
    /// <para>A recorded key in the SUPERSEDED path-folding form is accepted too,
    /// when its signature half is identical. That is what makes adoption cheap: the
    /// archive keeps the key it was written with, and the alternative — rewriting
    /// a manifest entry inside a hundred-megabyte zip, which
    /// <c>ZipArchiveMode.Update</c> would read wholly into memory — buys nothing.
    /// It is also the ONLY thing that makes an adopted archive survive the NEXT
    /// move of its audio, which is the entire point of dropping the path.</para>
    /// </summary>
    public bool AcceptsRecordedKey(string? recordedKey)
    {
        if (string.IsNullOrEmpty(recordedKey)) return true;
        if (string.Equals(recordedKey, Canonical, StringComparison.Ordinal)) return true;

        // A WEM-keyed library song accepts the archive its playable WAV keyed before
        // An archive only reaches this key's file by an adoption that verified
        // it (StemCacheCoordinator), and the archive keeps the key it was written
        // with, exactly like the path-keyed own-chart archives below.
        if (SignsSourceWem) return IsPlayableFormOfThisSong(recordedKey);

        // Only an own-chart key may adopt a path-keyed archive: a library key names
        // a specific song and must never accept anything else.
        if (!Canonical.StartsWith(AudioPrefix, StringComparison.Ordinal)) return false;
        if (!recordedKey.StartsWith(FilePrefix, StringComparison.Ordinal)) return false;

        var mine = SignatureSuffix(Canonical);
        var theirs = SignatureSuffix(recordedKey);
        return mine.Length > 0 && string.Equals(mine, theirs, StringComparison.Ordinal);
    }

    private const string FilePrefix = "file:";

    /// <summary>Marks a library key that signs the song's source <c>.wem</c>.</summary>
    private const string WemMarker = "|wem";

    /// <summary>The path-independent basis every own-chart key uses.</summary>
    private const string AudioPrefix = "audio:";

    /// <summary>
    /// <para><b>Own-chart keys deliberately do NOT fold the audio's path in.</b> Folding it in would mean that renaming the file,
    /// moving the project folder, switching to a synced/OneDrive path or opening
    /// the same song from a copy all produce a different key for BYTE-IDENTICAL
    /// audio — a cache miss, and minutes of separation paid again for stems that
    /// are already on disk. That is a direct weakening of "stems are NEVER
    /// regenerated"; the PSARC half was immune only because it keys on
    /// <c>psarc:SongKey</c>. The content signature already provides all the
    /// uniqueness the path was standing in for, and the degenerate cases the path
    /// appeared to guard are covered explicitly by <see cref="TryDerive"/>.</para>
    /// </summary>
    private static string Basis(StemCacheIdentity identity) =>
        !string.IsNullOrWhiteSpace(identity.LogicalId)
            ? identity.LogicalId!.Trim()
            : AudioPrefix;

    private static bool NamesNothing(StemCacheIdentity identity) =>
        string.IsNullOrWhiteSpace(identity.LogicalId) &&
        string.IsNullOrWhiteSpace(identity.SourceLabel);

    private static string Canonicalize(string basis, StemAudioSignature signature) =>
        $"{basis}|b={signature.Bytes}|h={signature.Sha256Hex}";

    /// <summary>The <c>|b=…|h=…</c> tail — the part that identifies the AUDIO
    /// rather than the song.</summary>
    private static string SignatureSuffix(string canonical)
    {
        var i = canonical.IndexOf("|b=", StringComparison.Ordinal);
        return i < 0 ? string.Empty : canonical[i..];
    }

    /// <summary>Windows-friendly path normalization: full path, forward slashes,
    /// case-folded (the filesystem is case-insensitive on the supported OS).</summary>
    internal static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        string full;
        try { full = Path.GetFullPath(path); }
        catch { full = path; }
        return full.Replace('\\', '/').ToUpperInvariant();
    }

    /// <summary>32-hex-char (128-bit) truncated SHA-256 of the canonical key —
    /// short enough for a filename, wide enough to avoid collisions.</summary>
    internal static string ShortHash(string value)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(digest, 0, 16).ToLowerInvariant();
    }
}
