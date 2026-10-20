using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using DotCharter.Services.Ai.Match;
using DotCharter.Services.Ai.Match.Search;
using DotCharter.Services.Ai.Match.Similarity;

namespace DotCharter.Tools.RigDataset;

/// <summary>
///     <c>build-library</c>: renders the matcher's retrieval library for the
///     BUNDLED reference DI to a file the app can install as-is.
///     <para>
///         The bundled DI is generated (<see cref="ReferenceDi.Medley" />), the
///         sampler is seeded, and the extractor is deterministic — so the K-row
///         library for that DI is the same bytes on every machine, and rendering it
///         once here and publishing it (<c>rig-library-v1.zip</c>, next to the
///         estimator model) spares every user the ~2.8 h build. This command goes
///         through the same <see cref="RigLibraryBuilder" /> the app grows its own
///         cache with, so the file it writes IS the file the app would have
///         produced: same header, same rows, same order. RESUMABLE — rerun it and
///         it continues at the last complete row.
///     </para>
/// </summary>
public sealed class BuildLibraryCommand(BuildLibraryOptions o)
{
    /// <summary>Runs it. 0 on a complete library, 2 when the renderer is
    /// unavailable, 130 when stopped by Ctrl-C (the file is still resumable).</summary>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        if (o.BelowNormalPriority) TrySetBelowNormal();

        var medley = ReferenceDi.Medley();
        var fingerprint = RigLibrary.Fingerprint(medley);
        var extractor = new ToneFeatureExtractor();
        var options = new RigLibraryOptions(Count: o.Count, Seed: o.Seed);
        var fileName = RigLibrary.CacheFileName(extractor.Id, options.Count, options.Seed, fingerprint);
        var path = Directory.Exists(o.Out) || o.Out.EndsWith(Path.DirectorySeparatorChar) || o.Out.EndsWith('/')
            ? Path.Combine(o.Out, fileName)
            : o.Out;

        Console.WriteLine($"Reference DI: {ReferenceDi.Id} medley, {medley.Samples.Length} samples @ {medley.SampleRate} Hz, fingerprint {fingerprint}");
        Console.WriteLine($"Key: extractor {extractor.Id}, K {options.Count}, seed {options.Seed}");
        Console.WriteLine($"File: {Path.GetFullPath(path)}");
        if (!string.Equals(Path.GetFileName(path), fileName, StringComparison.Ordinal))
            Console.WriteLine($"  (the app's own name for this key is '{fileName}'; install it under that name)");

        Environment.SetEnvironmentVariable("DCA_ENABLE_NULL_DEVICE", "1");
        using var host = new NullDeviceRigHost();
        var renderer = new RigRenderer(host);
        if (!renderer.IsAvailable)
        {
            Console.Error.WriteLine($"error: the offline renderer is unavailable ({renderer.Describe()}).");
            return 2;
        }

        Console.WriteLine($"Renderer: {renderer.Describe()}");

        var builder = new RigLibraryBuilder(medley, new RigSampler(), renderer, extractor, options, path);
        Console.WriteLine(builder.ResumedFromDisk
            ? $"Resuming at {builder.Rows}/{builder.TargetCount} rows."
            : $"Starting a new file; {builder.TargetCount} rows to render.");

        var sw = Stopwatch.StartNew();
        var startRows = builder.Rows;
        var lastLine = "";
        var every = Math.Max(1, o.ProgressEvery);
        var lastPrinted = -1;
        var sink = new LineSink(line =>
        {
            lastLine = line;
            var rows = builder.Rows;
            if (rows - lastPrinted >= every || builder.IsComplete)
            {
                lastPrinted = rows;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {line}  ({sw.Elapsed:hh\\:mm\\:ss} elapsed)");
            }
        });

        try
        {
            // On the caller's thread: this process IS the background job, and the
            // whole point of BelowNormal is that the render inherits it.
            await builder.GrowAsync(o.Count, sink, ct, onCallerThread: true).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine($"Stopped at {builder.Rows}/{builder.TargetCount} rows after {sw.Elapsed:hh\\:mm\\:ss} " +
                              $"({builder.Rows - startRows} rendered this run). Rerun to resume.");
            return 130;
        }

        var rendered = builder.Rows - startRows;
        var per = rendered > 0 ? sw.Elapsed / rendered : TimeSpan.Zero;
        Console.WriteLine($"Done: {builder.Rows} rows in {sw.Elapsed:hh\\:mm\\:ss} this run " +
                          $"({rendered} rendered, {per.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture)} s each).");

        // Read it back the way the app will, and hash what is on disk.
        var back = RigLibrary.TryLoad(path, extractor.Id, options.Count, options.Seed, fingerprint);
        if (back is not { IsComplete: true })
        {
            Console.Error.WriteLine($"error: the file does not load back as a complete library " +
                                    $"(rows {back?.Count.ToString(CultureInfo.InvariantCulture) ?? "none"}).");
            return 3;
        }

        using var stream = File.OpenRead(path);
        var sha = Convert.ToHexStringLower(SHA256.HashData(stream));
        Console.WriteLine($"Verified: {back.Count}/{back.TargetCount} rows, {back.Entries[0].Features.Length} features each, " +
                          $"{new FileInfo(path).Length:N0} bytes, SHA-256 {sha}");
        Console.WriteLine($"Last status: {lastLine}");
        return 0;
    }

    private static void TrySetBelowNormal()
    {
        try
        {
            using var self = Process.GetCurrentProcess();
            self.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Console.WriteLine($"(could not lower the process priority: {ex.Message})");
        }
    }

    /// <summary>Reports on the calling thread; <see cref="Progress{T}" /> would
    /// post to a context this console has none of and reorder lines.</summary>
    private sealed class LineSink(Action<string> handler) : IProgress<string>
    {
        public void Report(string value) => handler(value);
    }
}

/// <summary>Flags for <see cref="BuildLibraryCommand" />.</summary>
/// <param name="Out">The file to write, or a directory to write the app's own file name into.</param>
/// <param name="Count">K. The app's default is 4,000.</param>
/// <param name="Seed">The sampler seed. The app's default is 20260912.</param>
/// <param name="ProgressEvery">Print a line every N rows.</param>
/// <param name="BelowNormalPriority">Drop the process to BelowNormal before rendering.</param>
public sealed record BuildLibraryOptions(
    string Out,
    int Count,
    int Seed,
    int ProgressEvery,
    bool BelowNormalPriority);
