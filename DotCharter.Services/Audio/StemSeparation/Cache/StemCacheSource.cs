namespace DotCharter.Services.Audio.StemSeparation.Cache;

/// <summary>
/// Which FILE a song's stem key signs, decided in ONE place for every lookup and
/// every save.
/// <para>A library (PSARC) song whose playable WAV sits beside its source
/// <c>.wem</c> — the <c>PsarcAudioCache</c> layout, <c>&lt;id&gt;.wem</c> +
/// <c>&lt;id&gt;.wav</c> — is keyed on the WEM's bytes
/// (<see cref="StemCacheKey.TryDeriveForWem"/>): the WAV is a decode, the cache
/// evicts it, and the decoder that recreates it may differ from the one that made
/// it by a few LSBs. Everything else (own charts, a library song with no WEM
/// beside its audio) is keyed on the audio it plays, as before.</para>
/// </summary>
/// <param name="Key">The key to look up and to save under.</param>
/// <param name="Signature">The signature folded into <paramref name="Key"/> (recorded in the manifest).</param>
/// <param name="SignedPath">The file <paramref name="Signature"/> was taken from; its full hash is the
/// manifest's <c>SourceFullHash</c>.</param>
/// <param name="PlayablePath">The audio the song plays (and separates).</param>
public sealed record StemCacheSource(
    StemCacheKey Key,
    StemAudioSignature Signature,
    string SignedPath,
    string PlayablePath)
{
    /// <summary>True when the key signs the source <c>.wem</c>, not the playable file.</summary>
    public bool SignsSourceWem => Key.SignsSourceWem;

    /// <summary>
    /// The source <c>.wem</c> beside <paramref name="playablePath"/> for a library
    /// identity, or <c>null</c> (an own chart, or no WEM there).
    /// </summary>
    public static string? SourceWemFor(StemCacheIdentity identity, string playablePath)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (string.IsNullOrWhiteSpace(identity.LogicalId) || string.IsNullOrWhiteSpace(playablePath)) return null;
        var wem = Path.ChangeExtension(playablePath, ".wem");
        return File.Exists(wem) ? wem : null;
    }

    /// <summary>
    /// The key for this song, or <c>null</c> when nothing can be signed (callers
    /// then neither look up nor save — see <see cref="StemCacheKey.TryDerive"/>).
    /// A WEM that exists but cannot be read falls back to the playable file's key,
    /// the key every earlier build used, which a later lookup ADOPTS onto the WEM
    /// key (<see cref="StemCacheCoordinator"/>).
    /// </summary>
    public static StemCacheSource? TryResolve(StemCacheIdentity identity, string playablePath)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (SourceWemFor(identity, playablePath) is { } wem)
        {
            var wemSignature = StemAudioSignature.ComputeFile(wem);
            if (StemCacheKey.TryDeriveForWem(identity, wemSignature) is { } wemKey)
                return new StemCacheSource(wemKey, wemSignature, wem, playablePath);
        }

        var signature = StemAudioSignature.ComputeFile(playablePath);
        return StemCacheKey.TryDerive(identity, signature) is { } key
            ? new StemCacheSource(key, signature, playablePath, playablePath)
            : null;
    }
}
