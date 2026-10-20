using System.Reflection;
using DotCharter.Presentation.Contracts.PlayMode;
using DotCharter.Tests.Presentation.Fixtures.Alpha;

namespace DotCharter.Tests.Presentation;

/// <summary>
///     The reflection-shaped rules of docs/agents/ui/target-architecture.md
///     § 4 over <c>DotCharter.Presentation</c>: no constructor takes more than
///     seven dependencies, and a screen view model never holds another screen's
///     view model. The assembly holds contracts only today, so each rule also
///     runs against deliberately bad fixture types (<c>Fixtures/</c>) and must
///     name them — that is what keeps a rule over an empty set honest.
/// </summary>
public sealed class PresentationTypeRulesTests
{
    internal const int MaxConstructorParameters = 7;

    private static readonly Assembly Presentation = typeof(IStartScreen).Assembly;

    [Fact]
    public void No_Presentation_constructor_takes_more_than_seven_parameters()
    {
        Assert.Equal("DotCharter.Presentation", Presentation.GetName().Name);
        var offenders = ConstructorViolations(Presentation.GetTypes());
        Assert.True(offenders.Count == 0,
            $"constructors over {MaxConstructorParameters} parameters (target-architecture.md § 4 — split the screen or " +
            "move shared state to an area-state object):\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void No_Presentation_screen_view_model_holds_another_screens_view_model()
    {
        var offenders = CrossScreenViolations(Presentation.GetTypes());
        Assert.True(offenders.Count == 0,
            "screen view models that hold another screen's view model — share state through Services or " +
            "the area's Shared/ state object instead (target-architecture.md § 3.3):\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void Probe_the_constructor_rule_names_a_type_with_eight_parameters()
    {
        var offenders = ConstructorViolations([typeof(EightDependencies), typeof(SevenDependencies)]);
        Assert.Single(offenders);
        Assert.Contains(nameof(EightDependencies), offenders[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Probe_the_cross_screen_rule_names_a_field_and_a_parameter_but_not_same_folder_or_shared()
    {
        var offenders = CrossScreenViolations(
        [
            typeof(SongListViewModel), typeof(SongRowViewModel),
            typeof(Fixtures.Beta.ResultsViewModel), typeof(Fixtures.Beta.Shared.SessionViewModel)
        ]);

        Assert.Equal(2, offenders.Count);
        Assert.Contains(offenders, o => o.Contains("field", StringComparison.Ordinal) && o.Contains(nameof(Fixtures.Beta.ResultsViewModel), StringComparison.Ordinal));
        Assert.Contains(offenders, o => o.Contains("parameter", StringComparison.Ordinal) && o.Contains("IReadOnlyList", StringComparison.Ordinal));
        Assert.DoesNotContain(offenders, o => o.Contains(nameof(SongRowViewModel), StringComparison.Ordinal));
        Assert.DoesNotContain(offenders, o => o.Contains("SessionViewModel", StringComparison.Ordinal));
    }

    // ── the rules ─────────────────────────────────────────────────────────

    internal static List<string> ConstructorViolations(IEnumerable<Type> types) =>
        types.Where(IsOwnType)
            .SelectMany(t => t.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(c => c.GetParameters().Length > MaxConstructorParameters)
                .Select(c => $"{t.FullName}: {c.GetParameters().Length} parameters"))
            .Order(StringComparer.Ordinal)
            .ToList();

    internal static List<string> CrossScreenViolations(IEnumerable<Type> types)
    {
        var offenders = new List<string>();
        foreach (var type in types.Where(t => IsOwnType(t) && IsViewModel(t)))
        {
            var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public |
                                        BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Where(f => !f.Name.Contains('<', StringComparison.Ordinal)) // a primary-ctor capture repeats its parameter
                .Select(f => ("field", f.Name, f.FieldType));
            var parameters = type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .SelectMany(c => c.GetParameters())
                .Select(p => ("parameter", p.Name ?? "?", p.ParameterType));

            foreach (var (kind, name, declared) in fields.Concat(parameters))
            {
                foreach (var referenced in Flatten(declared))
                {
                    if (referenced == type || !IsViewModel(referenced)) continue;
                    if (referenced.Namespace == type.Namespace) continue;
                    if (IsShared(referenced.Namespace)) continue;
                    offenders.Add($"{type.FullName} {kind} '{name}' ({Describe(declared)}) → {referenced.FullName}");
                }
            }
        }

        return offenders.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
    }

    private static bool IsOwnType(Type t) =>
        !t.IsInterface && !t.IsEnum && t.GetCustomAttribute<System.Runtime.CompilerServices.CompilerGeneratedAttribute>() == null
        && !t.Name.Contains('<', StringComparison.Ordinal);

    private static bool IsViewModel(Type t) =>
        t is { IsClass: true } && t.Name.EndsWith("ViewModel", StringComparison.Ordinal);

    private static bool IsShared(string? ns) =>
        ns != null && (ns.EndsWith(".Shared", StringComparison.Ordinal) || ns.Contains(".Shared.", StringComparison.Ordinal));

    /// <summary>The type and every type argument / element type inside it.</summary>
    private static IEnumerable<Type> Flatten(Type t)
    {
        yield return t;
        if (t.HasElementType && t.GetElementType() is { } element)
            foreach (var inner in Flatten(element)) yield return inner;
        if (t.IsGenericType)
        {
            foreach (var arg in t.GetGenericArguments())
                foreach (var inner in Flatten(arg)) yield return inner;
        }
    }

    private static string Describe(Type t) =>
        t.IsGenericType ? $"{t.Name[..t.Name.IndexOf('`', StringComparison.Ordinal)]}<{string.Join(", ", t.GetGenericArguments().Select(Describe))}>" : t.Name;
}
