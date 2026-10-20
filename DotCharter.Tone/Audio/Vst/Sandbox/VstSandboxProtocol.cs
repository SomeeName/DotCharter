using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotCharter.Tone.Audio.Vst.Sandbox;

/// <summary>
/// Newline-delimited JSON control protocol spoken over the named pipe
/// between the app and <c>juce_vst_host.exe</c>. Requests carry an
/// incrementing <c>id</c>; the child echoes it in the matching response.
/// Unsolicited lines (no <c>id</c>) are events: <c>hello</c> on connect,
/// <c>event/editorClosed</c> when the user closes a plugin window.
/// </summary>
public static class VstSandboxProtocol
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    public const string InstrumentSlotId = "instrument";

    public static string Serialize(VstSandboxRequest request)
        => JsonSerializer.Serialize(request, Json);

    public static VstSandboxMessage? TryParse(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        try { return JsonSerializer.Deserialize<VstSandboxMessage>(line, Json); }
        catch (JsonException) { return null; }
    }
}

public sealed record VstSandboxRequest
{
    public long? Id { get; init; }
    public string Op { get; init; } = string.Empty;
    public string? Slot { get; init; }
    public string? Path { get; init; }
    public bool? Bypass { get; init; }
    public bool? Value { get; init; }
    public string[]? Slots { get; init; }
    public int? Sr { get; init; }
    public int? Block { get; init; }
    public string? State { get; init; }
}

/// <summary>One parsed line from the child: response or event.</summary>
public sealed record VstSandboxMessage
{
    public long? Id { get; init; }
    public bool? Ok { get; init; }
    public string? Error { get; init; }
    public int? Latency { get; init; }
    public string? Name { get; init; }
    public string? State { get; init; }
    public string? Op { get; init; }     // "hello" | "event" on unsolicited lines
    public string? Event { get; init; }  // "editorClosed"
    public string? Slot { get; init; }
    public long? Pid { get; init; }
    public int? BridgeVersion { get; init; }

    public bool IsResponse => Id.HasValue;
    public bool IsHello => !Id.HasValue && Op == "hello";
    public bool IsEvent => !Id.HasValue && Op == "event";
}
