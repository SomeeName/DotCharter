using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DotCharter.Presentation.Dialogs;
using DotCharter.UI.Views.Shell;

namespace DotCharter.Tests.Presentation;

/// <summary>
///     <b>Every contract member is bound by its view</b> (target-architecture.md § 4):
///     each property and command on a <see cref="ScreenCatalog" /> contract, and on
///     every item contract it reaches (a list's element type, a property typed to
///     another contract), appears in a <c>{Binding}</c> / <c>{CompiledBinding}</c>
///     of the view — under the root <c>x:DataType</c>, or under a
///     <c>DataTemplate x:DataType</c> typed to that item contract, or as a later
///     segment of a dotted path (<c>SelectedRow.Title</c>).
///     <para>
///         A member no view binds is either dead weight on the contract or a
///         feature the screen forgot to draw; both used to be found by
///         <c>RelayCommandReachabilityTests</c>, which this replaces for new
///         screens. <see cref="IDialogScreen{TResult}.Result" /> is the one member
///         a view never binds — the host awaits it.
///     </para>
///     <para>
///         The gaps that existed when the lint arrived are pinned in
///         <see cref="KnownUnbound" />, which only shrinks. The rule is a pure
///         function over XAML text; the probe facts run it on known-bad markup.
///     </para>
/// </summary>
public sealed class ContractBindingLintTests
{
    /// <summary>"View: IContract.Member" gaps found when the lint arrived. Shrink-only: bind it or drop it from the contract, then delete the line.</summary>
    private static readonly HashSet<string> KnownUnbound = new(StringComparer.Ordinal)
    {
        "CountdownScreenView: ICountdownScreen.IsGo",
        "ResultsScreenView: ILeaderboardRow.IsActiveProfile",
        "SongSelectScreenView: ISongSelectScreen.SelectedArrangement",
        "StartScreenView: IProfileTile.IsLefty",
        "TuningCheckScreenView: ITuningCheckScreen.SelectedPath"
    };

    public static TheoryData<string> Contracts
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var entry in ScreenCatalog.Default.Entries) data.Add(entry.Contract.FullName!);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Contracts))]
    public void Every_contract_member_is_bound_by_its_view(string contractName)
    {
        var entry = ScreenCatalog.Default.Entries.Single(e => e.Contract.FullName == contractName);
        var axaml = AxamlOf(entry.ViewType);
        var prefix = entry.ViewType.Name + ": ";
        // A view's markup and the views it hosts (the Library's rows are LibraryRowView, #180).
        var markups = HostedViews(axaml).Prepend(axaml).Select(UiSourceTree.ReadAllText).ToArray();
        var unbound = UnboundMembers(entry.Contract, markups).Select(u => prefix + u).ToList();

        var fresh = unbound.Where(u => !KnownUnbound.Contains(u)).ToList();
        Assert.True(fresh.Count == 0,
            $"{Path.GetFileName(axaml)} binds none of these contract members — bind each, or drop it from the " +
            $"contract (target-architecture.md § 4):\n  {string.Join("\n  ", fresh)}");

        var fixedGaps = KnownUnbound.Where(k => k.StartsWith(prefix, StringComparison.Ordinal) && !unbound.Contains(k)).ToList();
        Assert.True(fixedGaps.Count == 0, $"now bound — delete from KnownUnbound: {string.Join(", ", fixedGaps)}");
    }

    [Fact]
    public void Every_KnownUnbound_entry_names_a_catalog_view_and_a_member_it_reaches()
    {
        var members = ScreenCatalog.Default.Entries
            .SelectMany(e => ReachedContracts(e.Contract)
                .SelectMany(c => BindableMembers(c).Select(m => $"{e.ViewType.Name}: {c.Name}.{m}")))
            .ToHashSet(StringComparer.Ordinal);
        Assert.All(KnownUnbound, k => Assert.Contains(k, members));
    }

    // ── probes ───────────────────────────────────────────────────────────

    public interface IProbeRow : System.ComponentModel.INotifyPropertyChanged
    {
        string Title { get; }

        string Artist { get; }
    }

    public interface IProbeScreen : System.ComponentModel.INotifyPropertyChanged
    {
        string Heading { get; }

        bool HasRows { get; }

        IReadOnlyList<IProbeRow> Rows { get; }

        IProbeRow? Selected { get; }

        System.Windows.Input.ICommand PlayCommand { get; }
    }

    private const string ProbeXaml = """
        <UserControl xmlns="https://github.com/avaloniaui"
                     xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                     xmlns:p="clr-namespace:DotCharter.Tests.Presentation;assembly=DotCharter.Tests"
                     x:DataType="p:ContractBindingLintTests+IProbeScreen">
            <StackPanel>
                <TextBlock Text="{Binding Heading}" IsVisible="{Binding !HasRows}" />
                <TextBlock Text="{CompiledBinding Selected.Artist}" />
                <Button Command="{Binding PlayCommand}" Width="{Binding $parent[Panel].Bounds.Width}" />
                <ItemsControl ItemsSource="{Binding Path=Rows}">
                    <ItemsControl.ItemTemplate>
                        <DataTemplate x:DataType="p:ContractBindingLintTests+IProbeRow">
                            <TextBlock Text="{Binding Title}" />
                        </DataTemplate>
                    </ItemsControl.ItemTemplate>
                </ItemsControl>
            </StackPanel>
        </UserControl>
        """;

    [Fact]
    public void Probe_a_view_that_binds_everything_passes() => Assert.Empty(UnboundMembers(typeof(IProbeScreen), ProbeXaml));

    [Fact]
    public void Probe_a_dropped_binding_names_the_member_and_its_contract()
    {
        var withoutPlay = ProbeXaml.Replace("Command=\"{Binding PlayCommand}\"", "", StringComparison.Ordinal);
        Assert.Equal(["IProbeScreen.PlayCommand"], UnboundMembers(typeof(IProbeScreen), withoutPlay));

        var withoutTemplateTitle = ProbeXaml.Replace("{Binding Title}", "{Binding Artist}", StringComparison.Ordinal);
        Assert.Equal(["IProbeRow.Title"], UnboundMembers(typeof(IProbeScreen), withoutTemplateTitle));
    }

    /// <summary>A row template binds a screen verb through its ancestor's cast DataContext; that reaches the screen.</summary>
    [Fact]
    public void Probe_a_cast_ancestor_binding_counts_for_the_cast_contract()
    {
        var fromRow = ProbeXaml
            .Replace("Command=\"{Binding PlayCommand}\"", "", StringComparison.Ordinal)
            .Replace("<TextBlock Text=\"{Binding Title}\" />",
                "<Button Content=\"{Binding Title}\" Command=\"{Binding $parent[ItemsControl].((p:ContractBindingLintTests+IProbeScreen)DataContext).PlayCommand}\" />",
                StringComparison.Ordinal);
        Assert.Empty(UnboundMembers(typeof(IProbeScreen), fromRow));

        var castToTheRow = fromRow.Replace("(p:ContractBindingLintTests+IProbeScreen)", "(p:ContractBindingLintTests+IProbeRow)",
            StringComparison.Ordinal);
        Assert.Equal(["IProbeScreen.PlayCommand"], UnboundMembers(typeof(IProbeScreen), castToTheRow));
    }

    /// <summary>An inline <c>DataType=</c> binds the type it names, not the enclosing scope.</summary>
    [Fact]
    public void Probe_an_inline_data_type_binding_counts_for_the_named_contract()
    {
        var inline = ProbeXaml.Replace("<TextBlock Text=\"{Binding Title}\" />",
            "<TextBlock />", StringComparison.Ordinal).Replace("<ItemsControl ItemsSource=\"{Binding Path=Rows}\">",
            "<ItemsControl ItemsSource=\"{Binding Path=Rows}\" DisplayMemberBinding=\"{Binding Title, DataType=p:ContractBindingLintTests+IProbeRow}\">",
            StringComparison.Ordinal);
        Assert.Empty(UnboundMembers(typeof(IProbeScreen), inline));

        var onTheScreen = inline.Replace("{Binding Title, DataType=p:ContractBindingLintTests+IProbeRow}", "{Binding Title}",
            StringComparison.Ordinal);
        Assert.Equal(["IProbeRow.Title"], UnboundMembers(typeof(IProbeScreen), onTheScreen));
    }

    [Fact]
    public void Probe_a_binding_under_another_data_type_does_not_count()
    {
        // `Heading` bound only inside the ROW template is a binding to IProbeRow, not to the screen.
        var moved = ProbeXaml
            .Replace("<TextBlock Text=\"{Binding Heading}\" IsVisible=\"{Binding !HasRows}\" />",
                "<TextBlock IsVisible=\"{Binding !HasRows}\" />", StringComparison.Ordinal)
            .Replace("<TextBlock Text=\"{Binding Title}\" />",
                "<TextBlock Text=\"{Binding Title}\" Tag=\"{Binding Heading}\" />", StringComparison.Ordinal);
        Assert.Equal(["IProbeScreen.Heading"], UnboundMembers(typeof(IProbeScreen), moved));
    }

    // ── the rule ─────────────────────────────────────────────────────────

    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static readonly Regex BindingStart = new(@"\{\s*(?:Compiled)?Binding(?=[\s}])", RegexOptions.CultureInvariant);

    private static readonly Regex AncestorCast = new(
        @"^\$parent\[[^\]]*\]\.\(\((?<type>[\w:.+]+)\)DataContext\)\.(?<rest>.+)$", RegexOptions.CultureInvariant);

    /// <summary>"IContract.Member" for every bindable member of <paramref name="contract" /> and its item contracts no binding in any of <paramref name="xamls" /> reaches.</summary>
    internal static List<string> UnboundMembers(Type contract, params string[] xamls)
    {
        var bound = new Dictionary<Type, HashSet<string>>();
        foreach (var (type, names) in xamls.SelectMany(x => BoundMembers(XDocument.Parse(x), contract.Assembly)))
        {
            if (bound.TryGetValue(type, out var known)) known.UnionWith(names);
            else bound[type] = [.. names];
        }

        return ReachedContracts(contract)
            .SelectMany(c => BindableMembers(c)
                .Where(m => !(bound.TryGetValue(c, out var names) && names.Contains(m)))
                .Select(m => $"{c.Name}.{m}"))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The contract and every contract interface reachable through its properties (list elements included).</summary>
    internal static List<Type> ReachedContracts(Type contract)
    {
        var seen = new List<Type>();
        var queue = new Queue<Type>([contract]);
        while (queue.Count > 0)
        {
            var type = queue.Dequeue();
            if (seen.Contains(type)) continue;
            seen.Add(type);
            foreach (var property in AllProperties(type))
            {
                foreach (var reached in ContractTypesIn(property.PropertyType, contract.Assembly))
                    queue.Enqueue(reached);
            }
        }

        return seen;
    }

    private static IEnumerable<Type> ContractTypesIn(Type type, Assembly home)
    {
        if (type.IsGenericType)
        {
            foreach (var arg in type.GetGenericArguments())
            {
                foreach (var inner in ContractTypesIn(arg, home))
                    yield return inner;
            }

            yield break;
        }

        if (type.IsInterface && (IsContractAssembly(type.Assembly) || type.Assembly == home)) yield return type;
    }

    private static bool IsContractAssembly(Assembly assembly) =>
        assembly == typeof(IDialogs).Assembly || assembly == typeof(ContractBindingLintTests).Assembly;

    internal static IEnumerable<string> BindableMembers(Type contract) =>
        AllProperties(contract)
            .Where(p => !(p.DeclaringType is { IsGenericType: true } d && d.GetGenericTypeDefinition() == typeof(IDialogScreen<>)))
            .Select(p => p.Name)
            .Distinct(StringComparer.Ordinal);

    private static IEnumerable<PropertyInfo> AllProperties(Type type) =>
        new[] { type }.Concat(type.GetInterfaces())
            .Where(t => t != typeof(System.ComponentModel.INotifyPropertyChanged))
            .SelectMany(t => t.GetProperties());

    /// <summary>Contract type → member names bound under it, walking <c>x:DataType</c> scopes.</summary>
    private static Dictionary<Type, HashSet<string>> BoundMembers(XDocument doc, Assembly home)
    {
        var bound = new Dictionary<Type, HashSet<string>>();
        Walk(doc.Root!, null);
        return bound;

        void Walk(XElement element, Type? scope)
        {
            if (element.Attribute(X + "DataType") is { } declared)
                scope = ResolveType(element, declared.Value, home) ?? scope;

            foreach (var attribute in element.Attributes())
                Record(attribute.Value, scope, element);
            if (element.Name.LocalName is "Binding" or "CompiledBinding" && element.Attribute("Path") is { } path)
                RecordPath(path.Value, scope, element);

            foreach (var child in element.Elements()) Walk(child, scope);
        }

        void Record(string value, Type? scope, XElement element)
        {
            foreach (Match m in BindingStart.Matches(value))
            {
                var args = ArgumentsAfter(value, m.Index + m.Length);
                // `{Binding Label, DataType=c:IOption}` (a ComboBox's DisplayMemberBinding) binds the named type.
                var typed = args.Select(a => a.Trim()).FirstOrDefault(a => a.StartsWith("DataType=", StringComparison.Ordinal));
                var bindingScope = typed is null ? scope : ResolveType(element, typed["DataType=".Length..].Trim(), home);
                RecordPath(PathOf(args), bindingScope, element);
            }
        }

        void RecordPath(string path, Type? scope, XElement element)
        {
            path = path.TrimStart('!').Trim();
            // A row template reaching the screen: `$parent[ItemsControl].((c:IScreen)DataContext).Verb`.
            if (AncestorCast.Match(path) is { Success: true } cast)
            {
                scope = ResolveType(element, cast.Groups["type"].Value, home);
                path = cast.Groups["rest"].Value;
            }

            if (scope is null || path.Length == 0 || path[0] is '$' or '#' or '(' or '.') return;

            var type = scope;
            foreach (var raw in path.Split('.'))
            {
                var name = raw.Split('[')[0].Trim();
                if (type is null || name.Length == 0) return;
                if (!bound.TryGetValue(type, out var names)) bound[type] = names = new HashSet<string>(StringComparer.Ordinal);
                names.Add(name);
                type = AllProperties(type).FirstOrDefault(p => p.Name == name)?.PropertyType;
            }
        }
    }

    /// <summary>The top-level, comma-separated arguments of the markup extension opened before <paramref name="start" />.</summary>
    private static List<string> ArgumentsAfter(string value, int start)
    {
        var args = new List<string>();
        var depth = 0;
        var from = start;
        for (var i = start; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '{')
            {
                depth++;
            }
            else if (c == '}' && depth-- == 0)
            {
                args.Add(value[from..i]);
                break;
            }
            else if (c == ',' && depth == 0)
            {
                args.Add(value[from..i]);
                from = i + 1;
            }
        }

        return args;
    }

    private static string PathOf(List<string> args)
    {
        foreach (var arg in args.Select(a => a.Trim()).Where(a => a.Length > 0))
            if (arg.StartsWith("Path=", StringComparison.Ordinal)) return arg["Path=".Length..];
        var positional = args.Select(a => a.Trim()).FirstOrDefault(a => a.Length > 0 && !a.Contains('=', StringComparison.Ordinal));
        return positional ?? "";
    }

    private static Type? ResolveType(XElement element, string declared, Assembly home)
    {
        var colon = declared.IndexOf(':');
        if (colon < 0) return null;
        var xmlns = element.GetNamespaceOfPrefix(declared[..colon])?.NamespaceName;
        if (xmlns is null) return null;

        var ns = xmlns.StartsWith("using:", StringComparison.Ordinal) ? xmlns["using:".Length..]
            : xmlns.StartsWith("clr-namespace:", StringComparison.Ordinal) ? xmlns["clr-namespace:".Length..].Split(';')[0]
            : null;
        if (ns is null) return null;

        var name = $"{ns}.{declared[(colon + 1)..]}";
        return typeof(IDialogs).Assembly.GetType(name) ?? typeof(ContractBindingLintTests).Assembly.GetType(name)
               ?? home.GetType(name);
    }

    /// <summary>The markup of every DotCharter view element <paramref name="axaml" /> instantiates
    /// (an element whose namespace maps to <c>using:DotCharter.UI.Views.…</c> and whose type is a
    /// view with its own .axaml).</summary>
    private static IEnumerable<string> HostedViews(string axaml)
    {
        var doc = XDocument.Parse(UiSourceTree.ReadAllText(axaml));
        return doc.Descendants()
            .Where(e => e.Name.NamespaceName.StartsWith("using:", StringComparison.Ordinal))
            .Select(e => (Namespace: UiSourceTree.UiNamespace(e.Name.NamespaceName["using:".Length..])!, Element: e))
            .Where(e => e.Namespace.StartsWith("DotCharter.UI.Views.", StringComparison.Ordinal))
            .Select(e => Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI",
                Path.Combine(e.Namespace["DotCharter.UI.".Length..].Split('.')), e.Element.Name.LocalName + ".axaml"))
            .Select(UiSourceTree.Resolve)
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string AxamlOf(Type view)
    {
        var dir = TestPaths.RepoRoot();

        var relative = UiSourceTree.UiNamespace(view.Namespace)!["DotCharter.UI.".Length..].Replace('.', Path.DirectorySeparatorChar);
        var path = UiSourceTree.Resolve(Path.Combine(dir, "DotCharter.UI", relative, view.Name + ".axaml"));
        Assert.True(File.Exists(path), $"{view.FullName}: expected its markup at {path}");
        return path;
    }
}
