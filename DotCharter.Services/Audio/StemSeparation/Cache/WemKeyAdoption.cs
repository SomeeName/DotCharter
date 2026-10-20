using DotCharter.Services.Library;

namespace DotCharter.Services.Audio.StemSeparation.Cache;

/// <summary>
/// Moves the stem archives that older builds keyed on a library song's
/// PLAYABLE WAV onto the song's WEM key (<see cref="StemCacheKey.TryDeriveForWem"/>),
/// so a WAV that is evicted and decoded again natively never orphans them (stems
/// are NEVER regenerated).
/// <para>The only proof is the WAV ITSELF: the WAV on disk still has the exact bytes
/// the archive was keyed on (the old key, and the recorded full hash when there is
/// one). Rebuilding the old decoder's WAV from the WEM is not possible any more (the
/// managed decoders are gone), so this proof is run for EVERY
/// such archive once, early, by <see cref="StartAtStartup"/> — before any of their
/// WAVs can be re-decoded. A lookup still runs it for its own song
/// (<see cref="StemCacheCoordinator"/>). An archive whose WAV was evicted and
/// re-decoded before a build with this pass ever ran cannot be proven any more.</para>
/// </summary>
public static class WemKeyAdoption
{
    /// <summary>Set by the headless test host: the app's startup pass then never runs
    /// against the developer's real caches.</summary>
    public const string DisableStartupPassSwitch = "DotCharter.StemCache.DisableWemKeyAdoptionPass";

    /// <summary>Written to the stem cache root once a pass has completed; the pass
    /// does not run again.</summary>
    internal const string DoneMarkerFileName = ".wem-key-adoption-v1";

    /// <summary>
    /// Runs <see cref="AdoptAll"/> once per stem cache on the thread pool, unless
    /// <see cref="DisableStartupPassSwitch"/> is set or the pass already completed.
    /// The stores are resolved THERE, never on the caller's (UI) thread. Never throws.
    /// </summary>
    public static Task StartAtStartup(Func<StemCacheStore> store, Func<string> audioCacheRoot, IAppLogger logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(audioCacheRoot);
        ArgumentNullException.ThrowIfNull(logger);
        if (AppContext.TryGetSwitch(DisableStartupPassSwitch, out var disabled) && disabled) return Task.CompletedTask;
        return Task.Run(() =>
        {
            try
            {
                RunOnce(store(), audioCacheRoot(), logger, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.Warning(AppLogCategory.Audio, $"Stem cache: the WEM-key adoption pass could not start ({ex.Message}).");
            }
        });
    }

    /// <summary>
    /// True once nothing can need a library song's ORIGINAL decoded WAV any more:
    /// the pass completed (its marker is written), or there is no stem cache at all,
    /// so no archive can be keyed on a WAV (every archive written now is keyed
    /// on the <c>.wem</c>). Until then a cached WAV must keep its bytes -- the
    /// device-rate conversion (<c>LibraryDeviceRateAudio</c>) waits for this.
    /// </summary>
    public static bool HasCompleted(StemCacheStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        try
        {
            return !Directory.Exists(store.Root) || File.Exists(Path.Combine(store.Root, DoneMarkerFileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>The once-only wrapper: skips a completed pass, marks a finished one.
    /// Returns the archives adopted, or -1 when the pass was skipped or failed.</summary>
    internal static int RunOnce(StemCacheStore store, string audioCacheRoot, IAppLogger? logger, CancellationToken ct)
    {
        var marker = Path.Combine(store.Root, DoneMarkerFileName);
        try
        {
            if (!Directory.Exists(store.Root) || File.Exists(marker)) return -1;
            var adopted = AdoptAll(store, audioCacheRoot, logger, ct);
            File.WriteAllText(marker, $"adopted={adopted}\n");
            return adopted;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.Warning(AppLogCategory.Audio, $"Stem cache: the WEM-key adoption pass failed ({ex.Message}); it runs again next start.");
            return -1;
        }
    }

    /// <summary>
    /// Walks every archive still keyed on a library song's playable WAV and adopts
    /// each one whose WAV is still in <paramref name="audioCacheRoot"/> (the PSARC
    /// audio cache, <c>&lt;folder&gt;/&lt;id&gt;.wav</c> beside <c>&lt;id&gt;.wem</c>) with the
    /// bytes it was keyed on. Idempotent: an adopted archive is keyed on the WEM and
    /// so does not match. Renames only (never overwrites, never deletes); returns the
    /// number adopted and logs it.
    /// </summary>
    public static int AdoptAll(StemCacheStore store, string audioCacheRoot, IAppLogger? logger, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        var pending = store.FindArchives(IsKeyedOnPlayable).ToList();
        if (pending.Count == 0 || !Directory.Exists(audioCacheRoot)) return 0;

        var adopted = 0;
        foreach (var wav in Directory.EnumerateFiles(audioCacheRoot, "*.wav", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            if (pending.Count == 0) break;
            var wem = Path.ChangeExtension(wav, ".wem");
            if (!File.Exists(wem)) continue;
            long length;
            try
            {
                length = new FileInfo(wav).Length;
            }
            catch (IOException)
            {
                continue;
            }

            if (!pending.Any(p => p.Manifest.SourceBytes == length)) continue;

            var signature = StemAudioSignature.ComputeFile(wav);
            if (signature.IsEmpty) continue;
            string? fullHash = null;
            for (var i = pending.Count - 1; i >= 0; i--)
            {
                var (path, manifest) = pending[i];
                // ForLibrary spells the id lib:; an archive from before then records
                // psarc:, so it is compared, and moved, in that spelling.
                var identity = StemCacheIdentity.ForLibrary(manifest.LogicalId!);
                if (StemCacheKey.TryDerive(identity, signature) is not { } exact ||
                    exact.Canonical != LibrarySongIdentity.Normalize(manifest.Key))
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(manifest.SourceFullHash))
                {
                    fullHash ??= StemAudioSignature.ComputeFullFileHash(wav);
                    if (!string.Equals(manifest.SourceFullHash, fullHash, StringComparison.OrdinalIgnoreCase)) continue;
                }

                if (StemCacheKey.TryDeriveForWem(identity, StemAudioSignature.ComputeFile(wem)) is not { } wemKey) continue;
                const string reason = "keyed on the playable WAV before #426 (startup pass)";
                var moved = LibrarySongIdentity.IsLegacy(manifest.Key)
                    ? exact.LegacyLibraryForm is { } legacy && store.TryRekeyLegacyArchiveAt(path, legacy, wemKey, reason)
                    : store.TryAdoptArchiveAt(path, wemKey, reason);
                if (moved)
                {
                    adopted++;
                    pending.RemoveAt(i);
                }
            }
        }

        logger?.Info(AppLogCategory.Audio,
            $"Stem cache: the WEM-key adoption pass adopted {adopted} archive(s); {pending.Count} WAV-keyed archive(s) had no matching WAV.");
        return adopted;
    }

    /// <summary>
    /// The per-lookup form of the same proof, for one song: files the archive its
    /// playable WAV keyed under the song's WEM key when the WAV on disk still has
    /// that archive's bytes. Never overwrites and never throws; anything else is a
    /// plain miss.
    /// </summary>
    internal static bool TryAdoptByPlayable(StemCacheStore store, StemCacheIdentity identity, StemCacheSource source)
    {
        if (File.Exists(store.ArchivePath(source.Key))) return false;

        var playable = StemAudioSignature.ComputeFile(source.PlayablePath);
        if (StemCacheKey.TryDerive(identity, playable) is not { } exact) return false;

        // An older archive was named after the psarc: identity: re-key it to
        // the lib: playable key first, then adopt it like any other. When the re-key
        // fails, adopt it in the OLD spelling (its psarc: WEM name, the move every build
        // of the earlier WEM-key move) so the lookup's psarc: fallback still finds it.
        store.TryRekeyLegacyLibraryArchive(exact);
        if (TryAdopt(store, exact, source.Key, source.PlayablePath)) return true;
        return exact.LegacyLibraryForm is { } legacyPlayable && source.Key.LegacyLibraryForm is { } legacyWem &&
               TryAdopt(store, legacyPlayable, legacyWem, source.PlayablePath);
    }

    private static bool TryAdopt(StemCacheStore store, StemCacheKey playableKey, StemCacheKey wemKey, string playablePath) =>
        File.Exists(store.ArchivePath(playableKey)) &&
        store.TryGetManifest(playableKey, out var manifest) &&
        (string.IsNullOrEmpty(manifest.SourceFullHash) ||
         string.Equals(manifest.SourceFullHash, StemAudioSignature.ComputeFullFileHash(playablePath),
             StringComparison.OrdinalIgnoreCase)) &&
        store.TryAdoptArchiveAt(store.ArchivePath(playableKey), wemKey, "keyed on the playable WAV before #426");

    /// <summary>A library archive keyed on the playable form <c>&lt;id&gt;|b=…|h=…</c>
    /// (not yet on a WEM key).</summary>
    private static bool IsKeyedOnPlayable(StemCacheManifest manifest)
    {
        var id = manifest.LogicalId?.Trim();
        return !string.IsNullOrEmpty(id) && manifest.Key.StartsWith(id + "|b=", StringComparison.Ordinal);
    }
}
