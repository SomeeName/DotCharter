namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     Arms the native engine's process-wide null-device switches for one test and
///     puts back EXACTLY what was there before (#606).
///     <para>
///         The fixtures this replaces either set <c>DCA_ENABLE_NULL_DEVICE</c> and
///         never restored it, or cleared it on the way out whatever it had been: a
///         run started with the variable already set (CI does that for a machine
///         without a sound card) lost it after the first such class, and one
///         fixture cleared a <c>DCA_NULL_DEVICE_MANUAL</c> it had not set. The
///         previous values are captured before the first write and restored by
///         <see cref="Dispose" />, which a <c>using</c> or an xUnit class
///         <c>Dispose</c> runs on the skip and failure paths too.
///     </para>
///     <para>
///         The engine reads both variables when it is constructed, so a user of
///         this type opens a native engine and belongs in
///         <c>[Collection("NativeEngine")]</c>
///         (<see cref="NativeEngineCollectionLintTests" /> matches this type's name).
///     </para>
/// </summary>
internal sealed class NullDeviceEnvScope : IDisposable
{
    public const string EnableVariable = "DCA_ENABLE_NULL_DEVICE";
    public const string ManualVariable = "DCA_NULL_DEVICE_MANUAL";

    private readonly (string Name, string? Previous)[] _restore;
    private bool _disposed;

    private NullDeviceEnvScope(params string[] names)
    {
        _restore = [.. names.Select(name => (name, Environment.GetEnvironmentVariable(name)))];
        foreach (var name in names) Environment.SetEnvironmentVariable(name, "1");
    }

    /// <summary>Registers the null device type (<c>DCA_ENABLE_NULL_DEVICE=1</c>).</summary>
    public static NullDeviceEnvScope Enable() => new(EnableVariable);

    /// <summary>The null device in MANUAL mode: no pacing thread, so no callback races
    /// an offline render (both variables set to <c>1</c>).</summary>
    public static NullDeviceEnvScope EnableManual() => new(EnableVariable, ManualVariable);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var (name, previous) in _restore) Environment.SetEnvironmentVariable(name, previous);
    }
}
