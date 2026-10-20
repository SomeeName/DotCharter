using System.Collections;
using System.Reflection;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     Reflection deep-compare that reports every differing PATH at once, so one
///     run names every loss rather than the first.
///     <para>Shared on purpose: the <c>.rscp</c> round trip
///     (<c>RscpDeepValueRoundTripTests</c>) and the domain clone/copy guard
///     (<c>DomainCloneCoverageTests</c>) ask the same question of two different
///     transports — "did every settable property survive?" — and a second
///     hand-rolled walker would be a second thing to keep correct.</para>
///     <para>Only PUBLIC SETTABLE properties are walked. A computed property is a
///     function of the ones that are, so comparing it would double-count; a
///     property with no setter is nothing any copy path could have transferred.</para>
/// </summary>
internal static class DeepCompare
{
    /// <summary>Every differing path between <paramref name="a" /> and
    /// <paramref name="b" />, rooted at <paramref name="rootPath" /> and skipping
    /// any path in <paramref name="ignored" /> (and everything under it).</summary>
    public static List<string> Diff(string rootPath, object? a, object? b, IReadOnlySet<string> ignored)
    {
        var diffs = new List<string>();
        Walk(rootPath, a, b, diffs, new HashSet<object>(ReferenceEqualityComparer.Instance), ignored);
        return diffs;
    }

    private static void Walk(string path, object? a, object? b, List<string> diffs, HashSet<object> seen,
        IReadOnlySet<string> ignored)
    {
        if (ignored.Contains(path)) return;
        if (diffs.Count > 200) return;

        if (a is null || b is null)
        {
            if (!ReferenceEquals(a, b)) diffs.Add($"{path}: {Fmt(a)} -> {Fmt(b)}");
            return;
        }

        var type = a.GetType();
        if (type != b.GetType())
        {
            diffs.Add($"{path}: type {type.Name} -> {b.GetType().Name}");
            return;
        }

        if (type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal))
        {
            if (!Equals(a, b)) diffs.Add($"{path}: {Fmt(a)} -> {Fmt(b)}");
            return;
        }

        if (type == typeof(Fraction) || type == typeof(FractionalPosition))
        {
            if (!Equals(a, b)) diffs.Add($"{path}: {Fmt(a)} -> {Fmt(b)}");
            return;
        }

        if (a is IDictionary da && b is IDictionary db)
        {
            foreach (var key in da.Keys)
            {
                if (!db.Contains(key))
                {
                    diffs.Add($"{path}[{key}]: present -> MISSING");
                    continue;
                }

                Walk($"{path}[{key}]", da[key], db[key], diffs, seen, ignored);
            }

            foreach (var key in db.Keys)
            {
                if (!da.Contains(key))
                    diffs.Add($"{path}[{key}]: MISSING -> {Fmt(db[key])}");
            }

            return;
        }

        if (a is IEnumerable ea && b is IEnumerable eb)
        {
            var la = ea.Cast<object?>().ToList();
            var lb = eb.Cast<object?>().ToList();
            if (la.Count != lb.Count)
            {
                diffs.Add($"{path}.Count: {la.Count} -> {lb.Count}");
                return;
            }

            for (var i = 0; i < la.Count; i++) Walk($"{path}[{i}]", la[i], lb[i], diffs, seen, ignored);
            return;
        }

        if (!seen.Add(a)) return;

        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.GetIndexParameters().Length > 0) continue;
            if (prop.GetSetMethod() is null) continue; // computed / read-only
            object? va, vb;
            try
            {
                va = prop.GetValue(a);
                vb = prop.GetValue(b);
            }
            catch (TargetInvocationException)
            {
                continue;
            }

            Walk($"{path}.{prop.Name}", va, vb, diffs, seen, ignored);
        }
    }

    private static string Fmt(object? v)
    {
        return v switch
        {
            null => "<null>",
            string s => $"\"{s}\"",
            double d => d.ToString("R"),
            _ => v.ToString() ?? "?"
        };
    }
}
