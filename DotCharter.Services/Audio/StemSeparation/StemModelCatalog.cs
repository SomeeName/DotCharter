// ReSharper disable InconsistentNaming -- private static catalog field uses _camelCase consistent with project style.
using JuceSharp.StemSeparation.Onnx;
using JuceSharp.StemSeparation.Runtime;

namespace DotCharter.Services.Audio.StemSeparation;

/// <summary>
/// Which inference backend a model is meant for.
/// </summary>
public enum StemModelKind
{
    /// <summary>ggml-format Demucs weights for the in-process demucs.cpp backend.</summary>
    DemucsGgml,
    /// <summary>MDX-Net (UVR) ONNX model for the managed ONNX Runtime backend.</summary>
    MdxNetOnnx,
    /// <summary>BS-RoFormer ONNX (no-STFT variant) for the managed ONNX Runtime backend.</summary>
    BsRoformerOnnx,
    /// <summary>Hybrid Transformer Demucs (htdemucs v4) ONNX export for the managed ONNX Runtime backend.</summary>
    HtdemucsOnnx,
}

/// <summary>
/// One downloadable stem-separation model: a ggml weight file for the
/// in-process demucs.cpp backend, or an ONNX graph for the managed ONNX Runtime
/// backend.
/// </summary>
public sealed class StemModelDescriptor
{
    /// <summary>Stable identifier used by AppConfig and the manager.</summary>
    public required string Id { get; init; }

    /// <summary>User-facing label, e.g. "HTDemucs (4-source)".</summary>
    public required string DisplayName { get; init; }

    /// <summary>Long-form description for the Settings UI when the app has no copy
    /// of its own for the model (<see cref="DescriptionKey"/> is null): a model
    /// JuceSharp adds before this catalogue knows it, or one the user imported.</summary>
    public required string Description { get; init; }

    /// <summary>The resx key of the app's own Settings copy for a built-in model,
    /// so it reads in the UI language. When set, it wins over <see cref="Description"/>.</summary>
    public string? DescriptionKey { get; init; }

    /// <summary>HTTPS download URL. The FIRST source — a derived ONNX entry may
    /// carry mirrors, which live on <see cref="Onnx"/> and are the ones the
    /// downloader actually walks.</summary>
    public required string DownloadUrl { get; init; }

    /// <summary>Filename used on disk, e.g. <c>ggml-model-htdemucs-4s-f16.bin</c>.</summary>
    public required string FileName { get; init; }

    /// <summary>Approximate file size in bytes (best-effort; HTTP Content-Length wins at runtime).</summary>
    public long ApproxBytes { get; init; }

    /// <summary>
    /// EXACT file size in bytes when known, else 0. Unlike <see cref="ApproxBytes"/>
    /// this is an integrity constraint, not a UI hint: a completed download whose
    /// length differs is rejected. It is the only check available for a server that
    /// sends no <c>Content-Length</c> (chunked transfer), which is exactly the case
    /// a truncating proxy produces.
    /// </summary>
    public long ExactBytes { get; init; }

    /// <summary>
    /// Lower-case hex SHA-256 of the model file, or null when the hash is not
    /// pinned (every user-imported model, and any built-in nobody has hashed yet).
    /// When present the download is verified against it BEFORE the file is moved
    /// into place, so a corrupt or substituted model never becomes "installed".
    /// </summary>
    public string? Sha256 { get; init; }

    /// <summary>
    /// Where <see cref="Sha256"/> came from, in words — or, when it is null, WHY
    /// there is no pin. A pin nobody can trace is a pin nobody can re-check, and
    /// "unverified" has to be a stated position rather than an empty field.
    /// Linted by <c>StemModelDownloadIntegrityTests</c>.
    /// </summary>
    public string HashSource { get; init; } = string.Empty;

    /// <summary>Number of separated sources (4 = drums/bass/other/vocals, 6 = + guitar/piano).</summary>
    public int SourceCount { get; init; } = 4;

    /// <summary>Which backend consumes this model file.</summary>
    public StemModelKind Kind { get; init; } = StemModelKind.DemucsGgml;

    /// <summary>
    /// JuceSharp's own entry for this model, for every row that came from
    /// <see cref="OnnxModelCatalog.All"/> — null for the ggml weights (which
    /// carry <see cref="Ggml"/> instead) and for every user import, which
    /// JuceSharp does not know about.
    /// <para>
    /// It is what <c>StemModelManager</c> hands to
    /// <see cref="OnnxModelCatalog.EnsureAsync(OnnxModelEntry,string,IProgress{JuceSharp.StemSeparation.Runtime.DownloadProgress}?,bool,Action{string}?,CancellationToken)"/>,
    /// so the hash, the size and the MIRROR LIST all travel from one table
    /// instead of being re-typed here. Re-typing them is exactly what went
    /// wrong: this catalogue claimed <c>htdemucs.onnx</c> was ~80 MB when the
    /// file is 302,899,058 bytes, and nine of its eleven rows had no hash at
    /// all.
    /// </para>
    /// </summary>
    public OnnxModelEntry? Onnx { get; init; }

    /// <summary>
    /// JuceSharp's own entry for this model, for every row that came from
    /// <see cref="DemucsGgmlModelCatalog.All"/> — null for every ONNX row (which
    /// carries <see cref="Onnx"/>) and for every user import.
    /// <para>
    /// The ggml weights are NOT hand-written here: that left two of the three
    /// unpinned and a <c>HashSource</c> asserting that a Hugging Face DATASET
    /// repo publishes no LFS oid. It does, under the <c>api/datasets/</c> route;
    /// all three hashes come from that API through the submodule's table,
    /// and nothing about them is re-typed in this file.
    /// </para>
    /// </summary>
    public DemucsGgmlModelEntry? Ggml { get; init; }

    /// <summary>
    /// For a model published as a release asset of this app's OWN repository: which release
    /// and which file. Null for every other row.
    /// <para>
    /// <b>Downloading it needs the user's GitHub sign-in</b> (Settings ▸ Updates): the
    /// repository is private, so an anonymous request is a 404. That is also why such a row
    /// is written HERE and not in JuceSharp's <see cref="OnnxModelCatalog"/>: the submodule's
    /// downloader is anonymous by design, and the token belongs to the app
    /// (<see cref="Update.IGitHubReleaseAssetDownloader"/>).
    /// </para>
    /// </summary>
    public Update.GitHubReleaseAssetRef? ReleaseAsset { get; init; }
}

/// <summary>
/// Static catalog of stem-separation models we know how to fetch.
/// <para>
/// <b>The ONNX half is DERIVED from JuceSharp's <see cref="OnnxModelCatalog"/>,
/// not restated here.</b> That table's hashes and sizes were read from the
/// Hugging Face repository API (an LFS object's <c>oid</c> IS its SHA-256), it
/// carries mirror lists, and the downloader behind it resumes. A second
/// hand-maintained copy of the same nine rows is how this file came to claim
/// <c>htdemucs.onnx</c> was ~80 MB (it is 302,899,058 bytes) and to ship nine of
/// eleven entries unpinned. Adding an ONNX model now means adding it to the
/// SUBMODULE; the only thing kept here is the long-form Settings description,
/// which is ours.
/// </para>
/// <para>
/// <b>The ggml half is DERIVED the same way</b>, from
/// <see cref="DemucsGgmlModelCatalog"/> (JuceSharp 0.9). Hand-writing the rows here
/// would leave two of its three
/// rows unpinned behind a <c>HashSource</c> claiming a Hugging Face
/// DATASET repo publishes no LFS oid. It does, under the <c>api/datasets/</c>
/// route; all three are pinned from it, and the only thing written in
/// this file is the long-form Settings description and the app-side id the
/// settings file has always persisted (<see cref="GgmlRows"/>).
/// </para>
/// </summary>
public static class StemModelCatalog
{
    private static readonly List<StemModelDescriptor> Models;

    static StemModelCatalog() => Models = [.. BuiltIn];

    public static IReadOnlyList<StemModelDescriptor> All
    {
        get { lock (Models) return Models.ToArray(); }
    }

    /// <summary>Append a user-imported descriptor (deduplicated by Id).</summary>
    public static void RegisterCustom(StemModelDescriptor descriptor)
    {
        if (descriptor == null) throw new ArgumentNullException(nameof(descriptor));
        lock (Models)
        {
            Models.RemoveAll(d => string.Equals(d.Id, descriptor.Id, StringComparison.OrdinalIgnoreCase));
            Models.Add(descriptor);
        }
    }

    /// <summary>Remove a registered custom descriptor.</summary>
    public static bool UnregisterCustom(string id)
    {
        lock (Models)
        {
            return Models.RemoveAll(d =>
                string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase) &&
                !BuiltIn.Any(b => string.Equals(b.Id, id, StringComparison.OrdinalIgnoreCase))) > 0;
        }
    }

    /// <summary>
    /// The resx key of our long-form Settings copy for a ggml weight, plus the id
    /// this app has always persisted for it, keyed by JuceSharp's id.
    /// <para>
    /// <b>The id override is the one thing that may not be derived.</b>
    /// <c>AppConfig.HtDemucsActiveModelId</c> holds these strings in every
    /// existing user's settings file and <c>StemCacheManifest.ModelId</c> holds
    /// them in every cached separation, so taking JuceSharp's
    /// <c>ggml-htdemucs-4s</c> verbatim would silently un-select the model a user
    /// had chosen. Everything with integrity meaning — file name, URL, hash,
    /// exact size, source count, provenance — comes from the submodule.
    /// </para>
    /// <para>
    /// A weight JuceSharp adds that is not listed here still appears, under
    /// JuceSharp's id and with a description built from what the submodule knows.
    /// The <c>*_ft_*</c> fine-tuned family is deliberately in NEITHER catalogue:
    /// demucs.cpp ships those as four separate per-source weights, which this
    /// app's one-file-per-model install has no shape for.
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, (string AppId, string DescriptionKey)> GgmlRows =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["ggml-htdemucs-4s"] = ("htdemucs-4s", "Settings_StemModels_Desc_HtDemucs4s"),
            ["ggml-htdemucs-6s"] = ("htdemucs-6s", "Settings_StemModels_Desc_HtDemucs6s"),
            ["ggml-hdemucs-mmi-v3"] = ("hdemucs-mmi-v3", "Settings_StemModels_Desc_HDemucsMmiV3"),
        };

    /// <summary>The ggml weights for the in-process demucs.cpp backend, DERIVED
    /// from <see cref="DemucsGgmlModelCatalog.All"/> exactly as the ONNX half is
    /// derived from <see cref="OnnxModelCatalog.All"/>.</summary>
    private static readonly StemModelDescriptor[] GgmlBuiltIn =
        DemucsGgmlModelCatalog.All.Select(FromGgml).ToArray();

    /// <summary>Our long-form Settings copy, keyed by JuceSharp's id, as resx keys
    /// so it reads in the UI language. An entry JuceSharp adds that is not listed
    /// here still appears — it just gets a description built from what the
    /// submodule knows, rather than silently not being offered at all. The copy
    /// names only what this build can run: the ONNX Runtime it ships has DirectML
    /// (any DirectX 12 GPU) and the CPU, and no CUDA.</summary>
    private static readonly Dictionary<string, string> OnnxDescriptionKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["uvr-mdx-net-inst-hq-3"] = "Settings_StemModels_Desc_UvrMdxNetInstHq3",
        ["uvr-mdx-net-voc-ft"] = "Settings_StemModels_Desc_UvrMdxNetVocFt",
        ["uvr-mdx-net-kim-vocal-2"] = "Settings_StemModels_Desc_UvrMdxNetKimVocal2",
        ["uvr-mdx-net-kim-inst"] = "Settings_StemModels_Desc_UvrMdxNetKimInst",
        ["uvr-mdx-net-inst-main"] = "Settings_StemModels_Desc_UvrMdxNetInstMain",
        ["bs-roformer-fp16"] = "Settings_StemModels_Desc_BsRoformerFp16",
        ["htdemucs-6s-onnx"] = "Settings_StemModels_Desc_HtDemucs6sOnnx",
        ["bs-roformer-sw-6s"] = "Settings_StemModels_Desc_BsRoformerSw6s",
    };

    /// <summary>Every resx key the catalogue names, for the lint that checks each
    /// exists in all three languages.</summary>
    internal static IEnumerable<string> DescriptionKeys =>
        GgmlRows.Values.Select(r => r.DescriptionKey).Concat(OnnxDescriptionKeys.Values)
            .Concat(AppHosted.Select(d => d.DescriptionKey!));

    /// <summary>The id of BS-RoFormer SW exported at its 13.35 s training chunk.</summary>
    public const string BsRoformerSw13sId = "bs-roformer-sw-6s-13s";

    /// <summary>
    /// BS-RoFormer SW re-exported at the 588,800-sample (13.35 s) chunk it was trained on, by
    /// <c>JuceSharp/tools/bs-roformer-sw-export</c> from <c>enerjazzer/BS-ROFO-SW-Fixed</c>: fp16,
    /// the per-band mask estimators in seven batched groups, so DirectML's graph fusion is
    /// verified correct on it and the file says so in its metadata
    /// (<c>dml_graph_fusion=verified</c>, read by JuceSharp's <c>OnnxSessionFactory</c>; every
    /// other model keeps fusion off). Its stems agree with PyTorch's own demix at that chunk to
    /// 50–61 dB; the 4 s file's agree with them to only 13–27 dB and leak into the piano stem.
    /// 20.1 s for a 224 s song on an RTX 5070 Ti, peaking at ~5.2 GB above idle.
    /// <para>
    /// The weights' licence is unknown (see <c>bs-roformer-sw-6s</c>), which is why the file is
    /// hosted on this app's PRIVATE releases and nowhere public.
    /// </para>
    /// </summary>
    private static readonly OnnxModelEntry BsRoformerSw13sEntry = new(
        Id: BsRoformerSw13sId,
        Kind: OnnxStemModelKind.BsRoformer,
        DisplayName: "BS-RoFormer SW 6-stem 13 s (ONNX)",
        FileName: "bs_roformer_sw_6stem_13s_fp16.onnx",
        Urls: ["https://github.com/SomeeName/DotCharter/releases/download/models-bs-roformer-sw-v1/bs_roformer_sw_6stem_13s_fp16.onnx"],
        Sha256: "2fd77c88e3fb049eed18d7e3b3c1dbf3c401f61e9c04e58375c557a5a27157fe",
        SizeBytes: 357_936_365,
        HashSource: "SHA-256 of the file JuceSharp/tools/bs-roformer-sw-export writes byte for byte from the " +
                    "enerjazzer/BS-ROFO-SW-Fixed checkpoint (#267), computed before the upload; GitHub's own " +
                    "digest of the release asset, read 2026-10-06, is the same value.",
        SourceCount: 6,
        LicenceNote: "Weights licence UNKNOWN (the trainer of BS-RoFormer SW is not known). Hosted only on " +
                     "DotCharter's private releases. Do not redistribute.",
        BsRoformerLayout: BsRoformerIoLayout.SplitComplex);

    /// <summary>
    /// Rows hosted on this app's own private releases (<see cref="StemModelDescriptor.ReleaseAsset"/>).
    /// They are written here, not derived, because JuceSharp's downloader cannot sign in; every
    /// integrity field still travels on the <see cref="OnnxModelEntry"/> the separator and the
    /// verifier read.
    /// </summary>
    private static readonly StemModelDescriptor[] AppHosted =
    [
        AppHostedRow(BsRoformerSw13sEntry, "models-bs-roformer-sw-v1", "Settings_StemModels_Desc_BsRoformerSw6s13s"),
    ];

    /// <summary>Every built-in row: the ggml weights, JuceSharp's ONNX rows, and the app-hosted
    /// rows, each placed right after the JuceSharp row it improves on.</summary>
    public static IReadOnlyList<StemModelDescriptor> BuiltIn { get; } = BuildBuiltIn();

    private static StemModelDescriptor[] BuildBuiltIn()
    {
        var rows = GgmlBuiltIn.Concat(OnnxModelCatalog.All.Select(FromOnnx)).ToList();
        foreach (var hosted in AppHosted)
        {
            var sibling = rows.FindIndex(r => r.Kind == hosted.Kind && r.Onnx is { } o &&
                                              hosted.Id.StartsWith(o.Id, StringComparison.OrdinalIgnoreCase));
            rows.Insert(sibling < 0 ? rows.Count : sibling + 1, hosted);
        }

        return [.. rows];
    }

    /// <summary>An app-hosted row: every integrity field from its entry, plus where it is published.</summary>
    internal static StemModelDescriptor AppHostedRow(OnnxModelEntry entry, string tag, string descriptionKey)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new StemModelDescriptor
        {
            Id = entry.Id,
            DisplayName = entry.DisplayName,
            Description = "",
            DescriptionKey = descriptionKey,
            FileName = entry.FileName,
            DownloadUrl = entry.Urls[0],
            ApproxBytes = entry.SizeBytes,
            ExactBytes = entry.SizeBytes,
            Sha256 = entry.Sha256,
            HashSource = entry.HashSource,
            SourceCount = entry.SourceCount,
            Kind = MapKind(entry.Kind),
            Onnx = entry,
            ReleaseAsset = new Update.GitHubReleaseAssetRef(tag, entry.FileName),
        };
    }

    /// <summary>One of JuceSharp's ggml rows as this catalogue's descriptor.
    /// Every integrity field travels; only the Settings prose — and the
    /// persisted id, see <see cref="GgmlRows"/> — is ours.</summary>
    internal static StemModelDescriptor FromGgml(DemucsGgmlModelEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        DemucsGgmlModelCatalog.ValidateEntry(entry);

        var known = GgmlRows.TryGetValue(entry.Id, out var row);
        var description = known
            ? ""
            : $"{entry.DisplayName} — {entry.SourceCount} stems, via the in-process demucs.cpp backend. " +
              entry.LicenceNote;

        return new StemModelDescriptor
        {
            Id = known ? row.AppId : entry.Id,
            DisplayName = entry.DisplayName,
            Description = !known && entry.SizeBytes > 0
                ? $"{description} ~{entry.SizeBytes / (1024 * 1024):N0} MB."
                : description,
            DescriptionKey = known ? row.DescriptionKey : null,
            FileName = entry.FileName,
            DownloadUrl = entry.Urls[0],
            ApproxBytes = entry.SizeBytes,
            ExactBytes = entry.SizeBytes,
            Sha256 = entry.Sha256,
            HashSource = entry.HashSource,
            SourceCount = entry.SourceCount,
            Kind = StemModelKind.DemucsGgml,
            Ggml = entry,
        };
    }

    /// <summary>One of JuceSharp's rows as this catalogue's descriptor. Every
    /// integrity field travels; only the Settings prose is ours.</summary>
    internal static StemModelDescriptor FromOnnx(OnnxModelEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var known = OnnxDescriptionKeys.TryGetValue(entry.Id, out var key);
        var description = known
            ? ""
            : $"{entry.DisplayName} — {entry.SourceCount} stems, via the managed ONNX Runtime backend. " +
              entry.LicenceNote;

        return new StemModelDescriptor
        {
            Id = entry.Id,
            DisplayName = entry.DisplayName,
            Description = !known && entry.SizeBytes > 0
                ? $"{description} ~{entry.SizeBytes / (1024 * 1024):N0} MB."
                : description,
            DescriptionKey = key,
            FileName = entry.FileName,
            DownloadUrl = entry.Urls[0],
            // The exact size IS the best approximation; there is no reason to
            // keep a rounded second copy of a number we know precisely.
            ApproxBytes = entry.SizeBytes,
            ExactBytes = entry.SizeBytes,
            Sha256 = entry.Sha256,
            HashSource = entry.HashSource,
            SourceCount = entry.SourceCount,
            Kind = MapKind(entry.Kind),
            Onnx = entry,
        };
    }

    /// <summary>JuceSharp's driver label as ours. Total on purpose — a kind the
    /// submodule adds must be given an app-side meaning here rather than
    /// defaulting onto an inference path that cannot load it.</summary>
    internal static StemModelKind MapKind(OnnxStemModelKind kind) => kind switch
    {
        OnnxStemModelKind.Htdemucs => StemModelKind.HtdemucsOnnx,
        OnnxStemModelKind.BsRoformer => StemModelKind.BsRoformerOnnx,
        OnnxStemModelKind.MdxNet => StemModelKind.MdxNetOnnx,
        _ => throw new NotSupportedException(
            $"JuceSharp's OnnxModelCatalog has a model kind this app has no inference path for: {kind}. " +
            "Add it to StemModelKind and to the ONNX backend before the catalogue offers it."),
    };

    public static StemModelDescriptor? FindById(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        lock (Models)
            return Models.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
    }
}
