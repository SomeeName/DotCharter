namespace DotCharter.Services.Infrastructure.Storage;

/// <summary>Strict decoding of explicit Explorer volume settings; never assume undocumented defaults.</summary>
internal sealed record RecycleBinSettings(bool? IsEnabled, long? QuotaBytes)
{
    internal static RecycleBinSettings FromValues(object? nukeOnDelete, object? maxCapacity)
    {
        // Registry DWORDs are boxed Int32s. Strings and QWORDs are deliberately not coerced.
        bool? enabled = nukeOnDelete switch { 0 => true, 1 => false, _ => null };
        var quota = maxCapacity is int megabytes and > 0 ? (long?)megabytes * 1024 * 1024 : null;
        return new RecycleBinSettings(enabled, quota);
    }

    internal static bool PolicyAllowsRecycling(object? noRecycleFiles, object? recycleBinSize) =>
        (noRecycleFiles == null || noRecycleFiles is int and 0) && recycleBinSize == null;
}
