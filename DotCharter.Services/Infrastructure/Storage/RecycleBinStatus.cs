namespace DotCharter.Services.Infrastructure.Storage;

/// <summary>Evidence required before an unattended recycle operation may run.</summary>
internal sealed record RecycleBinStatus(
    bool IsFixedLocalVolume,
    bool IsPlainLocalPath,
    bool? IsEnabled,
    long? QuotaBytes,
    long? UsedBytes,
    long? CandidateBytes);
