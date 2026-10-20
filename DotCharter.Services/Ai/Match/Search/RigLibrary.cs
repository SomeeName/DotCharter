using DotCharter.Localization;
using System.Text.Json;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Services.Ai.Match.Search;

/// <summary>One library row: a sampled rig and the features of its render over the
/// medley.</summary>
public sealed record RigLibraryEntry(DcRigPreset Rig, float[] Features);

/// <summary>A retrieval hit: the row, its distance to the query and where it sat.</summary>
public sealed record RigLibraryHit(DcRigPreset Rig, double Distance, int Index);

/// <summary>How to build the library.</summary>
/// <param name="Count">K — how many rigs to sample and render. 4,000 at 20 s is
/// ≈22 h of audio; at the engine's measured offline speed (2.55 s a render in
/// Release) that is <b>~2.8 hours of CPU</b>, which is why it is never paid in
/// front of a waiting user any more: see <see cref="FirstMatchCount" /> and
/// <see cref="RigLibraryProvider" />.</param>
/// <param name="Seed">The sampler seed. Part of the cache key: a different seed is
/// a different library, never a silently mixed one.</param>
/// <param name="Profile">What kind of rigs to draw.</param>
/// <param name="FirstMatchCount">
///     How many rows the FIRST match on a fresh key waits for before it runs —
///     the small library it searches while the rest of <see cref="Count" /> is
///     rendered in the background. 96 is ~4 minutes at the measured render cost;
///     the owner watched "1/4000" and, rightly, gave up.
/// </param>
public sealed record RigLibraryOptions(
    int Count = 4000,
    int Seed = 20260912,
    RigSamplingProfile? Profile = null,
    int FirstMatchCount = 96);

/// <summary>
///     The retrieval half of the matcher: K sampled rigs rendered ONCE over a fixed
///     DI medley, embedded with one <see cref="IToneFeatureExtractor" />, and kept
///     in memory as a plain array for nearest-k.
///     <para>
///         <b>No ANN index, deliberately.</b> At K = 4,000 and a feature vector of a
///         few hundred floats, a full scan is ~1 M multiply-adds — under a
///         millisecond, and next to a single render (hundreds of ms) it does not
///         exist. An ANN library would add a dependency, a build step and an
///         approximation to the one stage of the pipeline that is currently exact.
///         Revisit at K ≥ 10⁵.
///     </para>
///     <para>
///         <b>The build cost is reported in RENDERS</b> (<see cref="RenderCount" />),
///         not seconds: a render's cost depends on the rig, so a second count is the
///         only figure that means the same thing on two machines.
///     </para>
///     <para>
///         <b>The cache key is (extractor id, K, seed, reference-DI fingerprint)</b>
///         and it is in the FILE NAME, so a library built with a different extractor
///         — or over a different dry take — can never be loaded into a comparison
///         that would be meaningless. The extractor matters because the features on
///         both sides of every distance must come from one space; the DI matters
///         because every row IS "this medley through that rig", and a library built
///         over another take describes a different signal while producing entirely
///         plausible numbers. The header repeats all four and is re-checked on load;
///         a mismatch, a short file or a version bump makes the load fail and the
///         caller rebuild, never throw.
///     </para>
///     <para>
///         <b>A library may be PARTIAL (format v3).</b> <see cref="Count" />
///         is how many rows it has; <see cref="TargetCount" /> is the K it is
///         growing towards. The file is append-only — one row at a time, each
///         closed by a marker, so a build interrupted at row 1,731 resumes at 1,731
///         instead of starting over — and row <c>i</c> is always the <c>i</c>-th
///         draw from <c>Random(Seed)</c>, which is what makes a resumed file the
///         same library as a one-shot one (<see cref="RigLibraryBuilder" />).
///     </para>
/// </summary>
public sealed class RigLibrary
{
    private const uint Magic = 0x4C495244; // "DRIL"

    /// <summary>
    ///     3: rows are APPENDED, each closed by <see cref="RowEnd" />,
    ///     and the header carries no row count — a reader takes every
    ///     complete row it finds and drops a torn last one. A v2 file (from before
    ///     the DI fingerprint joined the key) is a cache MISS and is rebuilt.
    /// </summary>
    private const int Version = 3;

    /// <summary>Closes every row. A row without it is a row a crash cut short.</summary>
    private const uint RowEnd = 0x21574F52; // "ROW!"

    internal static readonly JsonSerializerOptions RigJson = new(JsonSerializerDefaults.General);

    internal RigLibrary(
        string extractorId, int seed, string diFingerprint, int targetCount,
        IReadOnlyList<RigLibraryEntry> entries, int renderCount)
    {
        ExtractorId = extractorId;
        Seed = seed;
        DiFingerprint = diFingerprint;
        TargetCount = targetCount;
        Entries = entries;
        RenderCount = renderCount;
    }

    /// <summary>
    ///     The reference DI these features were rendered over, as
    ///     <see cref="Fingerprint" /> computes it.
    ///     <para>
    ///         <b>It is part of the key because a library IS its DI.</b> Every row is
    ///         "what one medley sounds like through one rig", and a query compares the
    ///         TARGET against those rows — so a library built over another dry take
    ///         describes a different signal and the distances are between two unrelated
    ///         things. It produces entirely plausible numbers and the wrong rigs, which
    ///         is the worst failure mode a cache has. The eval lane had already grown a
    ///         workaround for this in the caller (refusing to build a library when
    ///         <c>DOTCHARTER_AUDIO_EVAL_DI</c> is set); that belongs here.
    ///     </para>
    /// </summary>
    public string DiFingerprint { get; }

    /// <summary>
    ///     A short, stable fingerprint of a DI: SHA-256 over the sample rate and the
    ///     raw sample bytes, first 8 hex digits.
    ///     <para>
    ///         Over the SAMPLES rather than a file path or a length: two medleys can be
    ///         the same duration at the same rate and be different takes, and the
    ///         generated medley has no path at all. Truncated to 8 digits because this
    ///         guards against an ACCIDENT (a changed generator, another dry take), not
    ///         against an adversary, and it has to fit in a file name.
    ///     </para>
    /// </summary>
    /// <param name="di">The reference DI the library is rendered over.</param>
    public static string Fingerprint(MonoAudio di)
    {
        ArgumentNullException.ThrowIfNull(di);
        var bytes = new byte[sizeof(int) + di.Samples.Length * sizeof(float)];
        BitConverter.TryWriteBytes(bytes, di.SampleRate);
        Buffer.BlockCopy(di.Samples, 0, bytes, sizeof(int), di.Samples.Length * sizeof(float));
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes))[..8];
    }

    /// <summary>The extractor the features came from. Every query must use the same.</summary>
    public string ExtractorId { get; }

    /// <summary>The sampler seed the library was drawn with.</summary>
    public int Seed { get; }

    /// <summary>The rows, in sampling order.</summary>
    public IReadOnlyList<RigLibraryEntry> Entries { get; }

    /// <summary>How many renders the build cost. 0 for a library read from cache —
    /// which is the point of the cache.</summary>
    public int RenderCount { get; }

    /// <summary>How many rows are HERE. May be less than <see cref="TargetCount" />
    /// while the library is still growing.</summary>
    public int Count => Entries.Count;

    /// <summary>K — the size this library is growing towards (the header's declared
    /// count, which is also the K in the file name).</summary>
    public int TargetCount { get; }

    /// <summary>True once every one of <see cref="TargetCount" /> rows is here.</summary>
    public bool IsComplete => Count >= TargetCount;

    /// <summary>The file NAME for a key — the cache and a shipped copy share it, so
    /// "is the installed library the one this DI needs" is one string compare.</summary>
    /// <param name="extractorId">The extractor the features come from.</param>
    /// <param name="count">K.</param>
    /// <param name="seed">The sampler seed.</param>
    /// <param name="diFingerprint">The reference DI's <see cref="Fingerprint" />.</param>
    public static string CacheFileName(string extractorId, int count, int seed, string diFingerprint)
    {
        ArgumentNullException.ThrowIfNull(extractorId);
        var safe = string.Concat(extractorId.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_'));
        return $"library-{safe}-{count}-{seed}-{diFingerprint}.bin";
    }

    /// <summary>Where a library for this key lives:
    /// <c>%LOCALAPPDATA%\DotCharter\ai-match\library-&lt;extractorId&gt;-&lt;K&gt;-&lt;seed&gt;-&lt;di&gt;.bin</c>.</summary>
    /// <param name="extractorId">The extractor the features come from.</param>
    /// <param name="count">K.</param>
    /// <param name="seed">The sampler seed.</param>
    /// <param name="diFingerprint">The reference DI's <see cref="Fingerprint" />.</param>
    public static string DefaultCachePath(string extractorId, int count, int seed, string diFingerprint)
    {
        var root = Path.Combine(
            UserDataFolders.LocalAppData,
            "DotCharter", "ai-match");
        return Path.Combine(root, CacheFileName(extractorId, count, seed, diFingerprint));
    }

    /// <summary>Loads the cached library for this key, or builds and caches it —
    /// ALL of it, in front of the caller. The eval lane's entry point; the app goes
    /// through <see cref="RigLibraryProvider" />, which never blocks a match on
    /// more than <see cref="RigLibraryOptions.FirstMatchCount" /> renders.</summary>
    public static async Task<RigLibrary> LoadOrBuildAsync(
        MonoAudio medley,
        IRigSampler sampler,
        IRigRenderer renderer,
        IToneFeatureExtractor extractor,
        RigLibraryOptions options,
        ILocalizationService localization,
        IProgress<string>? status,
        CancellationToken ct,
        string? cachePath = null)
    {
        ArgumentNullException.ThrowIfNull(extractor);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(medley);
        var di = Fingerprint(medley);
        var path = cachePath ?? DefaultCachePath(extractor.Id, options.Count, options.Seed, di);
        if (TryLoad(path, extractor.Id, options.Count, options.Seed, di) is { IsComplete: true } cached)
        {
            status?.Report(RigLibraryProgress.FromCache(cached.Count));
            return cached;
        }

        var built = await BuildAsync(medley, sampler, renderer, extractor, options, status, ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            built.Save(path, options.Count);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A library that cannot be cached is still a usable library: the next
            // run pays the build again, which is slow, not wrong.
            status?.Report(localization.Format("Progress_ToneLibraryNotCached", ex.GetType().Name));
        }

        return built;
    }

    /// <summary>Samples K rigs, renders each over the medley and embeds it — in
    /// memory, all K, no file. Row <c>i</c> is the same rig <see cref="RigLibraryBuilder" />
    /// puts at row <c>i</c>: both draw from one <c>Random(Seed)</c> in order.</summary>
    public static async Task<RigLibrary> BuildAsync(
        MonoAudio medley,
        IRigSampler sampler,
        IRigRenderer renderer,
        IToneFeatureExtractor extractor,
        RigLibraryOptions options,
        IProgress<string>? status,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(medley);
        ArgumentNullException.ThrowIfNull(sampler);
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(extractor);
        ArgumentNullException.ThrowIfNull(options);

        var diFingerprint = Fingerprint(medley);
        var profile = options.Profile ?? new RigSamplingProfile();
        var rng = new Random(options.Seed);
        var entries = new List<RigLibraryEntry>(options.Count);
        var renders = 0;
        var eta = new RigLibraryProgress.Estimator();
        for (var i = 0; i < options.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var rig = sampler.Sample(rng, profile);
            var wet = await renderer.RenderAsync(medley, rig, ct).ConfigureAwait(false);
            renders++;
            entries.Add(new RigLibraryEntry(rig, extractor.Extract(wet).Vector));
            eta.Tick();
            if (i % 10 == 0 || i == options.Count - 1)
                status?.Report(RigLibraryProgress.Building(i + 1, options.Count, eta.Remaining(options.Count - i - 1)));
        }

        return new RigLibrary(extractor.Id, options.Seed, diFingerprint, options.Count, entries, renders);
    }

    /// <summary>The k nearest rows to a target, nearest first. The similarity's
    /// extractor id must match the library's — the alternative is a comparison
    /// between two unrelated spaces that produces plausible numbers and wrong
    /// rigs.</summary>
    public IReadOnlyList<RigLibraryHit> Query(ToneFeatures target, int k, IToneSimilarity similarity)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(similarity);
        if (!string.Equals(similarity.Extractor.Id, ExtractorId, StringComparison.Ordinal) ||
            !string.Equals(target.ExtractorId, ExtractorId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Library was built with extractor '{ExtractorId}' but the query uses " +
                $"'{target.ExtractorId}' / '{similarity.Extractor.Id}'.");
        }

        var hits = new List<RigLibraryHit>(Entries.Count);
        for (var i = 0; i < Entries.Count; i++)
        {
            var d = similarity.Distance(new ToneFeatures(Entries[i].Features, ExtractorId), target);
            hits.Add(new RigLibraryHit(Entries[i].Rig, d, i));
        }

        hits.Sort(static (a, b) => a.Distance.CompareTo(b.Distance));
        return hits.Count <= k ? hits : hits.GetRange(0, Math.Max(0, k));
    }

    /// <summary>Writes the library to <paramref name="path" /> atomically (temp file
    /// + move), so an interrupted save never leaves a half-written cache that the
    /// next run would have to detect. The result is a v3 file that
    /// <see cref="RigLibraryBuilder" /> can go on appending to.</summary>
    public void Save(string path, int declaredCount)
    {
        var tmp = path + ".tmp";
        using (var fs = File.Create(tmp))
        using (var w = new BinaryWriter(fs))
        {
            WriteHeader(w, ExtractorId, declaredCount, Seed, DiFingerprint);
            foreach (var e in Entries) WriteRow(w, e);
        }

        File.Move(tmp, path, overwrite: true);
    }

    internal static void WriteHeader(BinaryWriter w, string extractorId, int declaredCount, int seed, string di)
    {
        w.Write(Magic);
        w.Write(Version);
        w.Write(extractorId);
        w.Write(declaredCount);
        w.Write(seed);
        w.Write(di);
    }

    internal static void WriteRow(BinaryWriter w, RigLibraryEntry e)
    {
        w.Write(JsonSerializer.Serialize(e.Rig, RigJson));
        w.Write(e.Features.Length);
        foreach (var f in e.Features) w.Write(f);
        w.Write(RowEnd);
    }

    /// <summary>Reads a cached library, or null when it is absent, unreadable, a
    /// different version, or built for another key. Never throws on a bad file.
    /// A PARTIAL file loads as a partial library (<see cref="IsComplete" /> false);
    /// a torn last row is dropped, not reported.</summary>
    /// <param name="path">The cache file.</param>
    /// <param name="extractorId">The extractor the caller will query with.</param>
    /// <param name="count">K.</param>
    /// <param name="seed">The sampler seed.</param>
    /// <param name="diFingerprint">The caller's reference DI, as
    /// <see cref="Fingerprint" /> computes it.</param>
    public static RigLibrary? TryLoad(
        string path, string extractorId, int count, int seed, string diFingerprint)
        => TryLoad(path, extractorId, count, seed, diFingerprint, out _);

    /// <summary>
    ///     <see cref="TryLoad(string,string,int,int,string)" />, also answering where
    ///     the last COMPLETE row ends — the byte offset an appender truncates to
    ///     before it writes, so a torn row from a crash is overwritten rather than
    ///     followed. 0 when the file did not load.
    /// </summary>
    public static RigLibrary? TryLoad(
        string path, string extractorId, int count, int seed, string diFingerprint, out long validEnd)
    {
        validEnd = 0;
        try
        {
            if (!File.Exists(path)) return null;
            using var fs = File.OpenRead(path);
            using var r = new BinaryReader(fs);
            if (r.ReadUInt32() != Magic) return null;
            if (r.ReadInt32() != Version) return null;
            var id = r.ReadString();
            var k = r.ReadInt32();
            var s = r.ReadInt32();
            var di = r.ReadString();
            if (!string.Equals(id, extractorId, StringComparison.Ordinal) || k != count || s != seed ||
                !string.Equals(di, diFingerprint, StringComparison.Ordinal))
            {
                return null;
            }

            if (k <= 0) return null;

            var entries = new List<RigLibraryEntry>();
            validEnd = fs.Position;
            while (fs.Position < fs.Length && entries.Count < k)
            {
                RigLibraryEntry? row;
                try
                {
                    row = ReadRow(r);
                }
                catch (Exception ex) when (ex is EndOfStreamException or JsonException or InvalidDataException)
                {
                    // A row a crash cut short: everything before it is good.
                    break;
                }

                if (row is null) break;
                entries.Add(row);
                validEnd = fs.Position;
            }

            return new RigLibrary(id, s, di, k, entries, renderCount: 0);
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or JsonException
                                       or UnauthorizedAccessException or InvalidDataException)
        {
            validEnd = 0;
            return null;
        }
    }

    /// <summary>
    ///     The rows of a FORMAT-2 file for this key (a row
    ///     count and dimension in the header, no row markers), for the one-time
    ///     import in <see cref="RigLibraryBuilder.TryImportLegacyAsync" />. Null when
    ///     the file is absent, not v2, or keyed on another extractor, seed or DI. K
    ///     is deliberately NOT part of the check — a v2 file was written for its
    ///     own K, and its first rows are still the first draws from the seed.
    /// </summary>
    /// <param name="path">A candidate file.</param>
    /// <param name="extractorId">The extractor the caller queries with.</param>
    /// <param name="seed">The sampler seed.</param>
    /// <param name="diFingerprint">The caller's DI, as <see cref="Fingerprint" /> computes it.</param>
    public static IReadOnlyList<RigLibraryEntry>? TryLoadLegacyV2(
        string path, string extractorId, int seed, string diFingerprint)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var fs = File.OpenRead(path);
            using var r = new BinaryReader(fs);
            if (r.ReadUInt32() != Magic) return null;
            if (r.ReadInt32() != 2) return null;
            var id = r.ReadString();
            _ = r.ReadInt32(); // the file's own K
            var s = r.ReadInt32();
            var di = r.ReadString();
            if (!string.Equals(id, extractorId, StringComparison.Ordinal) || s != seed ||
                !string.Equals(di, diFingerprint, StringComparison.Ordinal))
            {
                return null;
            }

            var rows = r.ReadInt32();
            _ = r.ReadInt32();
            if (rows is < 0 or > 10_000_000) return null;
            var entries = new List<RigLibraryEntry>(rows);
            for (var i = 0; i < rows; i++)
            {
                var rig = JsonSerializer.Deserialize<DcRigPreset>(r.ReadString(), RigJson);
                if (rig is null) return null;
                var dim = r.ReadInt32();
                if (dim is < 0 or > 1_000_000) return null;
                var f = new float[dim];
                for (var j = 0; j < dim; j++) f[j] = r.ReadSingle();
                entries.Add(new RigLibraryEntry(rig, f));
            }

            return entries;
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or JsonException
                                       or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>One row, or null when what is there is not a row (a bad marker, an
    /// impossible dimension, an unreadable rig).</summary>
    private static RigLibraryEntry? ReadRow(BinaryReader r)
    {
        var json = r.ReadString();
        var rig = JsonSerializer.Deserialize<DcRigPreset>(json, RigJson);
        if (rig is null) return null;
        var dim = r.ReadInt32();
        if (dim is < 0 or > 1_000_000) return null;
        var f = new float[dim];
        for (var j = 0; j < dim; j++) f[j] = r.ReadSingle();
        return r.ReadUInt32() == RowEnd ? new RigLibraryEntry(rig, f) : null;
    }
}
