using System.Reflection;
using Avalonia;

namespace DotCharter.Tests.Perf;

/// <summary>
///     Fails a headless dispatch that leaves a <see cref="Visual" /> or an
///     <see cref="Application" /> in a STATIC field of this test assembly after
///     <c>HeadlessSessionFixture</c> has released the app it booted.
///     <para>
///         A dispatch owns its app. A static that outlives the dispatch pins the
///         released window, its App and its whole container for the rest of the
///         run — and, worse, it pins the NEXT app too: Avalonia's
///         <c>Grid</c> (ported from WPF) shares ONE <c>TempDefinitions</c> array
///         per thread through <c>s_tempDefinitionsDataSlot</c>, every Grid that
///         lays out keeps a reference to it, and its slots keep the column and
///         row definitions of whichever Grid used them last. A live Grid of a dead
///         window therefore holds the definitions — and through them the Grid,
///         window and app — of a LATER case. That is how
///         <c>UiPerfSmokeTests._window</c> made
///         <c>Perf/IsolatedAppReleaseUiPerfSmokeTests</c> fail only when it ran
///         after that class in the same process (#250).
///     </para>
///     <para>
///         The fields are found once, by type: a static non-constant field whose
///         declared type could hold a <see cref="Visual" /> or an
///         <see cref="Application" />. Values are read after every dispatch, so the
///         class that leaks fails ITSELF, whatever order the run takes; the guard
///         then clears the field, so no later class inherits the failure.
///         <c>UI/IsolatedAppTeardownTests</c> asserts the fixture calls it and that
///         it sees a leaking field.
///     </para>
/// </summary>
internal static class HeadlessStaticLeakGuard
{
    private static IReadOnlyList<FieldInfo>? _fields;

    /// <summary>The static fields of this assembly that could hold a visual or an app.</summary>
    internal static IReadOnlyList<FieldInfo> Fields => _fields ??= FindFields(typeof(HeadlessStaticLeakGuard).Assembly);

    /// <summary>
    ///     Throws, naming each field, when any of <see cref="Fields" /> still holds a
    ///     visual or an app — after clearing the field, so the case that leaked is the
    ///     one that fails and every later dispatch is judged on its own.
    /// </summary>
    internal static void ThrowIfAnyStaticHoldsAReleasedApp()
    {
        var leaks = Leaks(Fields);
        if (leaks.Count == 0) return;
        Release(Fields);
        throw new InvalidOperationException(
            "The dispatch released its app, but these static fields still hold a piece of it: "
            + string.Join(", ", leaks)
            + ". A dispatch owns its app: clear the field when the window closes (or keep it in a local). "
            + "A static like this pins the released app for the rest of the run and, through Avalonia "
            + "Grid's thread-shared TempDefinitions, the next app as well (#250). "
            + "See DotCharter.Tests/AGENTS.md, \"A dispatch owns its app\".");
    }

    /// <summary>"Type.field (ValueType)" for each field that holds a visual or an app right now.</summary>
    internal static IReadOnlyList<string> Leaks(IEnumerable<FieldInfo> fields) =>
        [.. fields
            .Select(f => (Field: f, Value: f.GetValue(null)))
            .Where(x => x.Value is Visual or Application)
            .Select(x => $"{x.Field.DeclaringType?.Name}.{x.Field.Name} ({x.Value!.GetType().Name})")];

    /// <summary>Nulls each writable field of <paramref name="fields" /> that holds a visual or an app.</summary>
    internal static void Release(IEnumerable<FieldInfo> fields)
    {
        foreach (var field in fields)
        {
            if (!field.IsInitOnly && field.GetValue(null) is Visual or Application)
                field.SetValue(null, null);
        }
    }

    internal static IReadOnlyList<FieldInfo> FindFields(Assembly assembly)
    {
        Type?[] types;
        try { types = assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { types = ex.Types; }

        return [.. types
            .OfType<Type>()
            .Where(t => !t.ContainsGenericParameters && t != typeof(HeadlessStaticLeakGuard))
            .SelectMany(t => t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            .Where(f => !f.IsLiteral && CouldHoldAnApp(f.FieldType))];
    }

    private static bool CouldHoldAnApp(Type type) =>
        !type.IsValueType
        && (typeof(Visual).IsAssignableFrom(type) || typeof(Application).IsAssignableFrom(type)
            || type.IsAssignableFrom(typeof(Visual)) || type.IsAssignableFrom(typeof(Application)));
}
