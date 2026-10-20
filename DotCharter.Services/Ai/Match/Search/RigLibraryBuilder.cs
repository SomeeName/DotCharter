using System.Text.Json;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Services.Ai.Match.Search;

/// <summary>
///     Grows one library FILE towards its K, a row at a time, and can stop and pick
///     up again at any row.
///     <para>
///         <b>Why a file that grows and not a file that is written once.</b> The
///         full library is ~2.8 hours of offline rendering (4,000 rigs × 20 s at the
///         measured 2.55 s a render). A user who opened "Match from audio" for the
///         first time would otherwise watch "1/4000" with nothing else on the line, and the
///         only way to have a library at all would be to sit through it. So the FIRST
///         match waits for <see cref="RigLibraryOptions.FirstMatchCount" /> rows, and
///         the rest arrive in the background over the following hours, across app
///         restarts — which needs a file that is complete after every row and a
///         builder that knows where it left off.
///     </para>
///     <para>
///         <b>Row <c>i</c> is the <c>i</c>-th draw from <c>Random(Seed)</c>, whoever
///         renders it.</b> That is what makes a resumed file the same library as a
///         one-shot one, and what lets a SHIPPED library (rendered once, on the
///         owner's machine) stand in for a local build: for the same extractor, K,
///         seed and DI the rows are identical. Resuming re-draws the rows already on
///         disk to advance the generator, and checks the last re-drawn rig against
///         the stored one; a mismatch (a changed sampler, a changed catalogue) means
///         the file is not this library and it is started over rather than mixed.
///     </para>
///     <para>
///         <b>Every append is durable on its own.</b> The row is written after a
///         truncate to the last complete row, closed with a marker and flushed, so a
///         crash at any point leaves a file whose loader takes every complete row
///         and drops the torn one.
///     </para>
///     <para>
///         Not thread-safe for two concurrent <c>Grow</c> calls — there is one
///         generator and one file. <see cref="Snapshot" /> IS safe to call from
///         another thread while a grow runs, which is how a match reads what the
///         background has produced so far.
///     </para>
/// </summary>
public sealed class RigLibraryBuilder
{
    private readonly MonoAudio _medley;
    private readonly IRigSampler _sampler;
    private readonly IRigRenderer _renderer;
    private readonly IToneFeatureExtractor _extractor;
    private readonly RigLibraryOptions _options;
    private readonly RigSamplingProfile _profile;
    private readonly IAppLogger? _logger;
    private readonly Lock _gate = new();
    private readonly List<RigLibraryEntry> _entries = [];
    private Random _rng;
    private long _validEnd;
    private int _renders;

    /// <summary>Opens (or creates) the library file at <paramref name="path" /> for
    /// this key and positions the generator after whatever rows it already holds.</summary>
    /// <param name="medley">The reference DI every row is rendered over.</param>
    /// <param name="sampler">Draws the rigs.</param>
    /// <param name="renderer">Renders them. An <see cref="ISynchronousRigRenderer" />
    /// is rendered on the grower's own thread when asked to.</param>
    /// <param name="extractor">Embeds the renders.</param>
    /// <param name="options">K, the seed, the profile.</param>
    /// <param name="path">The library file. Created with a header when absent.</param>
    /// <param name="logger">Optional log sink.</param>
    public RigLibraryBuilder(
        MonoAudio medley,
        IRigSampler sampler,
        IRigRenderer renderer,
        IToneFeatureExtractor extractor,
        RigLibraryOptions options,
        string path,
        IAppLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(medley);
        ArgumentNullException.ThrowIfNull(sampler);
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(extractor);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (options.Count <= 0) throw new ArgumentOutOfRangeException(nameof(options), "Count must be positive.");

        _medley = medley;
        _sampler = sampler;
        _renderer = renderer;
        _extractor = extractor;
        _options = options;
        _profile = options.Profile ?? new RigSamplingProfile();
        _logger = logger;
        Path = path;
        DiFingerprint = RigLibrary.Fingerprint(medley);
        _rng = new Random(options.Seed);

        Resume();
    }

    /// <summary>The file being grown.</summary>
    public string Path { get; }

    /// <summary>The reference DI's fingerprint — part of the key and of the header.</summary>
    public string DiFingerprint { get; }

    /// <summary>Rows on disk and in memory right now.</summary>
    public int Rows
    {
        get { lock (_gate) return _entries.Count; }
    }

    /// <summary>K.</summary>
    public int TargetCount => _options.Count;

    /// <summary>True once every row is rendered.</summary>
    public bool IsComplete => Rows >= TargetCount;

    /// <summary>Renders this builder has done (not rows it loaded from disk).</summary>
    public int Renders => _renders;

    /// <summary>True when the file on disk was a resumable partial of THIS key and
    /// its rows were adopted; false when the builder started from an empty file
    /// (absent, another key, or a partial that did not re-draw the same rigs).</summary>
    public bool ResumedFromDisk { get; private set; }

    /// <summary>An immutable library over the rows so far. Cheap (one array copy)
    /// and safe while a grow is running on another thread.</summary>
    public RigLibrary Snapshot()
    {
        lock (_gate)
        {
            return new RigLibrary(
                _extractor.Id, _options.Seed, DiFingerprint, TargetCount, _entries.ToArray(), _renders);
        }
    }

    /// <summary>
    ///     Renders rows up to <paramref name="upTo" /> (exclusive, clamped to K),
    ///     appending each to the file as it lands.
    /// </summary>
    /// <param name="upTo">The row count to reach.</param>
    /// <param name="status">Progress lines with an ETA, every row for the first
    /// ten and every tenth after — the first match's status line.</param>
    /// <param name="ct">Stops BETWEEN renders; rows already appended stay.</param>
    /// <param name="onCallerThread">
    ///     Render on THIS thread when the renderer is an
    ///     <see cref="ISynchronousRigRenderer" /> — the background grower's path, so
    ///     the render inherits the grower thread's BelowNormal priority. False
    ///     hops each render to the pool, which is right when the caller is the UI.
    /// </param>
    /// <param name="beforeEachRender">Called before every render, with the token;
    /// the background grower parks here while a match is running.</param>
    public async Task GrowAsync(
        int upTo,
        IProgress<string>? status,
        CancellationToken ct,
        bool onCallerThread = false,
        Action<CancellationToken>? beforeEachRender = null)
    {
        upTo = Math.Min(upTo, TargetCount);
        var sync = onCallerThread ? _renderer as ISynchronousRigRenderer : null;
        var eta = new RigLibraryProgress.Estimator();
        var rendered = 0;

        while (Rows < upTo)
        {
            ct.ThrowIfCancellationRequested();
            beforeEachRender?.Invoke(ct);
            ct.ThrowIfCancellationRequested();

            var rig = _sampler.Sample(_rng, _profile);
            var wet = sync is not null
                ? sync.Render(_medley, rig, ct)
                : await _renderer.RenderAsync(_medley, rig, ct).ConfigureAwait(false);
            var entry = new RigLibraryEntry(rig, _extractor.Extract(wet).Vector);

            Append(entry);
            _renders++;
            rendered++;
            eta.Tick();

            var rows = Rows;
            if (status is not null && (rendered <= 10 || rows % 10 == 0 || rows >= upTo))
                status.Report(RigLibraryProgress.Building(rows, upTo, eta.Remaining(upTo - rows)));
        }
    }

    /// <summary>How many rows a legacy file's rows are re-rendered to prove they
    /// were made over THIS DI with THIS engine. Three: the first, the middle
    /// and the last, ~6 s against a 4-minute build.</summary>
    public const int LegacyVerificationRenders = 3;

    /// <summary>
    ///     ONE-TIME IMPORT of a format-2 library for the same key.
    ///     <para>
    ///         The owner's machine already held rendered v2 libraries for the
    ///         bundled DI from the eval work
    ///         (<c>library-tonefx-v2-96-20260912-5e4b70d7.bin</c>), and a v3
    ///         provider that treated them as cache misses would have rendered
    ///         the same 96 rows again from scratch. When this file has NO rows,
    ///         the directory is scanned for <c>library-&lt;extractor&gt;-*-&lt;seed&gt;-&lt;di&gt;.bin</c>
    ///         — any K — and the largest v2 file whose header matches is
    ///         adopted, if two checks pass:
    ///     </para>
    ///     <list type="number">
    ///         <item>STRUCTURE, for free: every stored rig must equal the
    ///         corresponding draw from <c>Random(Seed)</c> under this profile —
    ///         that is what makes them THIS library's rows and not merely rows
    ///         with the right label.</item>
    ///         <item>AUDIO, <see cref="LegacyVerificationRenders" /> renders: the
    ///         first, middle and last rows are re-rendered through the live
    ///         renderer and their features compared — the DI fingerprint says
    ///         the medley is the same, this says the engine and the extractor
    ///         are too.</item>
    ///     </list>
    ///     <para>
    ///         Un-fingerprinted (v1) files never match the pattern and are never
    ///         imported: their DI is unknown. The v2 file is left where it was.
    ///     </para>
    /// </summary>
    /// <param name="status">Progress lines.</param>
    /// <param name="ct">Cancels the verification renders.</param>
    /// <returns>The rows adopted, 0 when nothing was.</returns>
    public async Task<int> TryImportLegacyAsync(IProgress<string>? status, CancellationToken ct)
    {
        if (Rows > 0) return 0;

        var full = System.IO.Path.GetFullPath(Path);
        var directory = System.IO.Path.GetDirectoryName(full);
        if (directory is null || !Directory.Exists(directory)) return 0;

        var safe = string.Concat(_extractor.Id.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_'));
        var pattern = $"library-{safe}-*-{_options.Seed}-{DiFingerprint}.bin";
        var candidates = Directory.EnumerateFiles(directory, pattern)
            .Where(f => !string.Equals(System.IO.Path.GetFullPath(f), full, StringComparison.OrdinalIgnoreCase))
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.Length)
            .ToList();

        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var rows = RigLibrary.TryLoadLegacyV2(candidate.FullName, _extractor.Id, _options.Seed, DiFingerprint);
            if (rows is null || rows.Count == 0) continue;
            var take = Math.Min(rows.Count, TargetCount);

            // (1) structure
            var rng = new Random(_options.Seed);
            var structural = true;
            for (var i = 0; i < take && structural; i++)
            {
                var redrawn = JsonSerializer.Serialize(_sampler.Sample(rng, _profile), RigLibrary.RigJson);
                structural = string.Equals(redrawn, JsonSerializer.Serialize(rows[i].Rig, RigLibrary.RigJson), StringComparison.Ordinal);
            }

            if (!structural)
            {
                _logger?.Warning(AppLogCategory.Audio,
                    $"Tone library: '{candidate.Name}' carries the right key but its rigs do not re-draw from seed {_options.Seed}; not imported.");
                continue;
            }

            // (2) audio
            status?.Report(RigLibraryProgress.CheckingLegacy(take));
            var probes = take <= LegacyVerificationRenders
                ? Enumerable.Range(0, take).ToArray()
                : [0, take / 2, take - 1];
            var acoustic = true;
            foreach (var i in probes)
            {
                ct.ThrowIfCancellationRequested();
                var wet = await _renderer.RenderAsync(_medley, rows[i].Rig, ct).ConfigureAwait(false);
                _renders++;
                if (FeaturesAgree(rows[i].Features, _extractor.Extract(wet).Vector)) continue;
                acoustic = false;
                break;
            }

            if (!acoustic)
            {
                _logger?.Warning(AppLogCategory.Audio,
                    $"Tone library: '{candidate.Name}' does not re-render to its own features here (another engine build or extractor); not imported.");
                continue;
            }

            for (var i = 0; i < take; i++) Append(rows[i]);
            _rng = rng;
            _logger?.Info(AppLogCategory.Audio,
                $"Tone library: imported {take} rows from '{candidate.Name}' into '{System.IO.Path.GetFileName(Path)}'.");
            return take;
        }

        return 0;
    }

    /// <summary>Same dimension and every element within 1e-3 (absolute, or relative
    /// for large values). The renderer is deterministic, so a genuine match is
    /// bit-exact and a different DI or engine is off by orders of magnitude; the
    /// tolerance only keeps a rebuilt DLL's last-bit drift from refusing a good file.</summary>
    private static bool FeaturesAgree(float[] stored, float[] fresh)
    {
        if (stored.Length != fresh.Length) return false;
        for (var j = 0; j < stored.Length; j++)
        {
            var scale = MathF.Max(1f, MathF.Abs(fresh[j]));
            if (!(MathF.Abs(stored[j] - fresh[j]) <= 1e-3f * scale)) return false; // NaN fails
        }

        return true;
    }

    /// <summary>Truncates to the last complete row, writes one, closes it, flushes.</summary>
    private void Append(RigLibraryEntry entry)
    {
        lock (_gate)
        {
            using (var fs = new FileStream(Path, FileMode.Open, FileAccess.Write, FileShare.Read))
            using (var w = new BinaryWriter(fs))
            {
                fs.SetLength(_validEnd);
                fs.Seek(_validEnd, SeekOrigin.Begin);
                RigLibrary.WriteRow(w, entry);
                w.Flush();
                fs.Flush(flushToDisk: true);
                _validEnd = fs.Position;
            }

            _entries.Add(entry);
        }
    }

    /// <summary>Adopts the rows on disk when they are THIS library's, else starts a
    /// fresh file.</summary>
    private void Resume()
    {
        var existing = RigLibrary.TryLoad(
            Path, _extractor.Id, _options.Count, _options.Seed, DiFingerprint, out var validEnd);

        if (existing is { Count: > 0 } && ReDrawMatches(existing))
        {
            lock (_gate)
            {
                _entries.AddRange(existing.Entries);
                _validEnd = validEnd;
            }

            ResumedFromDisk = true;
            _logger?.Info(AppLogCategory.Audio,
                $"Tone library: resuming '{System.IO.Path.GetFileName(Path)}' at {existing.Count}/{existing.TargetCount} rows.");
            return;
        }

        if (existing is not null)
        {
            _logger?.Warning(AppLogCategory.Audio,
                existing.Count == 0
                    ? $"Tone library: '{System.IO.Path.GetFileName(Path)}' holds no complete row; starting it over."
                    : $"Tone library: '{System.IO.Path.GetFileName(Path)}' does not re-draw from seed {_options.Seed} " +
                      "(a changed sampler or catalogue); starting it over rather than mixing two libraries.");
        }

        // A fresh header. Whatever was there — another version, another key, a
        // partial that does not re-draw — is replaced, never appended to.
        _rng = new Random(_options.Seed);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
        using var fs = new FileStream(Path, FileMode.Create, FileAccess.Write, FileShare.Read);
        using var w = new BinaryWriter(fs);
        RigLibrary.WriteHeader(w, _extractor.Id, _options.Count, _options.Seed, DiFingerprint);
        w.Flush();
        lock (_gate)
        {
            _entries.Clear();
            _validEnd = fs.Position;
        }
    }

    /// <summary>
    ///     Advances the generator past the stored rows and checks the LAST
    ///     re-drawn rig against the stored one. The last rather than all of them
    ///     because a generator that agrees at row n−1 has agreed at every row
    ///     before it, and the check is a JSON compare per row.
    /// </summary>
    private bool ReDrawMatches(RigLibrary existing)
    {
        var rng = new Random(_options.Seed);
        DcRigPreset? last = null;
        for (var i = 0; i < existing.Count; i++) last = _sampler.Sample(rng, _profile);
        if (last is null) return false;

        var stored = JsonSerializer.Serialize(existing.Entries[^1].Rig, RigLibrary.RigJson);
        var redrawn = JsonSerializer.Serialize(last, RigLibrary.RigJson);
        if (!string.Equals(stored, redrawn, StringComparison.Ordinal)) return false;

        _rng = rng;
        return true;
    }
}
