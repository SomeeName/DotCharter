using System.Text.Json;

namespace DotCharter.UI.Platform;

/// <summary>
///     The pre-Avalonia reading of <c>appconfig.json</c>: platform options are
///     startup-only and are chosen before any config service exists, so
///     <c>Program.BuildAvaloniaApp</c> reads the RAW file once and asks this
///     for the two or three values it needs. Reading and JSON-parsing the file
///     once per option would cost ~145 ms of the 586 ms that
///     <c>BuildAvaloniaApp</c> spends before the phase clock even starts —
///     `WritablePaths.ConfigRoot` alone probes the directory with a real file
///     write, and that would run per caller). Pure; a missing, empty or malformed
///     document answers null.
/// </summary>
public static class StartupConfigProbe
{
    /// <summary>The string value of a top-level property, or null.</summary>
    public static string? StringValue(string? rawJson, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(rawJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                   doc.RootElement.TryGetProperty(propertyName, out var v) &&
                   v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }
}
