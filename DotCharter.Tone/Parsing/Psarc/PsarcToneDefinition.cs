namespace DotCharter.Tone.Parsing.Psarc;

/// <summary>A full tone definition lifted from a package manifest: the raw
/// JSON body plus its key/name for display and tone-slot matching. Lives in
/// DotCharter.Tone because the RS tone converter reads it; the package reader
/// that produces it is an optional plugin (<c>IPackageToneImporter</c>).</summary>
public sealed record PsarcToneDefinition(string Key, string Name, string Json);
