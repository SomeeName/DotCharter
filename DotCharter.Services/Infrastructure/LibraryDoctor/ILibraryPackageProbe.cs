namespace DotCharter.Services.Infrastructure.LibraryDoctor;

/// <summary>Result of probing one PSARC file on disk.</summary>
public sealed record PackageProbeResult(
    bool Readable,
    AudioAvailability Audio,
    string FailureReason = "");

/// <summary>
/// Opens a package read-only to answer the two questions the index cannot:
/// does the file still parse, and does it carry audio. Abstracted so the
/// doctor service can be unit-tested with a fake probe (no real archives).
/// </summary>
public interface ILibraryPackageProbe
{
    PackageProbeResult Probe(string path);
}
