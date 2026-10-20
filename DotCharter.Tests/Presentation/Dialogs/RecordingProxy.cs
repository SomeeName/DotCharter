using System.Reflection;

namespace DotCharter.Tests.Presentation.Dialogs;

/// <summary>
///     Any interface as a recorder: every call is logged as <c>Name(arg, …)</c>
///     and answers the default — a completed task for a <see cref="Task" />, a
///     completed task of the default for a <see cref="Task{T}" />.
/// </summary>
public class RecordingProxy : DispatchProxy
{
    public List<string> Calls { get; } = [];

    /// <summary>The same calls with their arguments as objects, for a test that needs more than the text.</summary>
    public List<(string Method, object?[] Args)> Invocations { get; } = [];

    public static (T Proxy, RecordingProxy Recorder) Create<T>() where T : class
    {
        var proxy = Create<T, RecordingProxy>();
        return (proxy, (RecordingProxy)(object)proxy);
    }

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(method);
        Invocations.Add((method.Name, args ?? []));
        Calls.Add($"{method.Name}({string.Join(", ", (args ?? []).Select(a => Convert.ToString(a, System.Globalization.CultureInfo.InvariantCulture)))})");
        var type = method.ReturnType;
        if (type == typeof(void)) return null;
        if (type == typeof(Task)) return Task.CompletedTask;
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var value = type.GetGenericArguments()[0] is { IsValueType: true } inner ? Activator.CreateInstance(inner) : null;
            return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(type.GetGenericArguments()[0])
                .Invoke(null, [value]);
        }

        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }
}
