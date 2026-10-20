using System.Diagnostics;
using DotCharter.Services.Ai.Match;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using JuceSharp;
using JuceSharp.AudioIo;
using JuceSharp.Dsp;
using DotCharter.Tools.RigDataset.Separate;

namespace DotCharter.Tools.RigDataset;

/// <summary>
///     The dataset render: DI clip + sampled rig → wet clip + a
///     <c>RigDatasetRow</c>.
///     <para>
///         <b>Determinism is per CLIP, not per run.</b> Every decision for clip
///         <c>i</c> comes from <c>new Random(SeedFor(runSeed, i))</c> — which DI, which
///         rig, which augmentations, which bed window. Four threads therefore produce
///         the same corpus as one, a resumed run produces the same corpus as an
///         uninterrupted one, and a row can be re-rendered from its own
///         <c>Seed</c> years later without the rest of the manifest.
///     </para>
///     <para>
///         <b>One engine per PROCESS, and that is not a preference.</b>
///         <c>ProcessOffline</c> refuses while a device is open — so the engine is
///         opened on the JUCE <c>Null</c> device (<c>DCA_ENABLE_NULL_DEVICE=1</c>) and
///         immediately <c>Stop()</c>ped — and it is also not CONCURRENT, not even
///         across seats: four workers on one engine produced 1 clip and 199
///         <c>JuceAudioStatus.Device</c> refusals on the first smoke render
///         (2026-09-12). <c>--threads N</c> therefore spawns N child processes at
///         Idle priority, each rendering <c>index % N == shard</c>, and merges their
///         manifests at the end.
///     </para>
/// </summary>
public sealed class RenderCommand
{
    private readonly RenderOptions _o;
    private readonly IRigSampler _sampler = new RigSampler();
    private long _rendered;
    private long _rejected;

    /// <summary>How far under <see cref="Augmentation.TargetRmsDbfs" /> a stored clip
    /// may sit after its peak trim before the draw is rejected (the corpus's level
    /// band is the target +/-6 dB).</summary>
    private const float MaxUnderTargetDb = 6f;
    private long _separated;

    public RenderCommand(RenderOptions options) => _o = options;

    public async Task<int> RunAsync(CancellationToken ct)
    {
        // --- fan out -----------------------------------------------------------
        // `ProcessOffline` is NOT concurrent, not even across SEATS: four workers on
        // one engine produced one clip and 199 `JuceAudioStatus.Device` refusals
        // (measured 2026-09-12 on the first smoke render). The chunk loop takes the
        // chain's process barrier, and the engine refuses a second render while one
        // is in flight — which is also why the study says renders belong in a
        // separate process from the app. So --threads N means N CHILD PROCESSES,
        // each with its own device-less engine and its own stride of clip indices.
        if (_o.Shards <= 1 && _o.Threads > 1) return await FanOutAsync(ct).ConfigureAwait(false);

        var di = ClipSource.Scan(Path.Combine(_o.DiRoot));
        if (di.Count == 0)
        {
            Console.Error.WriteLine($"error: no DI clips under {_o.DiRoot}. Run fetch-di.py first.");
            return 2;
        }

        var beds = _o.BedsRoot is null ? null : ClipSource.Scan(_o.BedsRoot);
        if (beds is { Count: 0 }) beds = null;
        Console.WriteLine($"DI pool: {di.Count} clips under {_o.DiRoot}");
        Console.WriteLine(beds is null
            ? "Beds: none — mix-bleed and the bed half of separation are SKIPPED and flagged. "
            + $"Drop 48 kHz WAVs into {_o.BedsRoot ?? "<out>/../beds"} to enable them."
            : $"Beds: {beds.Count} files under {_o.BedsRoot}");

        Directory.CreateDirectory(_o.OutRoot);
        var ownFile = _o.Shards > 1
            ? Path.Combine(_o.OutRoot, DatasetManifest.ShardFileName(_o.Shard))
            : Path.Combine(_o.OutRoot, DatasetManifest.FileName);
        using var manifest = new DatasetManifest(ownFile,
            [Path.Combine(_o.OutRoot, DatasetManifest.FileName)]);
        var done = manifest.ExistingIndices;
        if (done.Count > 0) Console.WriteLine($"{Tag}Resuming: {done.Count} rows already on disk.");

        var todo = Enumerable.Range(0, _o.Clips)
            .Where(i => _o.Shards <= 1 || i % _o.Shards == _o.Shard)
            .Where(i => !done.Contains(i))
            .ToArray();
        if (todo.Length == 0) { Console.WriteLine($"{Tag}Nothing to do."); return 0; }

        var separator = OnnxGuitarSeparator.TryCreate(_o, Console.WriteLine);
        using var ownedSeparator = separator as IDisposable;
        if (_o.SeparateFraction > 0 && separator is null)
        {
            Console.WriteLine("WARNING: --separate-fraction was asked for but no stem model is available; "
                              + "those clips will be flagged 'separated:unavailable' and left unseparated.");
        }

        Environment.SetEnvironmentVariable("DCA_ENABLE_NULL_DEVICE", "1");
        using var engine = OpenDevicelessEngine(_o.SampleRate, _o.RequireNullDevice);
        engine.Stop(); // OFFLINE MEANS OFFLINE

        var sw = Stopwatch.StartNew();
        var chain = engine.GetDspChain(0);
        foreach (var index in todo)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var row = await RenderOneAsync(index, chain, di, beds, separator, ct).ConfigureAwait(false);
                if (row is null) _rejected++;
                else manifest.Add(index, row);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _rejected++;
                Console.Error.WriteLine($"{Tag}clip {index}: {ex.GetType().Name}: {ex.Message}");
            }

            if (++_rendered % _o.ProgressEvery == 0) Report(_rendered, todo.Length, sw, manifest);
        }

        manifest.Flush();
        Report(_rendered, todo.Length, sw, manifest);
        Console.WriteLine($"{Tag}Done. {manifest.Count} rows; {_rejected} rejected; {_separated} separated.");
        return 0;
    }

    private string Tag => _o.Shards > 1 ? $"[s{_o.Shard}] " : "";

    // ----------------------------------------------------------------- fan out

    private async Task<int> FanOutAsync(CancellationToken ct)
    {
        var shards = Math.Max(1, _o.Threads);
        var exe = Environment.ProcessPath!;
        var isDotnetHost = Path.GetFileNameWithoutExtension(exe)
            .Equals("dotnet", StringComparison.OrdinalIgnoreCase);
        var asm = System.Reflection.Assembly.GetEntryAssembly()!.Location;

        Console.WriteLine($"Fanning out over {shards} processes (ProcessOffline is not concurrent).");
        var children = new List<Process>(shards);
        for (var i = 0; i < shards; i++)
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
            if (isDotnetHost) psi.ArgumentList.Add(asm);
            foreach (var a in _o.ChildArgs) psi.ArgumentList.Add(a);
            psi.ArgumentList.Add("--shards");
            psi.ArgumentList.Add(shards.ToString());
            psi.ArgumentList.Add("--shard");
            psi.ArgumentList.Add(i.ToString());
            psi.ArgumentList.Add("--threads");
            psi.ArgumentList.Add("1");
            var p = Process.Start(psi)!;
            try { p.PriorityClass = ProcessPriorityClass.Idle; }
            catch (InvalidOperationException) { /* it already exited */ }

            children.Add(p);
        }

        using var reg = ct.Register(() =>
        {
            foreach (var c in children)
            {
                try { if (!c.HasExited) c.CloseMainWindow(); }
                catch (InvalidOperationException) { /* gone */ }
            }
        });

        foreach (var c in children) await c.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        var total = DatasetManifest.Merge(_o.OutRoot);
        Console.WriteLine($"Merged {shards} shards into "
                          + $"{Path.Combine(_o.OutRoot, DatasetManifest.FileName)}: {total} rows.");
        return children.Any(c => c.ExitCode != 0) ? 1 : 0;
    }

    private void Report(long n, int total, Stopwatch sw, DatasetManifest manifest)
    {
        var perMin = n / Math.Max(sw.Elapsed.TotalMinutes, 1e-9);
        var remain = TimeSpan.FromMinutes((total - n) / Math.Max(perMin, 1e-9));
        Console.WriteLine($"[{n}/{total}] {perMin:F1} clips/min · rejected {Interlocked.Read(ref _rejected)} "
                          + $"· eta {remain:d\\.hh\\:mm\\:ss} · manifest {manifest.Count}");
        manifest.Flush();
    }

    // --------------------------------------------------------------- one clip

    private async Task<RigDatasetRow?> RenderOneAsync(int index, JuceDspChain chain, ClipSource di,
        ClipSource? beds, IGuitarSeparator? separator, CancellationToken ct)
    {
        var frames = (int)(_o.Seconds * _o.SampleRate);

        // Up to three attempts, each with its OWN derived seed, so a rejected draw
        // does not consume the clip's identity: attempt 0 of clip i is always the
        // same rig, whatever happened on the run before.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var seed = SeedFor(_o.Seed, index, attempt);
            var rng = new Random(seed);

            var diIndex = rng.Next(di.Count);
            var diPath = di.Relative[diIndex];

            // --- the DI stage --------------------------------------------------
            // Only ~5.9 h of licence-clean DI exists, so the DI itself is augmented
            // BEFORE the rig: pitch, level and a pickup-position comb. Every one of
            // them leaves the rig's label exactly as true as it was, and every one is
            // recorded — a DI augmentation is invisible in the wet clip, so an
            // unrecorded one is an unexplained slice when the model underperforms.
            var semitones = _o.DiPitchMaxSemitones <= 0f
                ? 0d
                : (rng.NextDouble() * 2d - 1d) * _o.DiPitchMaxSemitones;
            var ratio = Math.Pow(2d, semitones / 12d);
            var sourceFrames = (int)Math.Ceiling(frames * Math.Max(1d, ratio)) + 4;
            var raw = di.Window(diPath, sourceFrames, rng);
            if (Augmentation.Rms(raw) < Augmentation.FromDb(-45f)) continue; // a silent DI window

            var rig = _sampler.Sample(rng, new RigSamplingProfile(AllowNamCaptures: _o.NamRoot is not null)
            {
                Kind = _o.Profile,
                NamCaptureDirectory = _o.NamRoot
            });

            var aug = new List<string>(12);

            var dry = new float[frames];
            aug.Add(Augmentation.PitchShiftInto(raw, dry, semitones));
            if (Augmentation.Rms(dry) < Augmentation.FromDb(-45f)) continue;

            // The DI pool is normalised HERE rather than on disk, so the pool stays
            // the bytes the dataset authors published and the normalisation is a
            // recorded, reproducible step.
            var dryWork = (float[])dry.Clone();
            Augmentation.Normalise(dryWork, new Random(seed ^ 0x5EED), Augmentation.TargetRmsDbfs, 0f);
            if (_o.DiLevelMaxDb > 0f) aug.Add(Augmentation.DiLevel(dryWork, rng, _o.DiLevelMaxDb));
            if (rng.NextDouble() < _o.PickupCombFraction)
                aug.Add(Augmentation.PickupComb(dryWork, _o.SampleRate, rng));

            var wet = RenderThroughRig(chain, rig, dryWork, frames);

            if (Augmentation.ClippedFraction(wet) > 0.20) { aug.Clear(); continue; }
            if (Augmentation.Rms(wet) < Augmentation.FromDb(-60f)) continue;

            aug.Add(Augmentation.Normalise(wet, rng, Augmentation.TargetRmsDbfs, _o.LevelJitterDb));
            if (Augmentation.IsIndistinguishableFrom(wet, dryWork)) continue;

            aug.Add(Augmentation.Tilt(wet, _o.SampleRate, rng, _o.TiltMaxDb));

            // --- bleed ---------------------------------------------------------
            float[]? bed = null;
            if (beds is not null && rng.NextDouble() < _o.BleedFraction)
            {
                var bedPath = beds.Relative[rng.Next(beds.Count)];
                bed = beds.Window(bedPath, frames, rng);
                aug.Add(Augmentation.Bleed(wet, bed, rng, _o.BleedMinDb, _o.BleedMaxDb));
                aug.Add($"bed:{bedPath}");
            }
            else if (beds is null)
            {
                aug.Add("bleed:skipped-no-beds");
            }

            // --- separation (stage B: the distribution inference actually sees) --
            if (rng.NextDouble() < _o.SeparateFraction)
            {
                if (separator is null)
                {
                    aug.Add("separated:unavailable");
                }
                else
                {
                    // Mixed with a bed if there is one — a separator asked to isolate
                    // a guitar from a guitar mostly passes it through, and the whole
                    // point of this stage is the ARTEFACTS.
                    bed ??= beds?.Window(beds.Relative[rng.Next(beds.Count)], frames, rng);
                    var mix = (float[])wet.Clone();
                    if (bed is not null) Augmentation.Bleed(mix, bed, rng, -12f, -3f);
                    var guitar = await separator.IsolateGuitarAsync(mix, _o.SampleRate, ct).ConfigureAwait(false);
                    if (guitar is not null)
                    {
                        Array.Copy(guitar, wet, Math.Min(guitar.Length, wet.Length));
                        aug.Add(bed is null ? "separated:htdemucs_6s:no-bed" : "separated:htdemucs_6s");
                        aug.Add(Augmentation.Normalise(wet, rng, Augmentation.TargetRmsDbfs, _o.LevelJitterDb));
                        Interlocked.Increment(ref _separated);
                    }
                    else
                    {
                        aug.Add("separated:failed");
                    }
                }
            }

            var trim = Augmentation.TrimPeak(wet);
            if (trim is not null) aug.Add(trim);
            if (!wet.All(float.IsFinite)) continue;
            // A render whose crest factor is so high that trimming its PEAK under
            // full scale drags its RMS more than 6 dB under the level target is a
            // clip the corpus cannot level: rejected like a clipped one, so every
            // stored clip sits at -18 dBFS +/-6 (RigDatasetRenderTests). Found when
            // a re-roll of the draws (2026-10, the amp/bass-head params of #70/#72)
            // landed a rat -> Acoustic Pre -> cab -> dimension -> shimmer rig at
            // -27 dBFS after a -9 dB trim.
            if (Augmentation.Db(Augmentation.Rms(wet)) < Augmentation.TargetRmsDbfs - MaxUnderTargetDb) continue;

            var clipRel = DatasetManifest.ClipPathFor(index);
            if (!_o.DryRun)
            {
                var full = Path.Combine(_o.OutRoot, clipRel.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                using var w = new WavWriter(full, _o.SampleRate, 1, WavSampleFormat.Pcm16);
                w.Write(wet);
            }

            return new RigDatasetRow(
                ClipPath: clipRel,
                DiPath: "di/" + diPath,
                Rig: rig,
                Augmentations: aug,
                SampleRate: _o.SampleRate,
                Seconds: _o.Seconds,
                Seed: seed);
        }

        return null;
    }

    /// <summary>
    ///     Renders mono DI through the rig and folds back to mono.
    ///     <para>
    ///         <b>The render is STEREO and the store is mono, deliberately.</b>
    ///         <c>ProcessOffline</c>'s <c>channels: 1</c> overload folds the rig's
    ///         OUTPUT down as well as duplicating its input, and it also allocates two
    ///         <c>float[frames]</c> where the stereo overload works in place — so the
    ///         DI is duplicated into L and R here, the stereo overload runs, and the
    ///         sum is taken afterwards. The corpus is mono because the INFERENCE input
    ///         is mono, and a mono sum is what a listener of a mono mix hears too; the
    ///         cost is that a purely-stereo effect (<c>autopan</c>, <c>midside</c>,
    ///         <c>widener</c>) partly cancels in the sum, which is why those three sit
    ///         at the bottom of the sampler's weight table.
    ///     </para>
    /// </summary>
    private float[] RenderThroughRig(JuceDspChain chain, DcRigPreset rig, float[] dryMono, int frames)
    {
        chain.Clear();
        foreach (var mod in rig.Modules)
        {
            var node = chain.AddNode(mod.ModuleTypeId);
            foreach (var (paramId, value) in mod.Params.OrderBy(p => p.Key, StringComparer.Ordinal))
                node.SetParam(paramId, value);
            if (mod.StringParams is null) continue;
            foreach (var (paramId, value) in mod.StringParams.OrderBy(p => p.Key, StringComparer.Ordinal))
                node.SetString(paramId, value);
        }

        var interleaved = new float[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            var v = i < dryMono.Length ? dryMono[i] : 0f;
            interleaved[2 * i] = v;
            interleaved[2 * i + 1] = v;
        }

        var outBuf = new float[interleaved.Length];
        chain.ProcessOffline(interleaved, channels: 2, _o.SampleRate, outBuf);

        var mono = new float[frames];
        for (var i = 0; i < frames; i++) mono[i] = 0.5f * (outBuf[2 * i] + outBuf[2 * i + 1]);
        return mono;
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>SplitMix64 over (run seed, clip index, attempt), folded to 32 bits.
    /// A plain <c>seed + index</c> would give adjacent clips adjacent
    /// <see cref="Random" /> states, and .NET's is good enough that that is only
    /// mildly bad — but "mildly correlated labels" is exactly the failure nobody
    /// finds.</summary>
    public static int SeedFor(int runSeed, int index, int attempt)
    {
        var z = (ulong)runSeed * 0x9E3779B97F4A7C15UL
                + (ulong)index * 0xBF58476D1CE4E5B9UL
                + (ulong)attempt * 0x94D049BB133111EBUL
                + 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        z ^= z >> 31;
        return (int)(z ^ (z >> 32));
    }

    public static JuceAudioEngine OpenDevicelessEngine(int sampleRate, bool requireNull = false)
    {
        try
        {
            return new JuceAudioEngine(new JuceEngineConfig
            {
                OutputTypeName = "Null",
                SampleRate = sampleRate,
                BufferFrames = 512,
                EnableConsoleLog = false
            });
        }
        catch (JuceAudioException ex)
        {
            if (requireNull) throw;
            Console.WriteLine($"Null device unavailable ({ex.Message}); opening the default device just to have an engine. "
                              + "It is stopped immediately — but if the owner is using the sound card, stop this run.");
            return new JuceAudioEngine(new JuceEngineConfig { EnableConsoleLog = false });
        }
    }
}
