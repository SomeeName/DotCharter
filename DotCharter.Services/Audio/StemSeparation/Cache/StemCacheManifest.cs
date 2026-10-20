using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotCharter.Services.Audio.StemSeparation.Cache;

/// <summary>One stem entry inside a cached archive.</summary>
public sealed class StemManifestEntry
{
    /// <summary>Stem name (drums, bass, vocals, other, guitar, piano…).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Entry filename inside the archive (e.g. <c>drums.opus</c>).</summary>
    public string FileName { get; set; } = string.Empty;
}

/// <summary>
/// Describes the contents of one cached stem archive. Serialized as
/// <c>manifest.json</c> inside the <c>.dcstems</c> archive so a cache produced
/// on one machine (or by an older/newer build) is self-describing and portable:
/// the reader learns which codec to decode and which stems are present without
/// guessing from filenames.
/// </summary>
public sealed class StemCacheManifest
{
    /// <summary>Bumped when the on-disk shape changes incompatibly.</summary>
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>The canonical cache key this archive answers (see
    /// <see cref="StemCacheKey.Canonical"/>). Lets a reader confirm it opened
    /// the right archive.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Logical identity that produced the key (diagnostic).</summary>
    public string? LogicalId { get; set; }

    /// <summary>Codec every stem in this archive is stored as.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public StemCodec Codec { get; set; } = StemCodec.Wav;

    /// <summary>Source-audio byte length at generation time (fingerprint half).</summary>
    public long SourceBytes { get; set; }

    /// <summary>Source-audio sampled SHA-256 at generation time (fingerprint half).</summary>
    public string SourceHash { get; set; } = string.Empty;

    /// <summary>SHA-256 over the WHOLE source audio at generation time, or null for
    /// an archive written before it was recorded. A hit whose current audio hashes
    /// differently is stale: the audio changed between the sampled windows.</summary>
    public string? SourceFullHash { get; set; }

    /// <summary>Id of the model that separated (e.g. <c>htdemucs-4s</c>), diagnostic.</summary>
    public string ModelId { get; set; } = string.Empty;

    /// <summary>Build that generated the cache (diagnostic / migration hints).</summary>
    public string AppVersion { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; }

    public List<StemManifestEntry> Stems { get; set; } = [];

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static StemCacheManifest? FromJson(string json)
    {
        try { return JsonSerializer.Deserialize<StemCacheManifest>(json, Options); }
        catch (JsonException) { return null; }
    }
}
