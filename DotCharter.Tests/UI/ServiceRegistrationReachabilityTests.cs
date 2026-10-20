using System.Text.RegularExpressions;
using DotCharter.UI;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.UI;

/// <summary>
///     Every service the composition root registers must be REACHED by product
///     code outside the composition root.
///     <para>
///         The root <c>AGENTS.md</c> states this as law — <i>"A new service gets
///         a lint that it is REACHED, not that it is registered"</i> — and until
///         2026-08-29 nothing made a new service get one. There were seventeen
///         per-feature reachability classes (Coop, PowerTool, Eq, Gameplay,
///         PhraseCatalog, ProfileToneRig, …), each written after its own feature
///         shipped dark, and no mechanism that covered the eighteenth.
///     </para>
///     <para>
///         The failure this catches is specific and has shipped at least three
///         times. An <c>AddSingleton</c> factory is not invoked until someone
///         asks for the type, so a registered-but-unresolved service compiles,
///         keeps its own passing unit tests, and reads as shipped while every
///         method on it is dead code. <c>CoopRigApplier</c> — the whole ABI v28
///         per-player audio feature — spent a release exactly that way.
///     </para>
///     <para>
///         <b>The subject is the SERVICE type, not the implementation.</b> That
///         is what a consumer names to get the thing: an interface nobody
///         injects is a dead registration however alive its implementation
///         looks, and an implementation named only inside a factory lambda in
///         <c>App*.cs</c> is perfectly normal. Same rule as
///         <c>CoopReachabilityTests</c>, which happens to look at concrete names
///         only because those services are self-registered.
///     </para>
///     <para>
///         Comments are stripped before matching, because a <c>see cref</c> is
///         exactly as inert as no reference at all — the mistake this repo's
///         guides have already made twice with greps that counted prose. XAML is
///         scanned too: a converter or a design-time <c>x:DataType</c> is a real
///         use the C# scan cannot see.
///     </para>
///     <para>
///         Every <c>App*.cs</c> file is excluded, not just <c>App.axaml.cs</c> —
///         <b>a registration is not a use</b>, and the composition root is one
///         class over nine partial files. That exclusion inverting is a recorded
///         hazard: when registrations moved to <c>App.AudioServices.cs</c>,
///         <c>CoopReachabilityTests</c> briefly stopped excluding them and the
///         registration would have counted as the use it was meant to discount.
///     </para>
///     <para>
///         <b>Proved non-vacuous 2026-08-29, and the proof found the one thing
///         that would have made it useless.</b> Two throwaway registrations were
///         added to the root — a self-registered concrete and an
///         interface/implementation pair, neither named anywhere else — and the
///         lint reported both. With
///         <see cref="BlankOutDeclarations" /> removed it reported NEITHER: a
///         dead <c>IFoo</c> still appears in <c>interface IFoo</c> and in
///         <c>class Foo : IFoo</c>, so without that step this whole lint passes
///         on its own subject's declaration. Do not "simplify" it away.
///     </para>
/// </summary>
public sealed class ServiceRegistrationReachabilityTests
{
    /// <summary>
    ///     Registered service types that no product code names outside the
    ///     composition root, each with the reason it is nonetheless alive.
    ///     <para>
    ///         <b>An entry here must name a path the lint CANNOT see</b> —
    ///         reflection, a XAML binding this scan misses, a framework
    ///         contract resolved by the container itself. "It looked dead but I
    ///         did not want a red build" is exactly how this kind of allow-list
    ///         defeats its own test; the remedy for a genuinely dead
    ///         registration is to delete it or wire it up, and either way that
    ///         is a decision for a human, not a line in this array.
    ///     </para>
    /// </summary>
    private static readonly (string Type, string Reason)[] Exclusions = [];

    [Fact]
    public void EveryRegisteredServiceType_IsNamedByProductCodeOutsideTheCompositionRoot()
    {
        var services = new ServiceCollection();
        App.ConfigureServices(services, new JsonConfigurationService());

        var subjects = services
            .Select(d => d.ServiceType)
            .Where(t => t.Namespace?.StartsWith("DotCharter.", StringComparison.Ordinal) == true)
            .Select(SimpleName)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        // A keeper: the container is built from the real root, so an empty or
        // tiny set means ConfigureServices moved or threw and this lint is
        // scanning nothing. 150+ DotCharter service types are registered today.
        Assert.True(subjects.Count >= 100,
            $"only {subjects.Count} DotCharter service types are registered — App.ConfigureServices is not " +
            "being read, and this lint would pass over almost nothing.");

        var sources = ProductSources();
        Assert.True(sources.Count >= 800,
            $"only scanned {sources.Count} product sources — the glob is broken.");

        var excluded = Exclusions.Select(e => e.Type).ToHashSet(StringComparer.Ordinal);
        foreach (var name in excluded)
            Assert.Contains(name, subjects);

        var unreached = subjects
            .Where(n => !excluded.Contains(n))
            .Where(n => !sources.Any(code => Regex.IsMatch(code, $@"\b{Regex.Escape(n)}\b")))
            .ToList();

        Assert.True(unreached.Count == 0,
            $"these service types are REGISTERED but named by no product code outside App*.cs " +
            $"({unreached.Count}). An AddSingleton factory is never invoked until someone asks for the " +
            "type, so each of these is a registration nothing resolves — the CoopRigApplier shape, which " +
            "shipped a whole feature dark. Wire it up, delete it, or add it to Exclusions with a REASON " +
            "naming the path this lint cannot see (reflection, a XAML binding):\n  " +
            string.Join("\n  ", unreached));
    }

    /// <summary>Generic service types carry an arity suffix on
    /// <c>Type.Name</c>; the source names them without it.</summary>
    private static string SimpleName(Type t)
    {
        var name = t.Name;
        var tick = name.IndexOf('`');
        return tick < 0 ? name : name[..tick];
    }

    /// <summary>
    ///     Comment-stripped product source, every project, plus the shipped
    ///     XAML. <c>App*.cs</c> is excluded everywhere — a registration is not a
    ///     use, and the composition root is one class over nine partial files.
    /// </summary>
    private static List<string> ProductSources()
    {
        string[] projects =
        [
            "DotCharter.UI", "DotCharter.Services", "DotCharter.Tone", "DotCharter.Core",
            "DotCharter.Analysis", "DotCharter.Rendering", "DotCharter.Localization",
            // Screen view models inject INavigator / IDialogs / Services here.
            "DotCharter.Presentation"
        ];

        var sources = new List<string>();
        foreach (var project in projects)
        {
            var root = Path.Combine(TestPaths.RepoRoot(), project);
            Assert.True(Directory.Exists(root), $"{project} no longer exists — the scan would miss it.");

            foreach (var file in UiSourceTree.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                if (IsBuildOutput(file)) continue;
                if (Path.GetFileName(file).StartsWith("App.", StringComparison.OrdinalIgnoreCase)) continue;
                // An <Area>Module.Register (target-architecture.md § 3) is the composition root split by area.
                if (Path.GetFileName(file).EndsWith("Module.cs", StringComparison.Ordinal)) continue;
                sources.Add(BlankOutDeclarations(StripCommentsFromCode(UiSourceTree.ReadAllText(file))));
            }

            foreach (var file in UiSourceTree.EnumerateFiles(root, "*.axaml", SearchOption.AllDirectories))
            {
                if (IsBuildOutput(file)) continue;
                sources.Add(StripXmlComments(UiSourceTree.ReadAllText(file)));
            }
        }

        return sources;
    }

    private static bool IsBuildOutput(string file) =>
        file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
        file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    private static string StripCommentsFromCode(string code)
    {
        code = Regex.Replace(code, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        return Regex.Replace(code, @"//[^\r\n]*", " ");
    }

    /// <summary>
    ///     Blanks out type DECLARATIONS so that declaring a type, or
    ///     implementing an interface, does not count as reaching it.
    ///     <para>
    ///         Without this the lint is nearly vacuous: <c>IFoo</c> that nobody
    ///         injects still appears in <c>IFoo.cs</c> (<c>interface IFoo</c>)
    ///         and in <c>Foo.cs</c> (<c>class Foo : IFoo</c>), so a dead
    ///         registration reads as reached by its own declaration. This is the
    ///         mechanical equivalent of <c>CoopReachabilityTests</c> excluding
    ///         the service's OWN FOLDER.
    ///     </para>
    ///     <para>
    ///         Two things are blanked and nothing else: the declared NAME after
    ///         <c>class</c>/<c>interface</c>/<c>record</c>/<c>struct</c>/<c>enum</c>,
    ///         and the base/constraint list between <c>:</c> and the body. A
    ///         primary constructor's parameter list is deliberately kept — those
    ///         parameters are exactly the injection sites this lint is looking
    ///         for.
    ///     </para>
    /// </summary>
    private static string BlankOutDeclarations(string code)
    {
        // class Foo<T>(IBar bar) : IBaseA, IBaseB where T : IThing {   →  the
        // name, the bases and the constraints go; "(IBar bar)" stays.
        code = Regex.Replace(
            code,
            @"\b(?<kw>class|interface|record|struct|enum)\s+\w+(\s*<[^>{}]*>)?(?<ctor>\s*\([^()]*\))?(\s*:[^{;]*)?",
            m => m.Groups["kw"].Value + " __decl__" + m.Groups["ctor"].Value);

        return code;
    }

    private static string StripXmlComments(string xaml) =>
        Regex.Replace(xaml, @"<!--.*?-->", " ", RegexOptions.Singleline);
}
