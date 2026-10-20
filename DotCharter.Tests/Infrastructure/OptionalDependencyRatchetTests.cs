using System.Text.RegularExpressions;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     A constructor parameter of the shape <c>IFoo? foo = null</c> is an optional
///     dependency: the composition root always passes it, so in the app the null branch
///     is dead and only tests reach it, and a registration that goes missing fails
///     silently instead of in <c>UI/AppServiceGraphTests</c> (#661). The counts below
///     are each project's optional interface-typed constructor parameters; a count may
///     fall (lower its row in the same change) but never rise. Comments are stripped
///     before counting, and only constructors count (primary constructors included),
///     not methods or delegates.
/// </summary>
public sealed partial class OptionalDependencyRatchetTests
{
    internal sealed record OptionalParameter(string Project, string File, int Line, string Type, string Name)
    {
        public override string ToString() => $"{File}:{Line}: {Type}? {Name} = null";
    }

    private static readonly string[] ScannedProjects =
        ["DotCharter.Services", "DotCharter.Tone", "DotCharter.Tone.UI", "DotCharter.Presentation", "DotCharter.UI"];

    // Seeded 2026-10-13 (#661, after the localization step). Lower a row when its
    // count falls; never raise one.
    private static readonly IReadOnlyDictionary<string, int> Allowed = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["DotCharter.Services"] = 127,
        ["DotCharter.Tone"] = 44,
        ["DotCharter.Tone.UI"] = 20,
        ["DotCharter.Presentation"] = 34,
        ["DotCharter.UI"] = 57,
    };

    private static readonly HashSet<string> ConstructorModifiers = new(StringComparer.Ordinal)
    {
        "public", "internal", "private", "protected", "class", "record", "struct",
    };

    [Fact]
    public void NoProjectGainsAnOptionalInterfaceDependency()
    {
        var failures = Violations(Allowed, Scan(TestPaths.RepoRoot()));

        Assert.True(failures.Count == 0, FailureMessage(failures));
    }

    [Fact]
    public void TheTableCoversExactlyTheScannedProjects() =>
        Assert.Equal(ScannedProjects.Order(StringComparer.Ordinal), Allowed.Keys.Order(StringComparer.Ordinal));

    [Fact]
    public void TheCounterSeesConstructorParametersAndNothingElse()
    {
        const string source = """
            namespace Probe;
            public sealed class Host
            {
                // public Host(IFoo? commented = null) {}
                /* public Host(IFoo? blockCommented = null) {} */
                public Host(IFoo foo, IBar? bar = null, string? name = null, Stem? stem = null,
                    DotCharter.Services.IQualified? qualified = null,
                    IList<int>? generic = null, int? count = null)
                {
                }

                internal Host(Func<int, int> f, IBaz? baz = null) : this(null!) { }

                public void Method(IFoo? notACtor = null) { }
                public Host Create(IFoo? factory = null) => new(null!);
                public delegate void Handler(IFoo? delegateParameter = null);
                private static int Local(IFoo? local = null) => 0;
            }
            public sealed class Primary(IFoo foo, IBar? primaryBar = null);
            public sealed record PrimaryRecord<T>(IBar? recordBar = null);
            """;

        var names = ParametersIn("Probe", "Probe.cs", source).Select(p => p.Name).ToArray();

        Assert.Equal(["bar", "qualified", "generic", "baz", "primaryBar", "recordBar"], names);
    }

    [Fact]
    public void OneExtraOptionalParameterFailsAndIsNamed()
    {
        const string seeded = """
            public sealed class Host
            {
                public Host(IFoo foo, IBar? bar = null) { }
            }
            """;
        const string grown = """
            public sealed class Host
            {
                public Host(IFoo foo, IBar? bar = null, IBaz? sneaky = null) { }
            }
            """;
        var table = new Dictionary<string, int>(StringComparer.Ordinal) { ["Probe"] = 1 };

        Assert.Empty(Violations(table, ParametersIn("Probe", "Probe/Host.cs", seeded)));

        var failures = Violations(table, ParametersIn("Probe", "Probe/Host.cs", grown));

        var failure = Assert.Single(failures);
        Assert.Contains("Probe: 2 optional interface-typed constructor parameters", failure, StringComparison.Ordinal);
        Assert.Contains("IBaz? sneaky = null", failure, StringComparison.Ordinal);
        Assert.Contains("lower its row", FailureMessage(failures), StringComparison.Ordinal);
    }

    [Fact]
    public void AFallingCountPasses()
    {
        var table = new Dictionary<string, int>(StringComparer.Ordinal) { ["Probe"] = 5 };

        Assert.Empty(Violations(table, ParametersIn("Probe", "Probe/Host.cs", "class Host { public Host(IBar? bar = null) { } }")));
    }

    [Fact]
    public void TheScanSeesTheProductTree()
    {
        var root = TestPaths.RepoRoot();
        var files = ScannedProjects.SelectMany(project => SourceFiles(root, project)).ToList();

        Assert.True(files.Count > 1000, $"only {files.Count} product source files found; the scan is broken");
        Assert.All(ScannedProjects, project =>
            Assert.Contains(files, file => Relative(root, file).StartsWith(project + "/", StringComparison.Ordinal)));
    }

    internal static List<string> Violations(IReadOnlyDictionary<string, int> table, IEnumerable<OptionalParameter> found)
    {
        var failures = new List<string>();
        foreach (var group in found.GroupBy(p => p.Project, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var allowed = table.GetValueOrDefault(group.Key);
            var count = group.Count();
            if (count <= allowed) continue;

            failures.Add($"{group.Key}: {count} optional interface-typed constructor parameters against a table of {allowed}. " +
                         "The new one is among these:\n    " + string.Join("\n    ", group));
        }

        return failures;
    }

    private static string FailureMessage(IReadOnlyCollection<string> failures) =>
        "A constructor gained an optional dependency (IFoo? foo = null). Make it required and let the composition " +
        "root (DotCharter.UI/App*.cs) pass it; a test passes a fake (FakeLocalization.English() / KeysOnly(), " +
        "Infrastructure/Fake*). If a host genuinely lacks the service (RigLab, the gallery), give that host a real " +
        "one rather than an optional parameter. When a count FALLS, lower its row in " +
        "OptionalDependencyRatchetTests.Allowed in the same change; never raise one.\n  " +
        string.Join("\n  ", failures);

    private static IEnumerable<OptionalParameter> Scan(string root)
    {
        foreach (var project in ScannedProjects)
        {
            foreach (var file in SourceFiles(root, project))
            {
                foreach (var parameter in ParametersIn(project, Relative(root, file), File.ReadAllText(file)))
                    yield return parameter;
            }
        }
    }

    internal static IEnumerable<OptionalParameter> ParametersIn(string project, string file, string source)
    {
        var code = SourceText.StripComments(source);
        foreach (Match match in OptionalInterfaceParameter().Matches(code))
        {
            if (!IsConstructorParameterList(code, match.Index)) continue;

            var line = 1 + code.AsSpan(0, match.Index).Count('\n');
            yield return new OptionalParameter(project, file, line, match.Groups["type"].Value, match.Groups["name"].Value);
        }
    }

    // Walks back to the '(' that opens the list the parameter sits in, then reads
    // the declaration in front of it: a constructor's name follows a modifier
    // (`public Foo(`) or, for a primary constructor, `class` / `record` / `struct`;
    // a method's or delegate's name follows its return type.
    private static bool IsConstructorParameterList(string code, int index)
    {
        var depth = 0;
        var i = index - 1;
        for (; i >= 0; i--)
        {
            var c = code[i];
            if (c is ')' or ']' or '}' or '>')
            {
                depth++;
            }
            else if (c is '[' or '{' or '<')
            {
                depth--;
            }
            else if (c == '(')
            {
                if (depth == 0) break;
                depth--;
            }

            if (depth < 0) return false;
        }

        if (i < 0) return false;

        var end = SkipBackWhitespace(code, i - 1);
        if (end >= 0 && code[end] == '>')
        {
            var angle = 0;
            for (; end >= 0; end--)
            {
                if (code[end] == '>') angle++;
                else if (code[end] == '<' && --angle == 0) break;
            }

            end = SkipBackWhitespace(code, end - 1);
        }

        var nameStart = end;
        while (nameStart >= 0 && (char.IsLetterOrDigit(code[nameStart]) || code[nameStart] == '_')) nameStart--;
        if (nameStart == end) return false;

        var beforeEnd = SkipBackWhitespace(code, nameStart);
        var beforeStart = beforeEnd;
        while (beforeStart >= 0 && char.IsLetter(code[beforeStart])) beforeStart--;
        return ConstructorModifiers.Contains(code[(beforeStart + 1)..(beforeEnd + 1)]);
    }

    private static int SkipBackWhitespace(string code, int i)
    {
        while (i >= 0 && char.IsWhiteSpace(code[i])) i--;
        return i;
    }

    private static string Relative(string root, string file) => Path.GetRelativePath(root, file).Replace('\\', '/');

    private static IEnumerable<string> SourceFiles(string root, string project)
    {
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, project), "*.cs", SearchOption.AllDirectories))
        {
            var relative = Relative(root, file);
            if (!relative.Contains("/bin/", StringComparison.Ordinal) && !relative.Contains("/obj/", StringComparison.Ordinal))
                yield return file;
        }
    }

    [GeneratedRegex(@"(?<![\w])(?<type>I[A-Z]\w*(?:<[^<>()]*>)?)\?\s+(?<name>[A-Za-z_]\w*)\s*=\s*null\s*(?=[,)])")]
    private static partial Regex OptionalInterfaceParameter();
}
