using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Diagnostics;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Avalonia.VisualTree;

namespace DotCharter.UI.Views.Shell;

/// <summary>
/// Counts the style value frames every realized control carries, per rule.
///     A style is attached to every control its selector's TYPE matches inside the scope
///     that declares it, worn or not: each attachment is a frame, an activator and a
///     <c>DynamicResource</c> expression per setter, so the count per rule is where the
///     managed heap's style bookkeeping goes. Read through Avalonia's public value-store
///     diagnostics, so it works on the release build (the perf autopilot's
///     <c>DOTCHARTER_AUTOPILOT_STYLE_CENSUS=1</c>) and in the headless tests alike.
/// </summary>
internal static class StyleFrameCensus
{
    /// <summary>One rule: its selector (or theme), the element whose <c>Styles</c> declare it,
    /// how many controls carry it and how many setter values those frames hold.</summary>
    internal sealed record Rule(string Selector, string Scope, int Frames, int Values);

    internal sealed record Result(int Controls, int Frames, int Values, IReadOnlyList<Rule> Rules,
        IReadOnlyDictionary<string, int> FramesBySurface);

    /// <summary>The shell hosts whose subtrees the per-surface totals are split by.</summary>
    internal static readonly string[] Surfaces =
        ["PlayModeHost", "SongBrowserHost", "SettingsHost", "HomeHost", "DialogContainer", "MainSplitGrid"];

    /// <summary>Counts every realized control under <paramref name="root" />, or only those
    /// <paramref name="controls" /> accepts.</summary>
    internal static Result Take(StyledElement root, Func<StyledElement, bool>? controls = null)
    {
        var rules = new Dictionary<(string, string), (int Frames, int Values)>();
        var bySurface = new Dictionary<string, int>(StringComparer.Ordinal);
        int frames = 0, values = 0;
        var realized = Realized(root).Where(e => controls?.Invoke(e) ?? true).ToList();
        foreach (var element in realized)
        {
            var surface = SurfaceOf(element);
            foreach (var frame in AppliedFrames(element))
            {
                if (Read(frame, "Source") is not IStyle style) continue;
                var key = (Describe(style), ScopeOf(style));
                var n = (Read(frame, "Values") as System.Collections.IEnumerable)?.Cast<object>().Count() ?? 0;
                rules[key] = rules.TryGetValue(key, out var c) ? (c.Frames + 1, c.Values + n) : (1, n);
                frames++;
                values += n;
                bySurface[surface] = bySurface.GetValueOrDefault(surface) + 1;
            }
        }

        var list = rules.Select(r => new Rule(r.Key.Item1, r.Key.Item2, r.Value.Frames, r.Value.Values))
            .OrderByDescending(r => r.Frames).ToList();
        return new Result(realized.Count, frames, values, list, bySurface);
    }

    /// <summary>The value store's applied frames. The members are <c>[PrivateApi]</c>, so the
    /// reference assembly omits them: read through reflection (Avalonia is not trimmed).</summary>
    private static IEnumerable<object> AppliedFrames(StyledElement element)
    {
        var diagnostic = element.GetValueStoreDiagnostic();
        return Read(diagnostic, "AppliedFrames") as IEnumerable<object> ?? [];
    }

    private static object? Read(object target, string property) =>
        target.GetType().GetProperty(property)?.GetValue(target)
        ?? target.GetType().GetInterfaces().Select(i => i.GetProperty(property)).FirstOrDefault(p => p is not null)?.GetValue(target);

    internal static string Format(Result result, int top)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture,
            $"controls={result.Controls} frames={result.Frames} values={result.Values}\n");
        foreach (var s in result.FramesBySurface.OrderByDescending(s => s.Value))
            sb.Append(CultureInfo.InvariantCulture, $"  surface {s.Key}: {s.Value}\n");
        foreach (var r in result.Rules.Take(top))
            sb.Append(CultureInfo.InvariantCulture, $"  {r.Frames,7} {r.Values,7}  {r.Selector}  @{r.Scope}\n");
        return sb.ToString();
    }

    private static List<StyledElement> Realized(StyledElement root)
    {
        var seen = new HashSet<StyledElement>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<StyledElement>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var e = stack.Pop();
            if (!seen.Add(e)) continue;
            if (e is Visual v)
            {
                foreach (var c in v.GetVisualChildren())
                    if (c is StyledElement s) stack.Push(s);
            }

            foreach (var c in ((ILogical)e).LogicalChildren)
                if (c is StyledElement s) stack.Push(s);
        }

        return [.. seen];
    }

    private static string SurfaceOf(StyledElement element)
    {
        for (var e = element as Visual; e is not null; e = e.GetVisualParent())
        {
            if (e is StyledElement { Name: { } name } && Surfaces.Contains(name, StringComparer.Ordinal))
                return name;
        }

        return "(shell)";
    }

    private static string Describe(IStyle style) => style switch
    {
        ControlTheme theme => "theme " + (theme.TargetType?.Name ?? "?"),
        Style { Selector: { } selector } s => (InsideTheme(s) ? "theme-rule " : "") + selector,
        _ => style.GetType().Name,
    };

    private static bool InsideTheme(StyleBase style)
    {
        for (var p = style.Parent; p is not null; p = (p as StyleBase)?.Parent)
            if (p is ControlTheme) return true;
        return false;
    }

    /// <summary>The type of the element (or application) whose <c>Styles</c> hold the rule.</summary>
    private static string ScopeOf(IStyle style)
    {
        var top = style;
        while (top is StyleBase { Parent: { } parent }) top = parent;
        var owner = (top as StyleBase)?.Owner ?? (top as Styles)?.Owner;
        return owner switch
        {
            null => top is ControlTheme ? "theme" : "?",
            StyledElement { Name: { Length: > 0 } name } e => e.GetType().Name + "#" + name,
            _ => owner.GetType().Name,
        };
    }
}
