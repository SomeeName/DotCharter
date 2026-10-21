using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotCharter.Services.Audio.StemSeparation;

/// <summary>
/// JSON-backed store for user-added stem-separation models. Lives in
/// <c>%APPDATA%/DotCharter/models/custom_models.json</c>.
/// </summary>
public sealed class CustomStemModelStore
{
    private readonly string _path;
    private readonly IAppLogger? _logger;
    private readonly object _gate = new();

    /// <summary>True when the last <see cref="Load" /> could not READ the file (as
    /// opposed to finding it absent or unparseable). Guarded by <c>_gate</c>.</summary>
    private bool _storeUnreadable;

    public CustomStemModelStore(string modelsDirectory, IAppLogger? logger = null)
    {
        _path = Path.Combine(modelsDirectory, "custom_models.json");
        _logger = logger;
    }

    /// <summary>
    ///     The user-added models. Empty when the file is missing or CORRUPT
    ///     (readable bytes that are not a model list).
    ///     <para>
    ///         A file that could not be READ — an <see cref="IOException" />, a
    ///         sharing violation, an antivirus or cloud-sync hold — is a different
    ///         answer: the file is presumed intact, and an empty list handed to a
    ///         caller that then <see cref="Save" />s (adding one model rewrites the
    ///         whole list) drops every model the user had registered. The read is
    ///         not cached, so the next call retries; until one succeeds
    ///         <see cref="Save" /> refuses to write.
    ///     </para>
    /// </summary>
    public IReadOnlyList<StemModelDescriptor> Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_path))
            {
                _storeUnreadable = false;
                return Array.Empty<StemModelDescriptor>();
            }

            try
            {
                var json = AtomicFile.ReadAllText(_path);
                var dtos = JsonSerializer.Deserialize<List<Dto>>(json) ?? [];
                _storeUnreadable = false;
                return dtos.Where(IsUsable).Select(d => d.ToDescriptor()).ToList();
            }
            catch (JsonException ex)
            {
                _storeUnreadable = false;
                _logger?.Warning(AppLogCategory.Audio,
                    $"Failed to load custom stem models from '{_path}': {ex.Message}");
                return Array.Empty<StemModelDescriptor>();
            }
            catch (Exception ex)
            {
                _storeUnreadable = true;
                _logger?.Warning(AppLogCategory.Audio,
                    $"Custom stem models could not be read from '{_path}' ({ex.GetType().Name}: " +
                    $"{ex.Message}); NOT writing over the file until a read succeeds.");
                return Array.Empty<StemModelDescriptor>();
            }
        }
    }

    /// <summary>A model whose file name is not a bare file name is dropped (and logged): the name is joined onto the models folder.</summary>
    private bool IsUsable(Dto dto)
    {
        if (StemModelFileName.IsBare(dto.FileName)) return true;
        _logger?.Warning(AppLogCategory.Audio,
            $"Ignoring custom stem model '{dto.Id}' in '{_path}': its file name is not a plain file name.");
        return false;
    }

    public void Save(IEnumerable<StemModelDescriptor> descriptors)
    {
        lock (_gate)
        {
            if (_storeUnreadable)
            {
                _logger?.Warning(AppLogCategory.Audio,
                    $"Custom stem models at '{_path}' were not readable; skipping the write so the " +
                    "existing list survives. This change was not persisted.");
                return;
            }

            try
            {
                var dtos = descriptors.Select(Dto.From).ToList();
                AtomicFile.WriteAllText(_path,
                    JsonSerializer.Serialize(dtos, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                _logger?.Error(AppLogCategory.Audio,
                    $"Failed to save custom stem models to '{_path}'.", ex);
            }
        }
    }

    private sealed class Dto
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("displayName")] public string DisplayName { get; set; } = "";
        [JsonPropertyName("description")] public string Description { get; set; } = "";
        [JsonPropertyName("downloadUrl")] public string DownloadUrl { get; set; } = "";
        [JsonPropertyName("fileName")] public string FileName { get; set; } = "";
        [JsonPropertyName("approxBytes")] public long ApproxBytes { get; set; }
        [JsonPropertyName("sourceCount")] public int SourceCount { get; set; }
        [JsonPropertyName("kind")] public string Kind { get; set; } = "MdxNetOnnx";

        public static Dto From(StemModelDescriptor d) => new()
        {
            Id = d.Id, DisplayName = d.DisplayName, Description = d.Description,
            DownloadUrl = d.DownloadUrl, FileName = d.FileName,
            ApproxBytes = d.ApproxBytes, SourceCount = d.SourceCount, Kind = d.Kind.ToString(),
        };

        public StemModelDescriptor ToDescriptor() => new()
        {
            Id = Id, DisplayName = DisplayName, Description = Description,
            DownloadUrl = DownloadUrl, FileName = FileName,
            ApproxBytes = ApproxBytes, SourceCount = SourceCount,
            Kind = Enum.TryParse<StemModelKind>(Kind, true, out var k) ? k : StemModelKind.MdxNetOnnx,
        };
    }
}
