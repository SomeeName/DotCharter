namespace DotCharter.Services.Audio.StemSeparation.Cache;

/// <summary>
///     The never-regenerate front door for stem separation. Given a song identity
///     and its source audio it derives a robust cache key (identity folded with an
///     audio content signature) and, on a hit, expands the cached archive so the
///     caller never runs separation again — not on the second launch, not ever,
///     until the audio itself changes.
///
///     This is the seam the app calls (<c>StemSeparationViewModel.SeparateStemsAsync</c>)
///     AND the seam the tests exercise. Keep it that way: an earlier revision had
///     the view model re-implement the hit path inline, so the tested code and the
///     shipped code drifted apart (they disagreed on corrupt-archive recovery)
///     while the suite stayed green.
///
///     Saving after a miss stays with the caller, which owns the model check and
///     the keep/replace dialog.
/// </summary>
public sealed class StemCacheCoordinator
{
    private readonly StemCacheStore _store;
    private readonly IAppLogger? _logger;

    public StemCacheCoordinator(StemCacheStore store, IAppLogger? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger;
    }

    /// <summary>
    ///     Expands cached stems for this exact song + audio into
    ///     <paramref name="playbackDir" />, or returns <c>null</c> when there is
    ///     nothing usable cached and the caller must separate.
    ///
    ///     Never throws for cache problems, and deletes an archive ONLY when it is
    ///     provably corrupt (a partially written archive cannot be allowed to wedge
    ///     the user out of their stems forever). Every other failure — a missing
    ///     decoder, a full disk, an ACL, a locked destination — is a plain miss that
    ///     KEEPS the archive: stems are never regenerated, so an archive we merely
    ///     failed to READ must survive to be read again.
    /// </summary>
    public async Task<IReadOnlyList<StemTrack>?> TryLoadCachedAsync(
        StemCacheIdentity identity,
        string inputAudioPath,
        string playbackDir,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (string.IsNullOrWhiteSpace(inputAudioPath) || !File.Exists(inputAudioPath)) return null;

        // TryDerive, not Derive: ComputeFile swallows every exception and returns
        // StemAudioSignature.None, and the File.Exists guard above does NOT cover
        // that — an ACL-denied, AV-quarantined or exclusively-locked file exists and
        // still fails to open. Keying on the degenerate signature made the lookup
        // miss the song's real archive and re-separate an already-cached song, then
        // (via the save side) leave a duplicate archive behind that the correct key
        // never hits again. WaveformDiskCache has had this guard since it was
        // written — "no key means no caching" — and the stem path is the one that
        // actually matters, because these stems are never regenerated.
        //
        // (The old `catch (IOException)` here was dead code: ComputeFile never lets
        // an exception escape, which is exactly how the hole stayed invisible.)
        //
        // StemCacheSource decides WHICH file is signed: a library song's source .wem
        // when it sits beside the playable WAV, else the audio itself.
        var source = StemCacheSource.TryResolve(identity, inputAudioPath);
        if (source == null)
        {
            _logger?.Warning(AppLogCategory.Audio,
                $"Stem cache key could not be derived for '{identity.LogicalId ?? identity.SourceLabel}' " +
                $"(audio '{inputAudioPath}' unreadable); treating as a miss and not caching.");
            return null;
        }

        var key = source.Key;

        // Older builds hashed a library song's key from its psarc: identity. File that
        // archive under the lib: key (rewritten, verified, then renamed) before
        // probing; a failure leaves it under the old name for the next lookup, and a
        // twin already under the new name wins (both are kept, the twin is logged).
        _store.TryRekeyLegacyLibraryArchive(key);

        if (source.SignsSourceWem)
        {
            // Older builds keyed a library song on its playable WAV's
            // bytes. Re-key that archive onto the WEM before probing, or the switch
            // to a decoder-independent key would orphan every library archive.
            WemKeyAdoption.TryAdoptByPlayable(_store, identity, source);
        }
        else
        {
            // An earlier build folded the audio's FULL PATH into an own-chart key, so every archive
            // it wrote is filed under a token this one never derives.
            // File it under the current key before probing, or the never-regenerate
            // invariant would be broken by the very change that strengthened it.
            var superseded = StemCacheKey.TryDeriveSuperseded(identity, source.Signature);
            if (superseded != null) _store.TryAdoptSupersededArchive(key, superseded);
        }

        // A re-key that failed (a full disk, a locked file, a copy that did not read
        // back identical) leaves the archive under its psarc: name. Read it there: a
        // miss would re-separate stems that are on disk. The next lookup re-keys it.
        if (!File.Exists(_store.ArchivePath(key)) && key.LegacyLibraryForm is { } legacy &&
            File.Exists(_store.ArchivePath(legacy)))
        {
            _logger?.Info(AppLogCategory.Audio,
                $"Stem cache: reading '{identity.LogicalId}' under its pre-#898 name (token={legacy.DirectoryToken}).");
            key = legacy;
        }

        try
        {
            if (!_store.TryGetManifest(key, out var manifest))
            {
                // An archive whose manifest cannot even be read is corrupt (a truncated
                // write, a half-copied cache dir). Reporting a plain miss would leave it
                // occupying disk forever, so drop it here too — not only when a later
                // extract throws.
                if (File.Exists(_store.ArchivePath(key)))
                {
                    _logger?.Warning(AppLogCategory.Audio,
                        $"Stem cache archive is unreadable (token={key.DirectoryToken}); deleting it.");
                    _store.Delete(key);
                }

                return null;
            }

            // The key samples the audio; the archive may record its WHOLE hash.
            // When it does and today's audio hashes differently, the audio changed
            // where the samples do not look (a same-length re-render with a fix in
            // the middle), and these stems are of the old audio. That is a miss; the
            // archive is kept until the new separation replaces it under this key.
            //
            // An archive ADOPTED onto a WEM key records the hash of the WAV it was
            // separated from, which a re-decode may have changed by design; the
            // adoption verified it, and the WEM key pins the source from then on.
            var hashedPath = !source.SignsSourceWem
                ? inputAudioPath
                : string.Equals(manifest.Key, key.Canonical, StringComparison.Ordinal) ? source.SignedPath : null;
            if (hashedPath != null && !string.IsNullOrEmpty(manifest.SourceFullHash))
            {
                var current = StemAudioSignature.ComputeFullFileHash(hashedPath);
                if (current != null && !string.Equals(current, manifest.SourceFullHash, StringComparison.OrdinalIgnoreCase))
                {
                    _logger?.Info(AppLogCategory.Audio,
                        $"Stem cache entry for '{identity.LogicalId ?? identity.SourceLabel}' (token={key.DirectoryToken}) " +
                        "was made from different audio of the same length; treating it as a miss so the stems " +
                        "are separated from the current audio.");
                    return null;
                }
            }
        }
        catch (Exception ex)
        {
            // e.g. UnauthorizedAccessException from an ACL-denied/AV-quarantined
            // archive escaping ReadManifest's catch list. Environmental, not
            // corruption — honour the "never throws for cache problems" contract
            // with a plain miss and leave the archive alone.
            _logger?.Warning(AppLogCategory.Audio,
                $"Stem cache manifest probe failed ({ex.Message}); treating as a miss.");
            return null;
        }

        try
        {
            var cached = await _store.ExtractAsync(key, playbackDir, ct).ConfigureAwait(false);
            if (cached.Count == 0) return null;

            _logger?.Info(AppLogCategory.Audio,
                $"Stem cache HIT for '{identity.LogicalId ?? identity.SourceLabel}' " +
                $"(token={key.DirectoryToken}). Skipping separation.");
            return cached;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (StemDecodeUnavailableException ex)
        {
            // The archive is valid — the DECODER is missing or failed (ffmpeg
            // removed from PATH, locked output, disk full). Deleting here would
            // destroy the durable never-regenerate copy over a transient/tooling
            // problem, so keep it and report a miss.
            _logger?.Warning(AppLogCategory.Audio,
                $"Stem cache hit could not be decoded ({ex.Message}); keeping the archive.");
            return null;
        }
        catch (Exception ex) when (IsArchiveCorruption(ex))
        {
            _logger?.Warning(AppLogCategory.Audio,
                $"Stem cache archive is corrupt ({ex.GetType().Name}: {ex.Message}); " +
                "deleting it and re-separating.");
            _store.Delete(key);
            return null;
        }
        catch (Exception ex)
        {
            // Everything else is ENVIRONMENTAL, and this arm must not be the
            // catch-all that deletes the archive. ExtractAsync throws plenty that
            // is not corruption: Directory.CreateDirectory(destDir) on a full disk
            // or a denied ACL, ExtractToFile when the destination WAV is still open
            // in the JUCE mixer from the previous session, and — the worst case —
            // the WAV-codec decode path, which is a plain File.Copy and is exactly
            // what a fresh install WITHOUT ffmpeg produces, so that configuration
            // had no protection at all. Losing the only copy of a 40-minute
            // separation to a transient IOException is the most direct violation of
            // the never-regenerate invariant in the subsystem: keep the archive and
            // report a miss, same as a decode outage.
            _logger?.Warning(AppLogCategory.Audio,
                $"Stem cache extract failed ({ex.GetType().Name}: {ex.Message}); " +
                "keeping the archive and reporting a miss.");
            return null;
        }
    }

    /// <summary>
    ///     True ONLY for exceptions that prove the archive bytes are unusable —
    ///     never for an environmental failure to read them.
    ///     <para><see cref="InvalidDataException"/> is what <c>ZipArchive</c> raises
    ///     for a bad signature / corrupt local header / CRC mismatch, and
    ///     <see cref="EndOfStreamException"/> for a truncated entry. The latter
    ///     derives from <see cref="IOException"/>, so it must be recognised here
    ///     rather than falling into the keep-the-archive arm below.</para>
    /// </summary>
    private static bool IsArchiveCorruption(Exception ex) => ex switch
    {
        StemArchiveCorruptException => true,
        InvalidDataException => true,
        EndOfStreamException => true,
        _ => false
    };
}
