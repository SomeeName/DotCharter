namespace DotCharter.Core.Domain.Project;

/// <summary>
///     A full Rocksmith tone definition owned by the project: the tone key the
///     arrangements' slot names / tone changes reference, a display name, and
///     the raw manifest-compatible tone JSON body that is embedded verbatim
///     into the exported package manifest.
///
///     <para>The body stays an opaque string at the domain level (Core has no
///     serialization deps): tones imported from an existing package are carried
///     verbatim, tones authored/edited in the tone editor are re-serialized by
///     the Services-layer <c>RsToneJsonWriter</c> before landing here.</para>
/// </summary>
public class ToneDefinition
{
    /// <summary>The tone key (matches the manifest body's <c>Key</c> field and
    /// the arrangement's tone slot names). Unique within a project.</summary>
    public string Key { get; set; } = "";

    /// <summary>Display name (the manifest body's <c>Name</c>).</summary>
    public string Name { get; set; } = "";

    /// <summary>The raw manifest tone JSON body.</summary>
    public string Json { get; set; } = "";

    public ToneDefinition Clone() => new() { Key = Key, Name = Name, Json = Json };

    public void CopyFrom(ToneDefinition other)
    {
        Key = other.Key;
        Name = other.Name;
        Json = other.Json;
    }
}
