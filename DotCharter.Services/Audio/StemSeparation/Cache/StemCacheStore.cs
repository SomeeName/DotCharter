namespace DotCharter.Services.Audio.StemSeparation.Cache;

/// <summary>
/// Durable, per-song stem cache. Each song's stems live as ONE compressed
/// <c>.dcstems</c> archive under
/// <c>%LocalAppData%/DotCharter/StemCache/&lt;token&gt;.dcstems</c> (LocalAppData,
/// not Roaming — this is large, regenerable data). On a cache hit the archive
/// is expanded to WAV on demand for JUCE; separation never re-runs.
/// </summary>
public sealed class StemCacheStore
{
    private readonly string _root;
    private readonly IStemEncoder _encoder;
    private readonly IAppLogger? _logger;
    private readonly Func<DateTime> _clock;

    public StemCacheStore(
        IStemEncoder? encoder = null,
        IAppLogger? logger = null,
        string? rootOverride = null,
        Func<DateTime>? clock = null)
    {
        _encoder = encoder ?? new FfmpegStemEncoder(logger);
        _logger = logger;
        _clock = clock ?? (() => DateTime.UtcNow);
        _root = rootOverride ?? DefaultRoot();
    }

    /// <summary>Root directory of the cache (created lazily on write).</summary>
    public string Root => _root;

    /// <summary>The encoder chosen by DI — exposed so callers can report the codec.</summary>
    public IStemEncoder Encoder => _encoder;

    internal static string DefaultRoot()
    {
        var localAppData = UserDataFolders.LocalAppData;
        if (string.IsNullOrWhiteSpace(localAppData)) localAppData = Path.GetTempPath();
        return Path.Combine(localAppData, "DotCharter", "StemCache");
    }

    /// <summary>Absolute path of the archive that would hold <paramref name="key"/>'s stems.</summary>
    public string ArchivePath(StemCacheKey key) =>
        Path.Combine(_root, key.DirectoryToken + StemArchive.ArchiveExtension);

    /// <summary>
    /// Every archive whose manifest reads and satisfies <paramref name="match"/>
    /// (unreadable ones are skipped). Reads each manifest, so callers use it on a
    /// miss only.
    /// </summary>
    internal IReadOnlyList<(string Path, StemCacheManifest Manifest)> FindArchives(Func<StemCacheManifest, bool> match)
    {
        var found = new List<(string, StemCacheManifest)>();
        try
        {
            if (!Directory.Exists(_root)) return found;
            foreach (var path in Directory.EnumerateFiles(_root, "*" + StemArchive.ArchiveExtension))
            {
                var manifest = StemArchive.ReadManifest(path);
                if (manifest is not null && manifest.SchemaVersion <= StemCacheManifest.CurrentSchemaVersion &&
                    match(manifest))
                {
                    found.Add((path, manifest));
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.Warning(AppLogCategory.Audio, $"Stem cache: could not search the archives: {ex.Message}");
        }

        return found;
    }

    /// <summary>
    /// Files the archive at <paramref name="archivePath"/> under
    /// <paramref name="current"/> (a same-directory rename, never an overwrite) and
    /// reports whether it did. The archive keeps the key it records; the caller has
    /// already proved it belongs to <paramref name="current"/>.
    /// </summary>
    internal bool TryAdoptArchiveAt(string archivePath, StemCacheKey current, string reason)
    {
        ArgumentNullException.ThrowIfNull(current);
        try
        {
            var to = ArchivePath(current);
            if (File.Exists(to) || !File.Exists(archivePath) ||
                string.Equals(Path.GetFullPath(archivePath), Path.GetFullPath(to), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            File.Move(archivePath, to);
            _logger?.Info(AppLogCategory.Audio,
                $"Stem cache: adopted '{Path.GetFileName(archivePath)}' as {current.DirectoryToken} ({reason}); " +
                "stems are not re-separated.");
            return true;
        }
        catch (Exception ex)
        {
            _logger?.Warning(AppLogCategory.Audio,
                $"Stem cache: could not adopt '{Path.GetFileName(archivePath)}': {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Files the archive an earlier build named after the song's <c>psarc:</c>
    /// identity under <paramref name="current"/> (a <c>lib:</c> key), rewriting the
    /// identity it records, and reports whether it did. Copy, flush, verify,
    /// rename, and only then remove the old file — see <see cref="LegacyLibraryRekey"/>.
    /// Never throws, never overwrites, never re-separates.
    /// </summary>
    public bool TryRekeyLegacyLibraryArchive(StemCacheKey current)
    {
        ArgumentNullException.ThrowIfNull(current);
        return LegacyLibraryRekey.TryRekey(this, current, _logger);
    }

    /// <summary>The same re-key for an archive found by its manifest
    /// (<see cref="WemKeyAdoption"/>'s startup pass): it records a key
    /// <paramref name="legacy"/> accepts and is filed under <paramref name="current"/>.</summary>
    internal bool TryRekeyLegacyArchiveAt(string archivePath, StemCacheKey legacy, StemCacheKey current, string reason) =>
        LegacyLibraryRekey.TryRekeyAt(this, archivePath, legacy, current, reason, _logger);

    /// <summary>Test seam: called at each <see cref="LegacyLibraryRekey.Stage"/> of a
    /// re-key with the file it just produced; a throw there is a crash at that point.</summary>
    internal Action<LegacyLibraryRekey.Stage, string>? RekeyFaultHook { get; set; }

    /// <summary>
    /// Files an archive written under an older, PATH-DEPENDENT key at the key this
    /// build derives, and reports whether it did.
    ///
    /// <para>Earlier builds folded the audio's full path into an own-chart key's basis, so
    /// the same bytes under a different name produced a different token. Dropping
    /// the path fixes that going forward — but on its own it would STRAND every
    /// archive already on disk, which is the same data loss as re-separating in a
    /// different shirt. This is the migration: a plain same-directory rename, no
    /// re-encode, no rewrite of the archive's manifest (the recorded key stays the
    /// old one and <see cref="StemCacheKey.AcceptsRecordedKey"/> accepts it).</para>
    ///
    /// <para>It never overwrites: if the new-key archive already exists, the old
    /// one is left where it is for the user to clear rather than silently
    /// destroyed.</para>
    /// </summary>
    public bool TryAdoptSupersededArchive(StemCacheKey current, StemCacheKey superseded)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(superseded);
        if (string.Equals(current.DirectoryToken, superseded.DirectoryToken, StringComparison.Ordinal))
            return false;

        try
        {
            var from = ArchivePath(superseded);
            var to = ArchivePath(current);
            if (File.Exists(to)) return false;
            if (!File.Exists(from))
            {
                // The audio may have moved before this version ever saw it.
                // Only a recorded legacy key with the same content signature
                // proves which existing archive belongs to it.
                if (!Directory.Exists(_root)) return false;
                from = Directory.EnumerateFiles(_root, "*" + StemArchive.ArchiveExtension)
                    .FirstOrDefault(path =>
                    {
                        var manifest = StemArchive.ReadManifest(path);
                        return manifest is not null &&
                               manifest.SchemaVersion <= StemCacheManifest.CurrentSchemaVersion &&
                               manifest.Key.StartsWith("file:", StringComparison.Ordinal) &&
                               current.AcceptsRecordedKey(manifest.Key);
                    });
                if (from is null) return false;
            }

            File.Move(from, to);
            _logger?.Info(AppLogCategory.Audio,
                $"Stem cache: adopted the archive filed under the old path-dependent key " +
                $"({superseded.DirectoryToken} → {current.DirectoryToken}); stems are not re-separated.");
            return true;
        }
        catch (Exception ex)
        {
            // A failed adoption is a plain miss at worst — never a reason to throw
            // out of the never-throws-for-cache-problems front door.
            _logger?.Warning(AppLogCategory.Audio,
                $"Stem cache: could not adopt the archive for '{superseded.DirectoryToken}': {ex.Message}");
            return false;
        }
    }

    /// <summary>Prefix of the per-save scratch directories under <see cref="Root"/>.</summary>
    internal const string StagingPrefix = ".staging-";

    /// <summary>How long a staging directory must be untouched before a sweep may
    /// take it. Anything younger could belong to a save running RIGHT NOW —
    /// including one in another instance of the app.</summary>
    internal static readonly TimeSpan AbandonedStagingAge = TimeSpan.FromHours(1);

    /// <summary>
    /// Deletes staging directories that no live save can still own, and returns
    /// how many went.
    /// <para><b>They orphan on every hard exit.</b> <see cref="SaveAsync"/> stages
    /// a full copy of every encoded stem PLUS the finished archive under
    /// <c>.staging-&lt;token&gt;-&lt;guid&gt;/</c>, removed only by that call's own
    /// <c>finally</c> — and the app's normal exit path is <c>App.TerminateNow</c> →
    /// <c>TerminateProcess</c>, which runs no <c>finally</c> at all. Quitting during
    /// a cache save therefore left hundreds of MB (GB, on the WAV fallback) behind
    /// permanently, and it was invisible in both directions: <see cref="MeasureUsage"/>
    /// and <see cref="Clear"/> each enumerated <c>*.dcstems</c> TOP-DIRECTORY ONLY,
    /// so the Settings ▸ Stem Models "Stem cache" row under-reported the real
    /// footprint and the "Clear" button could not reclaim it. The user had no
    /// in-app way to see the space, let alone free it.</para>
    /// <para>The age guard is what makes this safe to call from the save path
    /// itself: a concurrent save's own directory was written seconds ago.
    /// <b>This is not cache eviction</b> — nothing here touches a <c>.dcstems</c>
    /// archive, which is the artifact the never-regenerate invariant protects.</para>
    /// </summary>
    public int SweepAbandonedStaging()
    {
        var swept = 0;
        long bytes = 0;
        try
        {
            if (!Directory.Exists(_root)) return 0;
            foreach (var dir in EnumerateAbandonedStaging())
            {
                var size = TryMeasureDir(dir);
                try
                {
                    Directory.Delete(dir, recursive: true);
                    swept++;
                    bytes += size;
                }
                catch (Exception ex)
                {
                    _logger?.Warning(AppLogCategory.Audio,
                        $"Stem cache: could not remove abandoned staging dir '{Path.GetFileName(dir)}': {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.Warning(AppLogCategory.Audio, $"Stem cache staging sweep failed: {ex.Message}");
            return swept;
        }

        if (swept > 0)
        {
            _logger?.Info(AppLogCategory.Audio,
                $"Stem cache: removed {swept} abandoned staging dir(s), reclaiming {bytes:N0} bytes.");
        }

        return swept;
    }

    /// <summary>Staging directories older than <see cref="AbandonedStagingAge"/>.</summary>
    private IEnumerable<string> EnumerateAbandonedStaging()
    {
        var cutoff = _clock() - AbandonedStagingAge;
        foreach (var dir in Directory.EnumerateDirectories(_root, StagingPrefix + "*",
                     SearchOption.TopDirectoryOnly).ToList())
        {
            DateTime written;
            try { written = Directory.GetLastWriteTimeUtc(dir); }
            catch { continue; }
            if (written <= cutoff) yield return dir;
        }
    }

    /// <summary>Recursive byte total of a directory; 0 if it cannot be read.</summary>
    private static long TryMeasureDir(string dir)
    {
        try
        {
            long total = 0;
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(file).Length; }
                catch (IOException) { /* vanished between listing and stat */ }
                catch (UnauthorizedAccessException) { /* ditto */ }
            }
            return total;
        }
        catch { return 0; }
    }

    /// <summary>
    /// True when a valid archive already exists for <paramref name="key"/> (i.e.
    /// stems were generated before and must NOT be regenerated).
    /// </summary>
    public bool TryGetManifest(StemCacheKey key, out StemCacheManifest manifest)
    {
        var path = ArchivePath(key);
        if (File.Exists(path))
        {
            var read = StemArchive.ReadManifest(path);
            if (read != null && read.SchemaVersion <= StemCacheManifest.CurrentSchemaVersion
                             && KeyMatches(read, key))
            {
                manifest = read;
                return true;
            }
        }
        manifest = new StemCacheManifest();
        return false;
    }

    /// <summary>
    /// The manifest's own self-check, finally performed.
    /// <para>
    /// <see cref="StemCacheManifest.Key"/> is documented as "lets a reader
    /// confirm it opened the right archive" and no reader ever confirmed —
    /// <see cref="TryGetManifest"/> checked only <c>File.Exists</c> and the
    /// schema version. Everything therefore rested on the 128-bit
    /// <see cref="StemCacheKey.DirectoryToken"/> filename alone, so a token
    /// collision, a hand-copied cache directory, or an archive written under a
    /// degenerate key was served silently as the right song's stems.
    /// </para>
    /// <para>
    /// <b>An EMPTY recorded key is accepted.</b> This check is defence in depth,
    /// not a migration: rejecting a legacy archive that never wrote the field
    /// would re-separate a song the cache already holds, which is the exact
    /// outcome the never-regenerate invariant exists to prevent. Only a key that
    /// is present AND different is a miss.
    /// </para>
    /// </summary>
    private bool KeyMatches(StemCacheManifest manifest, StemCacheKey key)
    {
        // AcceptsRecordedKey also accepts the SUPERSEDED path-folding form of the
        // same own-chart key (same audio signature), which is what lets an adopted
        // archive keep the key it was written with — and, more importantly, what
        // lets it survive the NEXT move of its audio.
        if (key.AcceptsRecordedKey(manifest.Key)) return true;
        _logger?.Warning(AppLogCategory.Audio,
            $"Stem cache archive for '{key.DirectoryToken}' records a different key " +
            $"('{manifest.Key}' vs '{key.Canonical}'); treating it as a miss.");
        return false;
    }

    /// <summary>
    /// Encodes the freshly separated WAV stems, packages them (plus a manifest)
    /// into the archive, and writes it atomically. The codec is chosen by the
    /// encoder (Opus when ffmpeg is present, else WAV) and recorded in the
    /// manifest so the archive is portable.
    /// </summary>
    public async Task SaveAsync(
        StemCacheKey key,
        StemCacheIdentity identity,
        StemAudioSignature signature,
        IReadOnlyList<StemTrack> wavStems,
        string modelId,
        string appVersion,
        CancellationToken ct = default,
        string? sourceFullHash = null)
    {
        if (wavStems.Count == 0)
        {
            _logger?.Warning(AppLogCategory.Audio, "Stem cache save skipped: no stems to store.");
            return;
        }

        Directory.CreateDirectory(_root);
        // Every save is a chance to clear what an earlier hard exit stranded here.
        // Age-guarded, so it can never take a concurrent save's own scratch dir.
        SweepAbandonedStaging();
        var work = Path.Combine(_root, StagingPrefix + key.DirectoryToken + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var target = _encoder.PreferredCodec;
            var usable = wavStems.Where(s => !string.IsNullOrWhiteSpace(s.Path) && File.Exists(s.Path)).ToList();
            if (usable.Count == 0)
            {
                _logger?.Warning(AppLogCategory.Audio, "Stem cache save skipped: no encodable stems on disk.");
                return;
            }

            var entries = new List<(string EntryName, string SourceFile)>(usable.Count);
            var manifestStems = new List<StemManifestEntry>(usable.Count);
            var actualCodec = target;

            foreach (var stem in usable)
            {
                var destNoExt = Path.Combine(work, SafeName(stem.Name));
                var encoded = await _encoder.EncodeAsync(stem.Path, destNoExt, target, ct).ConfigureAwait(false);
                var producedExt = Path.GetExtension(encoded).TrimStart('.').ToLowerInvariant();
                if (!StemCodecExtensions.TryParse(producedExt, out var producedCodec))
                    producedCodec = StemCodec.Wav;
                // A single archive uses ONE codec so readers are unambiguous. If
                // any stem fell back to WAV (encoder ran but failed), demote the
                // whole archive to WAV and rebuild every entry from the original
                // WAV inputs below.
                if (producedCodec != target) actualCodec = StemCodec.Wav;

                var entryName = Path.GetFileName(encoded);
                entries.Add((entryName, encoded));
                manifestStems.Add(new StemManifestEntry { Name = stem.Name, FileName = entryName });
            }

            if (actualCodec != target)
            {
                entries.Clear();
                manifestStems.Clear();
                foreach (var stem in usable)
                {
                    var wavDest = Path.Combine(work, SafeName(stem.Name) + ".wav");
                    File.Copy(stem.Path, wavDest, overwrite: true);
                    var name = Path.GetFileName(wavDest);
                    entries.Add((name, wavDest));
                    manifestStems.Add(new StemManifestEntry { Name = stem.Name, FileName = name });
                }
            }

            var manifest = new StemCacheManifest
            {
                SchemaVersion = StemCacheManifest.CurrentSchemaVersion,
                Key = key.Canonical,
                LogicalId = identity.LogicalId,
                Codec = actualCodec,
                SourceBytes = signature.Bytes,
                SourceHash = signature.Sha256Hex,
                SourceFullHash = sourceFullHash,
                ModelId = modelId ?? string.Empty,
                AppVersion = appVersion ?? string.Empty,
                CreatedUtc = _clock(),
                Stems = manifestStems,
            };

            // A save may ADD to the cache or REPLACE an entry with an equal-or-better
            // one; it may never SHRINK one. `usable` above silently drops any stem
            // whose WAV has gone (a cleaned playback dir, a failed run, a file still
            // open in the mixer), so a six-stem archive could be atomically replaced
            // by a four-stem one — and because stems are never regenerated, the two
            // missing stems were gone for good, with an Info line reading like a
            // success. The archive on disk is the durable artifact; when it already
            // holds more than we are about to write, keep it.
            if (TryGetManifest(key, out var existing) && existing.Stems.Count > manifestStems.Count)
            {
                _logger?.Warning(AppLogCategory.Audio,
                    $"Stem cache save skipped: the archive for '{key.DirectoryToken}' already holds " +
                    $"{existing.Stems.Count} stem(s) and this save would leave only {manifestStems.Count} " +
                    $"({wavStems.Count - usable.Count} of {wavStems.Count} separated stem(s) were missing on disk). " +
                    "Keeping the existing archive.");
                return;
            }

            var tmpArchive = Path.Combine(work, "archive" + StemArchive.ArchiveExtension);
            StemArchive.Write(tmpArchive, manifest, entries);

            var finalPath = ArchivePath(key);
            Directory.CreateDirectory(_root);
            // Atomic overwrite (mirror WaveformDiskCache): no delete-then-move
            // window a concurrent open could see as a miss, and no crash gap
            // between the delete and the move that would lose the archive.
            AtomicFile.ReplaceWithRetry(tmpArchive, finalPath);

            _logger?.Info(AppLogCategory.Audio,
                $"Stem cache: stored {manifestStems.Count} stem(s) as {actualCodec} at '{finalPath}' ({new FileInfo(finalPath).Length:N0} bytes).");
        }
        finally
        {
            BestEffortDelete.Directory(work);
        }
    }

    /// <summary>
    /// Expands a cached archive into <paramref name="destDir"/> as WAV files
    /// JUCE can open, decoding from the stored codec on demand. Returns the
    /// resulting stem tracks. If the destination already holds a decoded set
    /// matching the manifest, extraction is skipped (re-open is instant).
    /// </summary>
    public async Task<IReadOnlyList<StemTrack>> ExtractAsync(StemCacheKey key, string destDir, CancellationToken ct = default)
    {
        var archivePath = ArchivePath(key);
        // Typed, because the coordinator's delete decision keys on the TYPE: this
        // is a genuine corruption signal (the manifest probe accepted the archive a
        // moment ago and it now fails to read), and an untyped InvalidOperationException
        // is indistinguishable from an environmental failure it must NOT delete for.
        var manifest = StemArchive.ReadManifest(archivePath)
                       ?? throw new StemArchiveCorruptException($"Stem cache archive missing/corrupt: '{archivePath}'.");

        Directory.CreateDirectory(destDir);

        // Fast path: already expanded (e.g. re-opening the same song this run).
        //
        // destDir is stable for a song (…/PsarcAudioCache/<song>/stems, or
        // <project>/stems) while the archive is keyed by AUDIO CONTENT. Matching
        // on the generic stem filenames alone would happily serve WAVs left
        // behind by different audio — swap a project's audio, separate, swap it
        // back, and the old stems would be handed to the mixer. Only take the
        // fast path when the marker says these WAVs came from THIS archive.
        var marker = Path.Combine(destDir, ExpandedMarkerFileName);
        var alreadyThere = manifest.Stems.Count > 0 &&
                           ReadMarker(marker) == key.DirectoryToken &&
                           manifest.Stems.All(s =>
                               File.Exists(Path.Combine(destDir, Path.GetFileNameWithoutExtension(s.FileName) + ".wav")));
        if (alreadyThere)
        {
            return manifest.Stems
                .Select(s => new StemTrack { Name = s.Name, Path = Path.Combine(destDir, Path.GetFileNameWithoutExtension(s.FileName) + ".wav") })
                .ToList();
        }

        var encodedDir = Path.Combine(destDir, ".encoded");
        var tracks = new List<StemTrack>(manifest.Stems.Count);
        try
        {
            var encodedFiles = StemArchive.ExtractStems(archivePath, encodedDir);
            // Keys are file names of real extracted archive entries, so
            // Path.GetFileName never returns null here (never called with null).
            var byName = encodedFiles.ToDictionary(p => Path.GetFileName(p)!, p => p, StringComparer.OrdinalIgnoreCase);

            // CONCURRENT, because this is the blocking half of opening a cached
            // song. Each stem is its own ffmpeg process writing its own distinct
            // wav, so the work was always independent — running it one at a time
            // just made the user wait for the SUM. Measured on the owner's real
            // cache (6 Opus stems): 25.8 s -> 2.2 s and 8.9 s -> 0.75 s.
            //
            // Capped by the same policy the separator uses, so a six-stem decode
            // cannot saturate the box and starve the audio callback. Results are
            // collected per index and appended in MANIFEST ORDER afterwards: the
            // mixer's channel order is the manifest's, and completion order is
            // not.
            var pending = new Task[manifest.Stems.Count];
            var decoded = new string?[manifest.Stems.Count];
            using (var slots = new SemaphoreSlim(
                       Math.Max(1, Math.Min(manifest.Stems.Count,
                           StemSeparationResourcePolicy.RecommendedWorkerThreads()))))
            {
                for (var i = 0; i < manifest.Stems.Count; i++)
                {
                    var index = i;
                    var stem = manifest.Stems[i];
                    if (!byName.TryGetValue(stem.FileName, out var encodedPath))
                    {
                        // The archive's central directory disagrees with its OWN
                        // manifest — the exact torn-write case StemArchive.Write's
                        // FlushFile exists to prevent, and a truncated payload can
                        // leave a readable manifest behind. Skipping the
                        // entry silently would report a cache HIT with
                        // fewer channels than the mixer expects, write the marker
                        // naming this archive so the fast path served the
                        // short set forever, and — because stems are never
                        // regenerated — lose a stem for good with nothing in
                        // the log. It is provably unusable, so it is corruption
                        // (StemCacheCoordinator deletes it and re-separates) rather
                        // than a decode outage (which keeps it).
                        throw new StemArchiveCorruptException(
                            $"Stem cache archive '{Path.GetFileName(archivePath)}' promises stem " +
                            $"'{stem.Name}' as entry '{stem.FileName}', which the archive does not contain " +
                            $"({byName.Count} of {manifest.Stems.Count} entries present).");
                    }

                    var wavPath = Path.Combine(destDir,
                        Path.GetFileNameWithoutExtension(stem.FileName) + ".wav");
                    pending[index] = Task.Run(async () =>
                    {
                        await slots.WaitAsync(ct).ConfigureAwait(false);
                        try
                        {
                            await _encoder.DecodeToWavAsync(encodedPath, manifest.Codec, wavPath, ct)
                                .ConfigureAwait(false);
                            decoded[index] = wavPath;
                        }
                        finally
                        {
                            slots.Release();
                        }
                    }, ct);
                }

                // WhenAll so every decode is awaited even when one throws — an
                // unawaited sibling would keep writing into a directory the
                // caller has already abandoned, and its failure would surface
                // later as an unobserved task fault.
                await Task.WhenAll(pending).ConfigureAwait(false);
            }

            for (var i = 0; i < manifest.Stems.Count; i++)
            {
                if (decoded[i] is { } path)
                    tracks.Add(new StemTrack { Name = manifest.Stems[i].Name, Path = path });
            }
        }
        finally
        {
            // Also runs when a decode throws (e.g. ffmpeg missing) so the
            // temporary Opus payloads never linger in the playback dir.
            BestEffortDelete.Directory(encodedDir);
        }

        WriteMarker(marker, key.DirectoryToken);
        return tracks;
    }

    /// <summary>Names which archive the loose WAVs in a playback dir came from,
    /// so a later fast path cannot serve stems belonging to different audio.</summary>
    internal const string ExpandedMarkerFileName = ".stemcache";

    private static string? ReadMarker(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void WriteMarker(string path, string token)
    {
        try
        {
            File.WriteAllText(path, token);
        }
        catch (IOException)
        {
            // Best effort: a missing marker only costs a re-decode next time.
        }
        catch (UnauthorizedAccessException)
        {
            // ditto
        }
    }

    /// <summary>What the cache currently occupies on disk.</summary>
    /// <param name="Songs">Number of cached `.dcstems` archives (one per song).</param>
    /// <param name="Bytes">Total size on disk — the archives PLUS any abandoned
    /// staging directories, which are cache bytes the user can reclaim and which
    /// this figure includes.</param>
    public readonly record struct StemCacheUsage(int Songs, long Bytes)
    {
        public static readonly StemCacheUsage Empty = new(0, 0);
    }

    /// <summary>
    /// Measures the cache. Cheap (a directory listing) and safe to call from a
    /// settings page.
    /// <para>
    /// <b>There is deliberately NO automatic cap, age eviction or LRU here, and
    /// adding one would be a bug.</b> Stems are NEVER regenerated — that is the
    /// subsystem's central invariant — so every byte in this folder represents
    /// minutes of separation the user has already paid for, on a song they may
    /// not even have open. Reclaiming it has to be an explicit, informed user
    /// action, which is what <see cref="Clear"/> plus this measurement give the
    /// Settings page. (Contrast the waveform cache, which is regenerable, where
    /// every eviction costs a re-scan and nothing else.)
    /// </para>
    /// </summary>
    public StemCacheUsage MeasureUsage()
    {
        try
        {
            if (!Directory.Exists(_root)) return StemCacheUsage.Empty;
            var count = 0;
            long bytes = 0;
            foreach (var file in Directory.EnumerateFiles(_root, "*" + StemArchive.ArchiveExtension,
                         SearchOption.TopDirectoryOnly))
            {
                try
                {
                    bytes += new FileInfo(file).Length;
                    count++;
                }
                catch (IOException) { /* vanished between listing and stat */ }
                catch (UnauthorizedAccessException) { /* ditto */ }
            }
            // Abandoned staging dirs are cache bytes too — omitting them made the
            // Settings row under-report by however much a hard exit stranded. They
            // are NOT counted as songs: they are scratch, not stems.
            foreach (var dir in EnumerateAbandonedStaging())
                bytes += TryMeasureDir(dir);

            return new StemCacheUsage(count, bytes);
        }
        catch (Exception ex)
        {
            _logger?.Warning(AppLogCategory.Audio, $"Stem cache measure failed: {ex.Message}");
            return StemCacheUsage.Empty;
        }
    }

    /// <summary>
    /// Deletes every cached archive and returns how many went. The ONLY bulk
    /// removal path, and it exists solely so the user can reclaim the space
    /// knowingly — nothing in the app may call it on its own.
    /// <para>
    /// Best-effort per file: an archive currently open (a song whose stems are
    /// being expanded right now) is skipped and left for the next attempt rather
    /// than failing the whole clear.
    /// </para>
    /// </summary>
    public int Clear()
    {
        var deleted = 0;
        try
        {
            if (!Directory.Exists(_root)) return 0;
            foreach (var file in Directory.EnumerateFiles(_root, "*" + StemArchive.ArchiveExtension,
                         SearchOption.TopDirectoryOnly).ToList())
            {
                try
                {
                    File.Delete(file);
                    deleted++;
                }
                catch (Exception ex)
                {
                    _logger?.Warning(AppLogCategory.Audio,
                        $"Stem cache clear skipped '{Path.GetFileName(file)}': {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.Warning(AppLogCategory.Audio, $"Stem cache clear failed: {ex.Message}");
        }
        // "Clear" is the user reclaiming the folder, so it has to reach the bytes
        // a hard exit stranded as well — otherwise the row keeps reporting space
        // the button cannot free. Same age guard: never a save that is running.
        var sweptStaging = SweepAbandonedStaging();

        if (deleted > 0 || sweptStaging > 0)
        {
            _logger?.Info(AppLogCategory.Audio,
                $"Stem cache cleared: {deleted} archive(s) and {sweptStaging} abandoned staging dir(s) " +
                $"deleted from '{_root}'.");
        }

        return deleted;
    }

    public void Delete(StemCacheKey key)
    {
        try
        {
            var path = ArchivePath(key);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger?.Warning(AppLogCategory.Audio, $"Stem cache delete failed: {ex.Message}");
        }
    }

    private static string SafeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "stem";
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name;
    }
}
