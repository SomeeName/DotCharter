namespace DotCharter.Tests.Ai.Eval;

/// <summary>
///     A variable table a test hands to
///     <see cref="ToneEvalEnvironment.TryResolve(Func{string, string?}, out ToneEvalEnvironment?, out string)" />
///     in place of the process environment. The process environment is shared by
///     every class in the run, and the classes run in parallel: three of them
///     cleared and set the same eval variables, so one read the other's provider
///     and prices (#606).
/// </summary>
internal sealed class FakeEnvironment
{
    private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);

    public void Set(string name, string? value) => _values[name] = value;

    public string? Read(string name) => _values.GetValueOrDefault(name);
}
