using System.Diagnostics;
using System.Text.Json;
using DotCharter.Services.Ai.Match;
using JuceSharp.AudioIo;

namespace DotCharter.Tools.RigDataset.Separate;

/// <summary>
///     Stage B as its own pass over an existing corpus: mix a rendered clip INTO a
///     bed at a realistic guitar-to-mix level, separate it with the app's own
///     htdemucs, and keep the guitar stem as the training input under the ORIGINAL
///     rig label.
///     <para>
///         <b>This is the stage v1 never ran, and it is why the v1 model has never
///         seen the input it will be given.</b> <c>render.log</c> says
///         <c>0 separated</c> over 49,908 clips: the beds folder was empty, so there
///         was nothing to mix into, and the CUDA EP could not load, so there was
///         nothing to separate with. Every real target IS a separated stem — the
///         match lane gets its guitar from <c>StemIsolator</c> — so a corpus of clean
///         renders trains a model on a distribution that does not occur.
///     </para>
///     <para>
///         <b>Separated clips go to <c>clips-sep/</c>, never over the original.</b>
///         Overwriting in place would make a crash between the WAV write and the
///         manifest flush leave a separated clip whose row says it is clean — and the
///         resume would then separate it a SECOND time. A side path makes the whole
///         pass idempotent: the row either points at <c>clips/</c> or at
///         <c>clips-sep/</c>, and re-running can only redo work, never corrupt it.
///     </para>
///     <para>
///         <b>Selection is a property of the corpus, not of the run.</b> Row <c>i</c>
///         is in the separated set iff <c>Random(SeedFor(seed, i, 11)).NextDouble() &lt;
///         fraction</c>, so raising the fraction later only ADDS rows, four shards
///         select the same set as one, and a resumed pass picks up exactly where it
///         stopped.
///     </para>
/// </summary>
public sealed class SeparateCommand
{
    /// <summary>Attempt salt for the SELECTION draw. Distinct from the render's
    /// attempts (0–2) and from the bed draw, so no two decisions share a stream.</summary>
    public const int SelectionAttempt = 11;

    /// <summary>Attempt salt for the bed and level draws.</summary>
    public const int MixAttempt = 12;

    /// <summary>Where a separated clip lands, relative to the dataset root.</summary>
    public const string SeparatedDir = "clips-sep";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly SeparateOptions _o;
    private readonly Func<SeparateOptions, Action<string>, IGuitarSeparator?> _separatorFactory;

    /// <summary>Creates the command.</summary>
    /// <param name="options">Parsed flags.</param>
    /// <param name="separatorFactory">Injected so a test can drive the whole pass —
    /// selection, resume, level draw, manifest merge — over a fake separator. Null
    /// uses the real ONNX one.</param>
    public SeparateCommand(SeparateOptions options,
        Func<SeparateOptions, Action<string>, IGuitarSeparator?>? separatorFactory = null)
    {
        _o = options;
        _separatorFactory = separatorFactory ?? OnnxGuitarSeparator.TryCreate;
    }

    /// <summary>True when row <paramref name="index" /> is in the separated set.
    /// Pure, and the one place that decides.</summary>
    /// <param name="seed">The run seed.</param>
    /// <param name="index">The clip index.</param>
    /// <param name="fraction">0…1.</param>
    public static bool IsSelected(int seed, int index, double fraction)
    {
        if (fraction <= 0) return false;
        if (fraction >= 1) return true;
        return new Random(RenderCommand.SeedFor(seed, index, SelectionAttempt)).NextDouble() < fraction;
    }

    /// <summary>The path a separated clip is written to.</summary>
    /// <param name="index">The clip index.</param>
    public static string SeparatedPathFor(int index)
        => $"{SeparatedDir}/{index / 1000:D5}/{index:D8}.wav";

    /// <summary>Runs the pass.</summary>
    /// <param name="ct">Ctrl-C.</param>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        if (_o.Shards <= 1 && _o.Threads > 1) return await FanOutAsync(ct).ConfigureAwait(false);

        var manifestPath = Path.Combine(_o.Root, DatasetManifest.FileName);
        if (!File.Exists(manifestPath))
        {
            Console.Error.WriteLine($"error: no manifest at {manifestPath}. Render first.");
            return 2;
        }

        var rows = ReadRows(manifestPath);
        foreach (var shardFile in Directory.GetFiles(_o.Root, "manifest.shard*.jsonl"))
        {
            foreach (var (i, row) in ReadRows(shardFile))
                rows[i] = row; // a shard's view is newer than the merged manifest's
        }

        var todo = rows
            .Where(kv => _o.Shards <= 1 || kv.Key % _o.Shards == _o.Shard)
            .Where(kv => IsSelected(_o.Seed, kv.Key, _o.Fraction))
            .Where(kv => !Augmentation.IsSeparated(kv.Value.Augmentations))
            .OrderBy(kv => kv.Key)
            .ToArray();

        var selected = rows.Count(kv => IsSelected(_o.Seed, kv.Key, _o.Fraction));
        Console.WriteLine($"{Tag}{rows.Count} rows; {selected} selected at fraction {_o.Fraction:P0}; "
                          + $"{todo.Length} left for this shard.");
        if (_o.Limit > 0) todo = todo.Take(_o.Limit).ToArray();
        if (todo.Length == 0) return 0;

        var beds = _o.BedsRoot is null ? null : ClipSource.Scan(_o.BedsRoot);
        if (beds is { Count: 0 }) beds = null;
        Console.WriteLine(beds is null
            ? $"{Tag}WARNING: no beds under '{_o.BedsRoot ?? "<unset>"}' — every row will be "
              + "tagged ':no-bed' and stage B is worth a fraction of what it should be. Run `beds` first."
            : $"{Tag}Beds: {beds.Count} files under {_o.BedsRoot}");

        var sep = _o.DryRun ? null : _separatorFactory(_o, Console.WriteLine);
        using var owned = sep as IDisposable;
        if (!_o.DryRun && sep is null)
        {
            Console.Error.WriteLine($"{Tag}error: no separator could be created — refusing to write "
                                    + "rows tagged 'separated:unavailable' over a good corpus.");
            return 3;
        }

        var ownFile = Path.Combine(_o.Root, $"manifest.shardsep{_o.Shard}.jsonl");
        using var outManifest = new DatasetManifest(ownFile);
        var sw = Stopwatch.StartNew();
        var n = 0;
        var failed = 0;

        foreach (var (index, row) in todo)
        {
            if (ct.IsCancellationRequested) break;
            if (_o.StopFile is not null && File.Exists(_o.StopFile))
            {
                Console.WriteLine($"{Tag}stop file {_o.StopFile} present — stopping cleanly.");
                break;
            }

            try
            {
                var updated = await SeparateOneAsync(index, row, beds, sep, ct).ConfigureAwait(false);
                if (updated is null) { failed++; continue; }
                if (Augmentation.IsSeparated(updated.Augmentations)
                    && updated.Augmentations.Any(a => a.EndsWith("failed", StringComparison.Ordinal)))
                {
                    failed++;
                }

                outManifest.Add(index, updated);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                failed++;
                Console.Error.WriteLine($"{Tag}clip {index}: {ex.GetType().Name}: {ex.Message}");
            }

            if (++n % _o.ProgressEvery == 0)
            {
                var perMin = n / Math.Max(sw.Elapsed.TotalMinutes, 1e-9);
                var eta = TimeSpan.FromMinutes((todo.Length - n) / Math.Max(perMin, 1e-9));
                Console.WriteLine($"{Tag}[{n}/{todo.Length}] {perMin:F1} clips/min · failed {failed} "
                                  + $"· eta {eta:d\\.hh\\:mm\\:ss}");
                outManifest.Flush();
            }
        }

        outManifest.Flush();
        Console.WriteLine($"{Tag}Done. {n} rows in {sw.Elapsed:d\\.hh\\:mm\\:ss}; {failed} failed.");
        return 0;
    }

    private string Tag => _o.Shards > 1 ? $"[s{_o.Shard}] " : "";

    private async Task<RigDatasetRow?> SeparateOneAsync(int index, RigDatasetRow row, ClipSource? beds,
        IGuitarSeparator? separator, CancellationToken ct)
    {
        var clipPath = Path.Combine(_o.Root, row.ClipPath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(clipPath))
        {
            Console.Error.WriteLine($"{Tag}clip {index}: {row.ClipPath} is missing; skipped.");
            return null;
        }

        var rng = new Random(RenderCommand.SeedFor(_o.Seed, index, MixAttempt));
        var wav = WavIo.Read(clipPath);
        var mono = wav.Channels == 1 ? wav.Interleaved : wav.ToMono();
        var mix = (float[])mono.Clone();

        var tags = new List<string>(row.Augmentations);
        string? bedPath = null;
        if (beds is not null)
        {
            bedPath = beds.Relative[rng.Next(beds.Count)];
            var bed = beds.Window(bedPath, mix.Length, rng);
            tags.Add(Augmentation.MixIntoBed(mix, bed, rng, _o.MixMinDb, _o.MixMaxDb));
            tags.Add($"bed:{bedPath}");
        }

        if (_o.DryRun)
        {
            tags.Add($"{Augmentation.SeparatedPrefix}{_o.ModelName}:dry-run");
            return row with { Augmentations = tags };
        }

        var guitar = await separator!.IsolateGuitarAsync(mix, row.SampleRate, ct).ConfigureAwait(false);
        if (guitar is null || !guitar.Any(float.IsFinite))
        {
            tags.Add($"{Augmentation.SeparatedPrefix}failed");
            return row with { Augmentations = tags };
        }

        var outAudio = new float[mono.Length];
        Array.Copy(guitar, outAudio, Math.Min(guitar.Length, outAudio.Length));
        tags.Add($"{Augmentation.SeparatedPrefix}{_o.ModelName}{(bedPath is null ? ":no-bed" : "")}");
        tags.Add(Augmentation.Normalise(outAudio, rng, Augmentation.TargetRmsDbfs, _o.LevelJitterDb));
        var trim = Augmentation.TrimPeak(outAudio);
        if (trim is not null) tags.Add(trim);

        var rel = SeparatedPathFor(index);
        var full = Path.Combine(_o.Root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var tmp = full + ".tmp";
        using (var w = new WavWriter(tmp, row.SampleRate, 1, WavSampleFormat.Pcm16)) w.Write(outAudio);
        File.Move(tmp, full, overwrite: true);

        return row with { ClipPath = rel, Augmentations = tags };
    }

    // ---------------------------------------------------------------- fan out

    private async Task<int> FanOutAsync(CancellationToken ct)
    {
        var shards = Math.Max(1, _o.Threads);
        var exe = Environment.ProcessPath!;
        var isDotnetHost = Path.GetFileNameWithoutExtension(exe)
            .Equals("dotnet", StringComparison.OrdinalIgnoreCase);
        var asm = System.Reflection.Assembly.GetEntryAssembly()!.Location;

        Console.WriteLine($"Fanning out over {shards} processes.");
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

        foreach (var c in children) await c.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        var total = DatasetManifest.Merge(_o.Root);
        Console.WriteLine($"Merged into {Path.Combine(_o.Root, DatasetManifest.FileName)}: {total} rows.");
        return children.Any(c => c.ExitCode != 0) ? 1 : 0;
    }

    // ----------------------------------------------------------------- rows

    /// <summary>Reads a manifest into index → row. A line that will not parse is
    /// skipped rather than fatal: a manifest is written through a temp file and a
    /// rename, so a torn line means someone copied it mid-flush.</summary>
    /// <param name="path">The manifest or shard file.</param>
    public static Dictionary<int, RigDatasetRow> ReadRows(string path)
    {
        var outp = new Dictionary<int, RigDatasetRow>();
        if (!File.Exists(path)) return outp;
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            RigDatasetRow? row;
            try { row = JsonSerializer.Deserialize<RigDatasetRow>(line, Json); }
            catch (JsonException) { continue; }

            if (row is null) continue;
            var index = DatasetManifest.IndexOfClipPath(row.ClipPath);
            if (index >= 0) outp[index] = row;
        }

        return outp;
    }
}
