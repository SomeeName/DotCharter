using System.Text;
using DotCharter.Services.Audio.StemSeparation;
using DotCharter.Services.Audio.StemSeparation.Cache;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Audio;

/// <summary>
/// Unit tests for the durable stem cache building blocks: content signature,
/// key derivation, manifest round-trip, codec fallback, archive read/write and
/// the store's save/hit/extract cycle.
/// </summary>
public sealed class StemCacheTests : IDisposable
{
    private readonly string _tmp;

    public StemCacheTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "DotCharterStemCacheTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose() => BestEffortDelete.Directory(_tmp);

    // ---- test doubles -------------------------------------------------------

    /// <summary>Encoder that "encodes" by copying the WAV to a codec-extension
    /// file and decodes by copying back — lets the store round-trip without
    /// ffmpeg while still exercising the codec plumbing / manifest recording.</summary>
    private sealed class FakeEncoder(StemCodec codec, bool external = true) : IStemEncoder
    {
        public StemCodec PreferredCodec => codec;
        public bool IsExternalEncoderAvailable => external;

        public Task<string> EncodeAsync(string wavPath, string destPathWithoutExtension, StemCodec target, CancellationToken ct = default)
        {
            var dest = destPathWithoutExtension + "." + target.FileExtension();
            File.Copy(wavPath, dest, overwrite: true);
            return Task.FromResult(dest);
        }

        public Task<string> DecodeToWavAsync(string encodedPath, StemCodec c, string destWavPath, CancellationToken ct = default)
        {
            File.Copy(encodedPath, destWavPath, overwrite: true);
            return Task.FromResult(destWavPath);
        }
    }

    /// <summary>
    ///     A save may ADD to the cache or replace an entry with an equal-or-better one;
    ///     it may never SHRINK one.
    ///     <para>
    ///         <c>SaveAsync</c>'s <c>usable</c> filter silently drops any stem whose WAV
    ///         has gone from disk (a cleaned playback dir, a failed run, a file still
    ///         open in the mixer), and then wrote the short set over the archive with
    ///         <c>AtomicFile.ReplaceWithRetry</c>. Stems are never regenerated, so the
    ///         dropped stems were gone for good — under an Info line that read like a
    ///         success.
    ///     </para>
    /// </summary>
    [Fact]
    public async Task A_save_that_would_shrink_an_existing_archive_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new StemCacheStore(new FakeEncoder(StemCodec.Opus), logger: null,
            rootOverride: Path.Combine(_tmp, "cache-shrink"));
        var id = StemCacheIdentity.ForLibrary("lib:SHRINK");
        var sig = new StemAudioSignature(10, "sh");
        var key = StemCacheKey.Derive(id, sig);

        var names = new[] { "drums", "bass", "other", "vocals" };
        var wavs = names
            .Select((n, i) => new StemTrack { Name = n, Path = WriteBytes(n + "-sh.wav", FakeWav(200, (byte)(i + 1))) })
            .ToArray();
        await store.SaveAsync(key, id, sig, wavs, "htdemucs-4s", "0.60.0", ct);
        Assert.True(store.TryGetManifest(key, out var full));
        Assert.Equal(4, full.Stems.Count);

        // A second run whose last two WAVs never made it to disk.
        File.Delete(wavs[2].Path);
        File.Delete(wavs[3].Path);
        await store.SaveAsync(key, id, sig, wavs, "htdemucs-4s", "0.60.0", ct);

        Assert.True(store.TryGetManifest(key, out var after));
        Assert.Equal(4, after.Stems.Count);
        var dest = Path.Combine(_tmp, "play-shrink");
        Assert.Equal(4, (await store.ExtractAsync(key, dest, ct)).Count);
    }

    /// <summary>
    ///     A manifest entry the archive does not actually contain is CORRUPTION, not a
    ///     shorter hit.
    ///     <para>
    ///         <c>ExtractAsync</c> used to skip such an entry with no log and no count
    ///         check, and the coordinator only rejects an EMPTY result — so a truncated
    ///         payload behind a readable manifest was served as a successful hit with
    ///         fewer channels than the mixer expects, the expanded-marker was written
    ///         naming that archive so the fast path served the short set on every
    ///         subsequent open, and the missing stem was lost for good.
    ///     </para>
    /// </summary>
    [Fact]
    public async Task An_archive_missing_a_stem_its_manifest_promises_is_reported_as_corrupt()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new StemCacheStore(new FakeEncoder(StemCodec.Opus), logger: null,
            rootOverride: Path.Combine(_tmp, "cache-torn"));
        var id = StemCacheIdentity.ForLibrary("lib:TORN");
        var sig = new StemAudioSignature(10, "to");
        var key = StemCacheKey.Derive(id, sig);

        var names = new[] { "drums", "bass", "vocals" };
        var wavs = names
            .Select((n, i) => new StemTrack { Name = n, Path = WriteBytes(n + "-to.wav", FakeWav(200, (byte)(i + 1))) })
            .ToArray();
        await store.SaveAsync(key, id, sig, wavs, "htdemucs-4s", "0.60.0", ct);

        // Drop one payload entry while leaving the manifest naming it — what a torn
        // write behind a flushed central directory leaves behind.
        var archive = store.ArchivePath(key);
        using (var zip = System.IO.Compression.ZipFile.Open(archive,
                   System.IO.Compression.ZipArchiveMode.Update))
        {
            var entry = zip.Entries.First(e => e.FullName.Contains("vocals", StringComparison.OrdinalIgnoreCase));
            entry.Delete();
        }

        var dest = Path.Combine(_tmp, "play-torn");
        var ex = await Assert.ThrowsAsync<StemArchiveCorruptException>(
            () => store.ExtractAsync(key, dest, ct));
        Assert.Contains("vocals", ex.Message, StringComparison.OrdinalIgnoreCase);

        // …and the fast-path marker was NOT written, so a retry cannot serve the
        // short set straight out of the playback directory.
        Assert.False(File.Exists(Path.Combine(dest, StemCacheStore.ExpandedMarkerFileName)));
    }

    /// <summary>
    ///     A decode that BLOCKS until the test releases it, so the extract's
    ///     concurrency is observed structurally rather than by racing a
    ///     stopwatch — the gate forbids a wall-clock assertion, and the first
    ///     cut of this double was one in disguise (it waited 5 s for its peers
    ///     and flaked the moment a dozen agents shared the box).
    ///     <para>Every decode announces that it started and then waits. The test
    ///     waits for the expected number of announcements, then releases them one
    ///     at a time, waiting for each to FINISH before releasing the next — so
    ///     completion order is exactly the release order, not "probably".</para>
    /// </summary>
    private sealed class BlockingDecodeEncoder : IStemEncoder
    {
        private readonly Dictionary<string, TaskCompletionSource> _release =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, TaskCompletionSource> _finished =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, TaskCompletionSource> _startedGates =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly HashSet<string> _running = new(StringComparer.OrdinalIgnoreCase);

        private readonly TaskCompletionSource _enoughStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly object _sync = new();
        private int _started;
        private int _live;
        private int _expectedStarts = int.MaxValue;

        public int PeakConcurrency;

        public StemCodec PreferredCodec => StemCodec.Opus;
        public bool IsExternalEncoderAvailable => true;

        /// <summary>Completes once <see cref="ExpectStarts" /> many decodes are
        /// simultaneously blocked. A store that decoded one at a time never gets
        /// here — which is the regression this exists to catch, and it surfaces
        /// as the awaiting side's timeout rather than as a hang.</summary>
        public Task EnoughStarted => _enoughStarted.Task;

        public void ExpectStarts(int count)
        {
            lock (_sync)
            {
                _expectedStarts = count;
                if (_started >= count) _enoughStarted.TrySetResult();
            }
        }

        public Task<string> EncodeAsync(string wavPath, string destPathWithoutExtension, StemCodec target,
            CancellationToken ct = default)
        {
            var dest = destPathWithoutExtension + "." + target.FileExtension();
            File.Copy(wavPath, dest, overwrite: true);
            return Task.FromResult(dest);
        }

        public async Task<string> DecodeToWavAsync(string encodedPath, StemCodec c, string destWavPath,
            CancellationToken ct = default)
        {
            var stem = Path.GetFileNameWithoutExtension(encodedPath);
            TaskCompletionSource release;
            lock (_sync)
            {
                release = Gate(_release, stem);
                _running.Add(stem);
                Gate(_startedGates, stem).TrySetResult();
                _started++;
                _live++;
                if (_live > PeakConcurrency) PeakConcurrency = _live;
                if (_started >= _expectedStarts) _enoughStarted.TrySetResult();
            }

            await release.Task.ConfigureAwait(false);

            File.Copy(encodedPath, destWavPath, overwrite: true);
            lock (_sync)
            {
                _live--;
                _running.Remove(stem);
                Gate(_finished, stem).TrySetResult();
            }

            return destWavPath;
        }

        /// <summary>
        ///     Finishes every stem, one at a time, always choosing the RUNNING stem
        ///     that comes LAST in <paramref name="manifestOrder" />, and returns the
        ///     order they finished in. It only ever releases a stem that has
        ///     started: below four decode slots the store cannot run every stem at
        ///     once, and releasing one still queued behind two blocked ones waits
        ///     forever. Which stems win the slots is a race, so the order is chosen
        ///     from what is actually running rather than fixed up front.
        /// </summary>
        public async Task<IReadOnlyList<string>> FinishRunningLatestFirstAsync(
            IReadOnlyList<string> manifestOrder, CancellationToken ct)
        {
            var completion = new List<string>();
            while (completion.Count < manifestOrder.Count)
            {
                string? next;
                Task[] notYetStarted;
                lock (_sync)
                {
                    next = manifestOrder.LastOrDefault(n => _running.Contains(n));
                    notYetStarted = manifestOrder
                        .Where(n => !completion.Contains(n, StringComparer.OrdinalIgnoreCase))
                        .Select(n => Gate(_startedGates, n).Task)
                        .ToArray();
                }

                if (next is null)
                {
                    await Task.WhenAny(notYetStarted).WaitAsync(TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
                    continue;
                }

                await ReleaseAndAwaitAsync(next, ct).ConfigureAwait(false);
                completion.Add(next);
            }

            return completion;
        }

        /// <summary>Lets <paramref name="stem" /> finish, and does not return
        /// until it has — so the caller decides the completion ORDER outright.</summary>
        public async Task ReleaseAndAwaitAsync(string stem, CancellationToken ct)
        {
            TaskCompletionSource release, finished;
            lock (_sync)
            {
                release = Gate(_release, stem);
                finished = Gate(_finished, stem);
            }

            release.TrySetResult();
            await finished.Task.WaitAsync(TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
        }

        private static TaskCompletionSource Gate(Dictionary<string, TaskCompletionSource> map, string stem)
        {
            if (!map.TryGetValue(stem, out var tcs))
                map[stem] = tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return tcs;
        }
    }

    private string WriteBytes(string name, byte[] bytes)
    {
        var p = Path.Combine(_tmp, name);
        File.WriteAllBytes(p, bytes);
        return p;
    }

    private static byte[] FakeWav(int dataLen, byte fill)
    {
        // Minimal but > 44 bytes so IsUsableAudioFile-style checks pass.
        var data = new byte[dataLen];
        for (var i = 0; i < dataLen; i++) data[i] = fill;
        var header = Encoding.ASCII.GetBytes("RIFFxxxxWAVEfmt \0\0\0\0dataxxxx");
        return header.Concat(data).ToArray();
    }

    // ---- StemAudioSignature -------------------------------------------------

    [Fact]
    public void Signature_is_deterministic_for_identical_content()
    {
        var a = WriteBytes("a.wav", FakeWav(2000, 0x11));
        var b = WriteBytes("b.wav", FakeWav(2000, 0x11));

        var sa = StemAudioSignature.ComputeFile(a);
        var sb = StemAudioSignature.ComputeFile(b);

        Assert.Equal(sa, sb);
        Assert.Equal(2028, sa.Bytes); // 28 header + 2000 data
        Assert.False(string.IsNullOrEmpty(sa.Sha256Hex));
    }

    [Fact]
    public void Signature_changes_when_content_changes()
    {
        var a = WriteBytes("a.wav", FakeWav(2000, 0x11));
        var b = WriteBytes("b.wav", FakeWav(2000, 0x22)); // same length, different bytes

        Assert.NotEqual(StemAudioSignature.ComputeFile(a), StemAudioSignature.ComputeFile(b));
    }

    [Fact]
    public void Signature_changes_when_length_changes()
    {
        var a = WriteBytes("a.wav", FakeWav(2000, 0x11));
        var b = WriteBytes("b.wav", FakeWav(2001, 0x11));

        Assert.NotEqual(StemAudioSignature.ComputeFile(a), StemAudioSignature.ComputeFile(b));
    }

    [Fact]
    public void Signature_large_file_is_deterministic_and_sampled()
    {
        // Larger than the full-hash threshold so the sampled path is exercised.
        var big = new byte[(int)StemAudioSignature.FullHashThresholdBytes + 500_000];
        new Random(7).NextBytes(big);
        var a = WriteBytes("big-a.bin", big);
        var b = WriteBytes("big-b.bin", (byte[])big.Clone());

        var sa = StemAudioSignature.ComputeFile(a);
        var sb = StemAudioSignature.ComputeFile(b);
        Assert.Equal(sa, sb);
        Assert.Equal(big.Length, sa.Bytes);
    }

    // ---- StemCacheKey -------------------------------------------------------

    [Fact]
    public void Key_is_stable_for_same_identity_and_signature()
    {
        var id = StemCacheIdentity.ForLibrary("lib:ABC|Chartey|1.2");
        var sig = new StemAudioSignature(1234, "deadbeef");

        var k1 = StemCacheKey.Derive(id, sig);
        var k2 = StemCacheKey.Derive(id, sig);

        Assert.Equal(k1, k2);
        Assert.Equal(k1.DirectoryToken, k2.DirectoryToken);
        Assert.Equal(32, k1.DirectoryToken.Length);
    }

    [Fact]
    public void Key_differs_when_signature_differs()
    {
        var id = StemCacheIdentity.ForLibrary("lib:ABC");
        var k1 = StemCacheKey.Derive(id, new StemAudioSignature(1, "aa"));
        var k2 = StemCacheKey.Derive(id, new StemAudioSignature(1, "bb"));
        Assert.NotEqual(k1.DirectoryToken, k2.DirectoryToken);
    }

    [Fact]
    public void Key_differs_between_library_and_file_identity()
    {
        var sig = new StemAudioSignature(1, "aa");
        var lib = StemCacheKey.Derive(StemCacheIdentity.ForLibrary("lib:ABC"), sig);
        var file = StemCacheKey.Derive(StemCacheIdentity.ForAudioFile(@"C:\songs\abc.wav"), sig);
        Assert.NotEqual(lib.DirectoryToken, file.DirectoryToken);
    }

    /// <summary>
    /// Path normalization no longer affects the CURRENT key — an own-chart key is
    /// path-independent (StemCacheKeyPathIndependenceTests), which is what stopped
    /// a rename re-separating byte-identical audio. It still decides which archive
    /// the migration adopts, so the case/slash folding is asserted where it is
    /// still load-bearing: an archive an earlier build filed under one spelling of
    /// the path must be found from another.
    /// </summary>
    [Fact]
    public void The_superseded_path_key_is_case_and_slash_insensitive()
    {
        var sig = new StemAudioSignature(1, "aa");
        var k1 = StemCacheKey.TryDeriveSuperseded(StemCacheIdentity.ForAudioFile(@"C:\Songs\ABC.wav"), sig);
        var k2 = StemCacheKey.TryDeriveSuperseded(StemCacheIdentity.ForAudioFile(@"c:/songs/abc.wav"), sig);
        Assert.Equal(k1!.DirectoryToken, k2!.DirectoryToken);
    }

    /// <summary>The single guard BOTH caches now key through. It refuses the two
    /// degenerate inputs outright, so neither the lookup nor the save can create a
    /// keyless entry — the read side would otherwise miss the real archive and
    /// re-separate an already-cached song, and the save side would leave a duplicate
    /// archive behind that only a future unreadable-audio moment ever hits.</summary>
    [Fact]
    public void TryDerive_refuses_a_signature_the_audio_could_not_produce()
    {
        var id = StemCacheIdentity.ForLibrary("lib:ABC");
        Assert.Null(StemCacheKey.TryDerive(id, StemAudioSignature.None));
        Assert.Null(StemCacheKey.TryDerive(id, default));

        // A real signature still keys, byte-identical to the plain derivation.
        var sig = new StemAudioSignature(1234, "abcd");
        Assert.Equal(StemCacheKey.Derive(id, sig), StemCacheKey.TryDerive(id, sig));
    }

    /// <summary>An identity that names NOTHING — neither a logical id nor a source
    /// label. Under the old path-folding basis it collapsed to the literal "file:",
    /// which together with an unreadable signature was the universal key
    /// "file:|b=0|h=" and every such song shared one archive. The basis no longer
    /// carries the path, so that collision is gone by construction — but a call
    /// site that has lost track of what it is caching still must not cache.</summary>
    [Fact]
    public void TryDerive_refuses_an_identity_that_names_nothing()
    {
        var nameless = StemCacheIdentity.ForLibrary("");
        Assert.Null(nameless.LogicalId);
        Assert.Equal(string.Empty, nameless.SourceLabel);

        Assert.Null(StemCacheKey.TryDerive(nameless, new StemAudioSignature(1234, "abcd")));
        Assert.Null(StemCacheKey.TryDerive(StemCacheIdentity.ForAudioFile(""), new StemAudioSignature(1234, "abcd")));
    }

    // ---- StemCacheManifest --------------------------------------------------

    [Fact]
    public void Manifest_round_trips_through_json_with_codec_as_string()
    {
        var m = new StemCacheManifest
        {
            Key = "lib:ABC|b=1|h=xx",
            LogicalId = "lib:ABC",
            Codec = StemCodec.Opus,
            SourceBytes = 42,
            SourceHash = "xx",
            ModelId = "htdemucs-4s",
            AppVersion = "0.13.0",
            CreatedUtc = new DateTime(2026, 7, 9, 0, 0, 0, DateTimeKind.Utc),
            Stems = { new StemManifestEntry { Name = "drums", FileName = "drums.opus" } },
        };

        var json = m.ToJson();
        Assert.Contains("\"Opus\"", json); // enum serialized as string, not number

        var back = StemCacheManifest.FromJson(json);
        Assert.NotNull(back);
        Assert.Equal(StemCodec.Opus, back!.Codec);
        Assert.Equal("htdemucs-4s", back.ModelId);
        Assert.Single(back.Stems);
        Assert.Equal("drums.opus", back.Stems[0].FileName);
    }

    [Fact]
    public void Manifest_from_garbage_returns_null() => Assert.Null(StemCacheManifest.FromJson("{ not json"));

    // ---- FfmpegStemEncoder (no external encoder) ---------------------------

    [Fact]
    public async Task Encoder_without_ffmpeg_falls_back_to_wav()
    {
        var enc = new FfmpegStemEncoder(logger: null, ffmpegResolver: () => null);
        Assert.False(enc.IsExternalEncoderAvailable);
        Assert.Equal(StemCodec.Wav, enc.PreferredCodec);

        var wav = WriteBytes("in.wav", FakeWav(100, 1));
        var outPath = await enc.EncodeAsync(wav, Path.Combine(_tmp, "out"), StemCodec.Opus, TestContext.Current.CancellationToken);
        Assert.EndsWith(".wav", outPath); // demoted to WAV
        Assert.True(File.Exists(outPath));
    }

    [Fact]
    public async Task Encoder_reports_opus_when_ffmpeg_resolver_present()
    {
        // Fake ffmpeg: "encode" by writing the destination arg (last token) as a copy.
        var enc = new FfmpegStemEncoder(
            logger: null,
            ffmpegResolver: () => "ffmpeg",
            runner: (_, args, _) =>
            {
                var dest = args[^1];
                File.WriteAllBytes(dest, new byte[256]);
                return Task.FromResult(true);
            });
        Assert.True(enc.IsExternalEncoderAvailable);
        Assert.Equal(StemCodec.Opus, enc.PreferredCodec);

        var wav = WriteBytes("in2.wav", FakeWav(100, 1));
        var outPath = await enc.EncodeAsync(wav, Path.Combine(_tmp, "out2"), StemCodec.Opus, TestContext.Current.CancellationToken);
        Assert.EndsWith(".opus", outPath);
        Assert.True(File.Exists(outPath));
    }

    /// <summary>Opus is 48 kHz by definition, but ffmpeg honours the OpusHead's
    /// remembered input rate and hands back a 44.1 kHz WAV for a CD-rate song, which
    /// the engine then upsamples with the load-time sinc: 6.1 s per 183 s stem
    /// against 51 ms for a 48 kHz WAV (measured 2026-08-29). The decode asks for the
    /// native rate.</summary>
    [Fact]
    public async Task Opus_decode_asks_ffmpeg_for_the_codecs_native_48_kHz()
    {
        IReadOnlyList<string>? seen = null;
        var enc = new FfmpegStemEncoder(
            logger: null,
            ffmpegResolver: () => "ffmpeg",
            runner: (_, args, _) =>
            {
                seen = args;
                File.WriteAllBytes(args[^1], new byte[256]);
                return Task.FromResult(true);
            });
        var opus = WriteBytes("in3.opus", new byte[64]);
        await enc.DecodeToWavAsync(opus, StemCodec.Opus, Path.Combine(_tmp, "out3.wav"), TestContext.Current.CancellationToken);
        Assert.NotNull(seen);
        var ar = seen!.ToList().IndexOf("-ar");
        Assert.True(ar >= 0 && seen[ar + 1] == FfmpegStemEncoder.OpusNativeSampleRate,
            "the Opus decode must pin -ar 48000, or every stem comes back at the song's rate and pays the load-time sinc: " + string.Join(" ", seen));
        Assert.True(ar < seen.Count - 2, "-ar must precede the output path (it is an output option)");
    }

    /// <summary>The WAV branch of <see cref="FfmpegStemEncoder.DecodeToWavAsync"/>
    /// is a plain File.Copy, and it must report an environmental failure with the
    /// SAME typed exception the Opus branch uses. Unwrapped, a locked destination
    /// (the previous session's stem WAV still open in the JUCE mixer) or a full disk
    /// surfaced as a bare IOException, which the cache coordinator read as
    /// corruption and answered by deleting the never-regenerate archive — and a WAV
    /// .dcstems is exactly what a fresh install without ffmpeg produces, so that
    /// configuration had no protection at all.</summary>
    [Fact]
    public async Task Wav_decode_onto_a_locked_destination_is_a_decode_outage_not_a_raw_IOException()
    {
        var enc = new FfmpegStemEncoder(logger: null, ffmpegResolver: () => null);
        var src = WriteBytes("cached.wav", FakeWav(100, 1));
        var dest = WriteBytes("locked.wav", FakeWav(10, 2));

        // Hold the destination open for exclusive write, as the mixer would.
        using var hold = new FileStream(dest, FileMode.Open, FileAccess.Write, FileShare.None);

        await Assert.ThrowsAsync<StemDecodeUnavailableException>(
            () => enc.DecodeToWavAsync(src, StemCodec.Wav, dest, TestContext.Current.CancellationToken));
    }

    // ---- StemArchive --------------------------------------------------------

    [Fact]
    public void Archive_write_read_extract_round_trip()
    {
        var stem1 = WriteBytes("drums.opus", [1, 2, 3, 4]);
        var stem2 = WriteBytes("bass.opus", [5, 6, 7, 8]);
        var manifest = new StemCacheManifest
        {
            Codec = StemCodec.Opus,
            Stems =
            {
                new StemManifestEntry { Name = "drums", FileName = "drums.opus" },
                new StemManifestEntry { Name = "bass", FileName = "bass.opus" },
            },
        };
        var archive = Path.Combine(_tmp, "song.dcstems");

        StemArchive.Write(archive, manifest,
            new[] { ("drums.opus", stem1), ("bass.opus", stem2) });

        Assert.True(File.Exists(archive));
        var read = StemArchive.ReadManifest(archive);
        Assert.NotNull(read);
        Assert.Equal(2, read!.Stems.Count);

        var dest = Path.Combine(_tmp, "extracted");
        var files = StemArchive.ExtractStems(archive, dest);
        Assert.Equal(2, files.Count);
        Assert.DoesNotContain(files, f => Path.GetFileName(f) == StemArchive.ManifestEntryName);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(Path.Combine(dest, "drums.opus")));
    }

    // ---- StemCacheStore -----------------------------------------------------

    [Fact]
    public async Task Store_save_then_manifest_hit_then_extract()
    {
        var store = new StemCacheStore(new FakeEncoder(StemCodec.Opus), logger: null, rootOverride: Path.Combine(_tmp, "cache"));
        var id = StemCacheIdentity.ForLibrary("lib:ABC");
        var sig = new StemAudioSignature(10, "hh");
        var key = StemCacheKey.Derive(id, sig);

        Assert.False(store.TryGetManifest(key, out _)); // miss before save

        var wavs = new[]
        {
            new StemTrack { Name = "drums", Path = WriteBytes("d.wav", FakeWav(200, 3)) },
            new StemTrack { Name = "bass", Path = WriteBytes("b.wav", FakeWav(200, 4)) },
        };
        await store.SaveAsync(key, id, sig, wavs, "htdemucs-4s", "0.13.0", TestContext.Current.CancellationToken);

        Assert.True(store.TryGetManifest(key, out var manifest));
        Assert.Equal(StemCodec.Opus, manifest.Codec);
        Assert.Equal("htdemucs-4s", manifest.ModelId);
        Assert.True(File.Exists(store.ArchivePath(key)));

        var dest = Path.Combine(_tmp, "play");
        var extracted = await store.ExtractAsync(key, dest, TestContext.Current.CancellationToken);
        Assert.Equal(2, extracted.Count);
        Assert.All(extracted, t => Assert.True(File.Exists(t.Path)));
        Assert.All(extracted, t => Assert.EndsWith(".wav", t.Path));
    }

    /// <summary>
    ///     The cache-hit decode runs the stems CONCURRENTLY (0.49 — one ffmpeg
    ///     per stem, sequentially, made opening a cached song cost the SUM:
    ///     25.8 s instead of 2.2 s on the owner's real 6-stem archive). Two
    ///     things must survive that, and this encoder forces both: it finishes
    ///     decodes in REVERSE manifest order, and it records the peak number
    ///     running at once.
    ///     <para>Returned order must still be the MANIFEST's — the mixer's
    ///     channel order is the manifest's, and completion order is not.</para>
    /// </summary>
    [Fact]
    public async Task Store_extracts_stems_concurrently_and_returns_them_in_manifest_order()
    {
        var ct = TestContext.Current.CancellationToken;
        var names = new[] { "drums", "bass", "other", "vocals" };
        var encoder = new BlockingDecodeEncoder();
        var store = new StemCacheStore(encoder, logger: null,
            rootOverride: Path.Combine(_tmp, "cache-order"));

        var id = StemCacheIdentity.ForLibrary("lib:ORDER");
        var sig = new StemAudioSignature(10, "oo");
        var key = StemCacheKey.Derive(id, sig);
        var wavs = names
            .Select((n, i) => new StemTrack { Name = n, Path = WriteBytes(n + ".wav", FakeWav(200, (byte)(i + 1))) })
            .ToArray();
        await store.SaveAsync(key, id, sig, wavs, "htdemucs-4s", "0.49.0", ct);

        // The store caps its fan-out with the separator's own resource policy,
        // which returns 1 on a one/two-core machine BY DESIGN. Expect what the
        // policy permits, so the test asserts the store's contract rather than
        // the size of the box.
        var cap = Math.Min(names.Length, StemSeparationResourcePolicy.RecommendedWorkerThreads());
        encoder.ExpectStarts(cap);

        var dest = Path.Combine(_tmp, "play-order");
        var extract = store.ExtractAsync(key, dest, ct);

        // A store that decoded one at a time never reaches `cap` starts, so this
        // is where a serialization regression fails — deterministically, on a
        // rendezvous, not on a duration.
        await encoder.EnoughStarted.WaitAsync(TimeSpan.FromMinutes(2), ct);
        Assert.Equal(cap, encoder.PeakConcurrency);

        // Finish them latest-in-the-manifest first, each fully before the next,
        // so completion order runs against the order the result must come back
        // in. A collect-as-they-complete implementation returns the completion
        // order.
        var completion = await encoder.FinishRunningLatestFirstAsync(names, ct);

        var extracted = await extract;
        Assert.Equal(names, extracted.Select(t => t.Name).ToArray());
        if (cap > 1)
            Assert.NotEqual(names, completion);
    }

    /// <summary>The playback dir is stable per song while the archive is keyed by
    /// audio CONTENT. Loose WAVs left behind by different audio must never be
    /// mistaken for this archive's expansion.</summary>
    [Fact]
    public async Task Store_does_not_serve_stale_wavs_left_by_different_audio()
    {
        var store = new StemCacheStore(new FakeEncoder(StemCodec.Opus), logger: null,
            rootOverride: Path.Combine(_tmp, "cache-stale"));
        var id = StemCacheIdentity.ForAudioFile(@"C:\songs\swap.wav");

        // Audio A is separated and cached; its stems are expanded into the dir.
        var keyA = StemCacheKey.Derive(id, new StemAudioSignature(10, "aaaa"));
        var wavsA = new[] { new StemTrack { Name = "drums", Path = WriteBytes("da.wav", FakeWav(200, 3)) } };
        await store.SaveAsync(keyA, id, new StemAudioSignature(10, "aaaa"), wavsA, "", "",
            TestContext.Current.CancellationToken);

        var dest = Path.Combine(_tmp, "play-stale");
        var fromA = await store.ExtractAsync(keyA, dest, TestContext.Current.CancellationToken);
        var drumsPath = Assert.Single(fromA).Path;
        var bytesFromA = await File.ReadAllBytesAsync(drumsPath, TestContext.Current.CancellationToken);

        // The user swaps in audio B (same song identity, different content) and
        // separates it. Its archive holds DIFFERENT drums, but A's drums.wav is
        // still sitting in the playback dir.
        var keyB = StemCacheKey.Derive(id, new StemAudioSignature(20, "bbbb"));
        var wavsB = new[] { new StemTrack { Name = "drums", Path = WriteBytes("db.wav", FakeWav(400, 9)) } };
        await store.SaveAsync(keyB, id, new StemAudioSignature(20, "bbbb"), wavsB, "", "",
            TestContext.Current.CancellationToken);
        Assert.NotEqual(keyA.DirectoryToken, keyB.DirectoryToken);
        Assert.True(File.Exists(drumsPath), "precondition: audio A's stems still occupy the playback dir");

        var fromB = await store.ExtractAsync(keyB, dest, TestContext.Current.CancellationToken);

        var bytesFromB = await File.ReadAllBytesAsync(Assert.Single(fromB).Path, TestContext.Current.CancellationToken);
        Assert.NotEqual(bytesFromA, bytesFromB); // B's archive was decoded, not A's leftovers
    }

    [Fact]
    public async Task Store_reuses_expanded_wavs_for_the_same_archive()
    {
        var store = new StemCacheStore(new FakeEncoder(StemCodec.Opus), logger: null,
            rootOverride: Path.Combine(_tmp, "cache-reuse"));
        var id = StemCacheIdentity.ForLibrary("lib:REUSE");
        var sig = new StemAudioSignature(10, "hh");
        var key = StemCacheKey.Derive(id, sig);
        var wavs = new[] { new StemTrack { Name = "drums", Path = WriteBytes("dr.wav", FakeWav(200, 3)) } };
        await store.SaveAsync(key, id, sig, wavs, "", "", TestContext.Current.CancellationToken);

        var dest = Path.Combine(_tmp, "play-reuse");
        await store.ExtractAsync(key, dest, TestContext.Current.CancellationToken);
        var marker = Path.Combine(dest, StemCacheStore.ExpandedMarkerFileName);
        Assert.True(File.Exists(marker));
        Assert.Equal(key.DirectoryToken, (await File.ReadAllTextAsync(marker, TestContext.Current.CancellationToken)).Trim());

        // Second expansion of the SAME archive takes the fast path (no re-decode).
        var again = await store.ExtractAsync(key, dest, TestContext.Current.CancellationToken);
        Assert.Single(again);
    }

    [Fact]
    public async Task Store_records_wav_codec_when_encoder_has_no_ffmpeg()
    {
        var store = new StemCacheStore(new FakeEncoder(StemCodec.Wav, external: false), logger: null, rootOverride: Path.Combine(_tmp, "cache2"));
        var id = StemCacheIdentity.ForAudioFile(@"C:\songs\x.wav");
        var sig = new StemAudioSignature(10, "hh");
        var key = StemCacheKey.Derive(id, sig);

        var wavs = new[] { new StemTrack { Name = "vocals", Path = WriteBytes("v.wav", FakeWav(200, 9)) } };
        await store.SaveAsync(key, id, sig, wavs, "", "", TestContext.Current.CancellationToken);

        Assert.True(store.TryGetManifest(key, out var manifest));
        Assert.Equal(StemCodec.Wav, manifest.Codec); // portable: reader learns codec
    }

    // ---- StemSeparationResourcePolicy --------------------------------------
    // Half-the-machine since 2026-07-18 (the old count-2 rule ran 14 inference
    // threads on 16 cores and starved playback — the owner's stutter report).
    // The full contract lives in StemSeparationResourcePolicyTests.

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(4, 2)]
    [InlineData(8, 4)]
    [InlineData(16, 8)]
    public void Resource_policy_uses_at_most_half_the_machine(int cores, int expected) => Assert.Equal(expected, StemSeparationResourcePolicy.RecommendedWorkerThreads(cores));
}
