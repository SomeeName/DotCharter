using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Logging;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using DotCharter.Core.Domain.Vocals;
using DotCharter.UI;
using DotCharter.UI.Views.Shell;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>How bad a single sweep finding is.</summary>
public enum UiSweepSeverity
{
    /// <summary>Reported and drawn on the contact sheet; does not fail the sweep.</summary>
    Warning,

    /// <summary>Fails the sweep. Reserved for the fault classes that mean the
    /// surface is broken in the product, not merely imperfect.</summary>
    Error
}

/// <summary>One thing the sweep noticed about one surface.</summary>
/// <param name="Surface">The view type's name.</param>
/// <param name="Severity">Whether this fails the sweep.</param>
/// <param name="Kind">Stable finding class, used to group the summary.</param>
/// <param name="Detail">Human-readable specifics, including the control involved.</param>
public sealed record UiSweepFinding(string Surface, UiSweepSeverity Severity, string Kind, string Detail);

/// <summary>Where a surface's DataContext came from — reported so a green sweep
/// cannot be confused with a sweep that measured empty chrome.</summary>
public enum UiSweepDataContextSource
{
    /// <summary>The markup declared no root <c>x:DataType</c>, so nothing was seeded.</summary>
    NotDeclared,

    /// <summary>The live instance reachable from the real shell view-model graph.</summary>
    ShellGraph,

    /// <summary>Resolved from the app's real DI container.</summary>
    Container,

    /// <summary>Built by the container from the view model's own constructor —
    /// the per-use view models the app itself news up when a surface opens.</summary>
    Activated,

    /// <summary>Declared, but nothing could build one. The surface is still
    /// swept; checks that need real data stand down for it.</summary>
    Unavailable
}

/// <summary>
///     The outcome for one surface.
///     <para>
///         <c>HiddenSurfacesRealized</c> counts the <c>IsVisible=false</c>
///         sub-surfaces of this view that were realized and swept on top of the
///         visible tree (zero for a view that gates nothing).
///         <c>ControlsChecked</c> is how many controls the checks actually
///         REACHED — the honest coverage number, and the one that moves when a
///         gated screen is realized. <c>VisualCount</c> counts the whole subtree
///         whether or not anything looked at it.
///         <c>InheritedStyles</c> lists the style grammars the sweep had to
///         supply because the view inherits them from its HOST rather than
///         including them itself — without those, the view lays out bare and
///         every measurement is of an unstyled tree.
///     </para>
///     <para>
///         <c>SeededLists</c> is how many empty <c>ItemsControl</c> sources were
///         given one item so their item template would be laid out at all;
///         <c>RealizedRows</c> is how many item containers the surface actually
///         ended up with. They are reported SEPARATELY because seeding and
///         realizing are two different things — adding to a source only queues a
///         layout invalidation for that control, so a sweep can seed every list
///         in the app and still measure none of them.
///         <c>RealizedRows</c> is counted AFTER the hidden sub-surfaces are
///         realized, because the two mechanisms compound: a list inside a gated
///         screen is in the tree all along (so it is seeded) but is measured
///         only once that screen is realized.
///     </para>
/// </summary>
public sealed record UiSweepResult(
    string Surface,
    string Group,
    UiSweepDataContextSource DataContextSource,
    string DataContextType,
    Rect Bounds,
    int VisualCount,
    int ControlsChecked,
    int HiddenSurfacesRealized,
    IReadOnlyList<string> InheritedStyles,
    int SeededLists,
    int RealizedRows,
    double ElapsedMs,
    string? ImageFile,
    IReadOnlyList<UiSweepFinding> Findings,
    AccessibilityAuditResult? Accessibility = null)
{
    /// <summary>True when nothing at <see cref="UiSweepSeverity.Error" /> was found.</summary>
    public bool Passed => Findings.All(f => f.Severity != UiSweepSeverity.Error);
}

/// <summary>Everything one sweep produced.</summary>
public sealed record UiSweepReport(
    IReadOnlyList<UiSweepResult> Results,
    IReadOnlyList<(string Surface, string Reason)> Exclusions,
    bool CapturedImages)
{
    /// <summary>Every finding across every surface, in sweep order.</summary>
    public IEnumerable<UiSweepFinding> Findings => Results.SelectMany(r => r.Findings);

    /// <summary>The findings that fail the sweep.</summary>
    public IReadOnlyList<UiSweepFinding> Errors =>
        [.. Findings.Where(f => f.Severity == UiSweepSeverity.Error)];

    /// <summary>The written summary the harness prints. Deliberately counts by
    /// CLASS: "9 clipped labels" is actionable, a wall of individual lines is
    /// not — the individual lines follow, and the contact sheet has the rest.</summary>
    public string Summary()
    {
        var sb = new StringBuilder();
        var passed = Results.Count(r => r.Passed);
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"UI SWEEP — {Results.Count} surfaces swept, {passed} clean, {Results.Count - passed} with errors");

        var bySource = Results.GroupBy(r => r.DataContextSource)
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Count()} {g.Key}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  data contexts: {string.Join(", ", bySource)}");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"  visuals laid out: {Results.Sum(r => r.VisualCount):N0}   " +
            $"controls CHECKED: {Results.Sum(r => r.ControlsChecked):N0}   " +
            $"layout wall time: {Results.Sum(r => r.ElapsedMs):N0} ms (logged, never asserted)");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"  hidden sub-surfaces realized: {Results.Sum(r => r.HiddenSurfacesRealized)} " +
            $"across {Results.Count(r => r.HiddenSurfacesRealized > 0)} surfaces");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"  lists seeded: {Results.Sum(r => r.SeededLists)} across " +
            $"{Results.Count(r => r.SeededLists > 0)} surfaces, " +
            $"{Results.Sum(r => r.RealizedRows)} item containers realized");

        var inherited = Results.Where(r => r.InheritedStyles.Count > 0).ToList();
        if (inherited.Count > 0)
        {
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"  style grammars supplied from a HOST view ({inherited.Count} surfaces):");
            foreach (var r in inherited)
            {
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"    {r.Surface,-28} {string.Join(", ", r.InheritedStyles.Select(Path.GetFileName))}");
            }
        }

        sb.AppendLine("  findings by class:");
        var byKind = Findings
            .GroupBy(f => (f.Kind, f.Severity))
            .OrderByDescending(g => g.Key.Severity)
            .ThenByDescending(g => g.Count());
        var any = false;
        foreach (var g in byKind)
        {
            any = true;
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"    {g.Key.Severity,-7} {g.Key.Kind,-26} {g.Count(),4}  " +
                $"across {g.Select(f => f.Surface).Distinct().Count()} surfaces");
        }

        if (!any) sb.AppendLine("    (none)");

        sb.AppendLine(CultureInfo.InvariantCulture, $"  exclusions ({Exclusions.Count}):");
        foreach (var (surface, reason) in Exclusions)
            sb.AppendLine(CultureInfo.InvariantCulture, $"    {surface,-28} {reason}");

        sb.AppendLine(CapturedImages
            ? $"  artifacts: {UiSweepRunner.ArtifactDirectory} (index.html + " +
              $"{Results.Count(r => r.ImageFile != null)} captures)"
            : $"  artifacts: {UiSweepRunner.ArtifactDirectory} (index.html; " +
              $"set {UiSweepRunner.CaptureEnvVar}=1 for captures)");

        var detail = Findings
            .OrderByDescending(f => f.Severity)
            .ThenBy(f => f.Surface, StringComparer.Ordinal)
            .ToList();
        if (detail.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("  every finding:");
            foreach (var f in detail)
            {
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"    [{f.Severity}] {f.Surface}: {f.Kind} — {f.Detail}");
            }
        }

        return sb.ToString();
    }
}

/// <summary>
///     Collects Avalonia's own diagnostics while a surface is laid out.
///     <para>
///         This is the only mechanism that sees a binding which resolved to
///         NOTHING. A <c>Command="{Binding Save}"</c> whose path does not exist
///         leaves the property at its default (null) with no value ever having
///         been pushed, so no runtime property inspection can tell it apart from
///         a button that never declared a command — but Avalonia logs the failed
///         path at <see cref="LogEventLevel.Warning" /> in
///         <see cref="LogArea.Binding" /> with the target property in the
///         message. That is the signal, and it is the one that turns "the button
///         draws and does nothing" into a test failure.
///     </para>
/// </summary>
internal sealed class UiSweepLogSink : ILogSink
{
    private readonly List<(string Area, string Message)> _records = [];

    /// <summary>The sink this one replaced, restored on <see cref="Detach" />.</summary>
    private readonly ILogSink? _previous = Logger.Sink;

    /// <summary>Records land here while a surface is under test.</summary>
    public List<(string Area, string Message)> Drain()
    {
        var copy = new List<(string, string)>(_records);
        _records.Clear();
        return copy;
    }

    public void Detach() => Logger.Sink = _previous;

    public static UiSweepLogSink Attach()
    {
        var sink = new UiSweepLogSink();
        Logger.Sink = sink;
        return sink;
    }

    public bool IsEnabled(LogEventLevel level, string area) =>
        level >= LogEventLevel.Warning &&
        area is LogArea.Binding or LogArea.Property or LogArea.Control or LogArea.Visual;

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate) =>
        Record(area, messageTemplate, []);

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate,
        params object?[] propertyValues) =>
        Record(area, messageTemplate, propertyValues);

    /// <summary>Substitutes the values into the template the same way a real sink
    /// would, so the recorded line names the control and the property.</summary>
    private void Record(string area, string template, object?[] values)
    {
        var text = new StringBuilder(template.Length + 64);
        var index = 0;
        for (var i = 0; i < template.Length; i++)
        {
            if (template[i] != '{')
            {
                text.Append(template[i]);
                continue;
            }

            var close = template.IndexOf('}', i);
            if (close < 0)
            {
                text.Append(template[i]);
                continue;
            }

            text.Append(index < values.Length ? Describe(values[index]) : template[i..(close + 1)]);
            index++;
            i = close;
        }

        _records.Add((area, text.ToString()));
    }

    private static string Describe(object? value) => value switch
    {
        null => "(null)",
        AvaloniaProperty p => p.Name,
        Type t => t.Name,
        Control c => c.GetType().Name,
        _ => value.ToString() ?? "(null)"
    };
}

/// <summary>
///     The repo-derived catalogue of every UI surface the sweep visits, plus the
///     hand-maintained exclusions — each of which carries its reason inline, so
///     "why is X not swept" is answerable from this file alone.
/// </summary>
public static class UiSweepCatalog
{
    /// <summary>Views that genuinely cannot be built and laid out standalone.
    /// Everything else in <c>DotCharter.UI/Views</c> is swept.</summary>
    public static readonly IReadOnlyDictionary<string, string> Exclusions =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Preview3DView"] =
                "hosts the Vulkan viewport (Preview3DComposedCanvas); attaching it initializes a " +
                "real render device, which is what DotCharter.Tests/Rendering/Headless covers instead",
            ["MainWindow"] =
                "the shell itself — it OWNS the sweep's host window and the prewarm pipeline; " +
                "covered by UiPerfSmokeTests/ShellConstructionUiPerfSmokeTests"
        };

    /// <summary>The root element names that mark an .axaml as a resource
    /// dictionary rather than a view. Derived, not listed: a new style file
    /// needs no maintenance here.</summary>
    private static readonly string[] NonViewRoots = ["ResourceDictionary", "Styles"];

    /// <summary>
    ///     Every view type under <c>DotCharter.UI.Views</c> that can be
    ///     constructed with no arguments — read off the compiled assembly, so a
    ///     view added to the repo is swept the day it compiles.
    /// </summary>
    public static IReadOnlyList<Type> ViewTypes() =>
    [
        // The rig's views live in DotCharter.Tone.UI (DotCharter.Tone.UI.Views.*,
        // mirroring DotCharter.UI.Views.*), so the sweep reads both assemblies.
        .. typeof(App).Assembly.GetTypes()
            .Concat(typeof(DotCharter.Tone.UI.Views.PlayMode.AmpFxRigView).Assembly.GetTypes())
            .Where(t => t is { IsAbstract: false, IsGenericTypeDefinition: false })
            .Where(t => typeof(Control).IsAssignableFrom(t))
            .Where(t => UiSourceTree.UiNamespace(t.Namespace)?.StartsWith("DotCharter.UI.Views", StringComparison.Ordinal) == true)
            .Where(t => t.GetConstructor(Type.EmptyTypes) != null)
            .Where(t => !Exclusions.ContainsKey(t.Name))
            .OrderBy(t => UiSourceTree.UiNamespace(t.Namespace), StringComparer.Ordinal)
            .ThenBy(t => t.Name, StringComparer.Ordinal)
    ];

    /// <summary>Every .axaml under <c>Views/</c> that declares a VIEW (root
    /// element is not a resource dictionary). The source-side half of the
    /// coverage check.</summary>
    public static IReadOnlyList<string> ViewAxamlFiles()
    {
        var root = UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views"));
        return
        [
            .. UiSourceTree.EnumerateFiles(root, "*.axaml", SearchOption.AllDirectories)
                .Where(f => !NonViewRoots.Contains(RootElementName(f), StringComparer.Ordinal))
                .OrderBy(f => f, StringComparer.Ordinal)
        ];
    }

    /// <summary>The local name of a XAML file's root element.</summary>
    public static string RootElementName(string axamlPath)
    {
        try { return XDocument.Load(axamlPath).Root?.Name.LocalName ?? ""; }
        catch (System.Xml.XmlException) { return ""; }
    }

    /// <summary>
    ///     The CLR type named by a view's ROOT <c>x:DataType</c>, or null when
    ///     the markup declares none. This is the authoritative statement of what
    ///     the view expects to be bound to — the compiled-binding declaration the
    ///     product itself relies on — so the sweep seeds from it rather than from
    ///     a hand-written view→view-model table that would drift.
    /// </summary>
    private static readonly System.Reflection.Assembly[] DataTypeAssemblies =
        [typeof(App).Assembly, typeof(DotCharter.Tone.UI.Views.PlayMode.AmpFxRigView).Assembly,
            typeof(DotCharter.Presentation.Contracts.PlayMode.IStartScreen).Assembly];

    public static Type? DeclaredDataType(string axamlPath)
    {
        XElement? root;
        try { root = XDocument.Load(axamlPath).Root; }
        catch (System.Xml.XmlException) { return null; }

        if (root is null) return null;

        var xamlNs = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml");
        var declared = root.Attribute(xamlNs + "DataType")?.Value;
        if (string.IsNullOrWhiteSpace(declared)) return null;

        var colon = declared.IndexOf(':');
        if (colon < 0) return null;

        var prefix = declared[..colon];
        var typeName = declared[(colon + 1)..];
        var xmlns = root.GetNamespaceOfPrefix(prefix)?.NamespaceName;
        if (xmlns is null) return null;

        string clrNamespace;
        if (xmlns.StartsWith("clr-namespace:", StringComparison.Ordinal)) clrNamespace = xmlns["clr-namespace:".Length..];
        else if (xmlns.StartsWith("using:", StringComparison.Ordinal)) clrNamespace = xmlns["using:".Length..];
        else return null;

        var semicolon = clrNamespace.IndexOf(';');
        if (semicolon >= 0) clrNamespace = clrNamespace[..semicolon];

        // Views bind to their own view models (the UI assembly) or to a screen
        // contract in DotCharter.Presentation (the redesign views, via `using:`).
        return DataTypeAssemblies
            .Select(a => a.GetType($"{clrNamespace}.{typeName}", throwOnError: false))
            .FirstOrDefault(t => t != null);
    }

    /// <summary>The .axaml that declares <paramref name="viewType" />, or null
    /// when it has none (a code-only control).</summary>
    public static string? AxamlFor(Type viewType) =>
        ViewAxamlFiles().FirstOrDefault(f =>
            string.Equals(Path.GetFileNameWithoutExtension(f), viewType.Name, StringComparison.Ordinal));

    /// <summary>
    ///     The style grammars a view needs but does NOT include — it inherits them
    ///     from the view that hosts it in the product, and the sweep hosts it
    ///     alone.
    ///     <para>
    ///         This is not a nicety. Six of the seven inspector tab views and
    ///         <c>ChordTemplateEditorView</c> carry no <c>StyleInclude</c> at all:
    ///         <c>InspectorView</c> includes <c>InspectorStyles.axaml</c> once for
    ///         all of them. Swept bare, ~150 controls across those tabs match no
    ///         selector, so every bound measured here is of an UNSTYLED tree —
    ///         which both masks real clipping and invents clipping that the
    ///         product does not have. The same is true of anything else hosted
    ///         inside a view that owns the grammar.
    ///     </para>
    ///     <para>
    ///         Derived from the markup, never listed: a view with no include of
    ///         its own is matched against every other view that declares it as an
    ///         ELEMENT, and that host's includes are used — recursing when the
    ///         host has none either (<c>ChordTemplateEditorView</c> →
    ///         <c>SoundInspectorView</c> → <c>InspectorView</c>). A view that
    ///         includes its own grammar inherits nothing.
    ///     </para>
    /// </summary>
    public static IReadOnlyList<string> InheritedStyleIncludes(Type viewType)
    {
        var file = AxamlFor(viewType);
        if (file is null || StyleIncludesIn(file).Count > 0) return [];
        return FromHostViews(Path.GetFileNameWithoutExtension(file), depth: 0, seen: []);
    }

    /// <summary>Depth-limited so a mutual reference cannot loop and so a grammar
    /// three hosts away — which the product would not apply either — is not
    /// invented.</summary>
    private static IReadOnlyList<string> FromHostViews(string viewName, int depth, HashSet<string> seen)
    {
        if (depth >= 3 || !seen.Add(viewName)) return [];

        var found = new List<string>();
        foreach (var host in ViewAxamlFiles().Where(f => DeclaresElement(f, viewName)))
        {
            var includes = StyleIncludesIn(host);
            found.AddRange(includes.Count > 0
                ? includes
                : FromHostViews(Path.GetFileNameWithoutExtension(host), depth + 1, seen));
        }

        return [.. found.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>The <c>avares://</c> sources of every <c>StyleInclude</c> in a
    /// view's own markup.</summary>
    public static IReadOnlyList<string> StyleIncludesIn(string axamlPath) =>
    [
        .. System.Text.RegularExpressions.Regex
            .Matches(UiSourceTree.ReadAllText(axamlPath), "<StyleInclude\\s+Source=\"(?<src>[^\"]+)\"")
            .Select(m => m.Groups["src"].Value)
            .Distinct(StringComparer.Ordinal)
    ];

    /// <summary>Whether <paramref name="axamlPath" /> declares
    /// <paramref name="viewName" /> as an element (with or without an xmlns
    /// prefix) — i.e. hosts it.</summary>
    /// <summary>The host declares the view as an element, or builds it in code through a
    /// type it declares (<c>RigModuleEditorView</c>'s <c>RigEditorTemplateSelector</c> builds
    /// the family editors, which rely on <c>RigModuleEditorStyles.axaml</c>, #242).</summary>
    private static bool DeclaresElement(string axamlPath, string viewName) =>
        System.Text.RegularExpressions.Regex.IsMatch(
            UiSourceTree.ReadAllText(axamlPath),
            $@"<(?:[A-Za-z0-9_]+:)?{System.Text.RegularExpressions.Regex.Escape(viewName)}[\s/>]")
        || ViewStyleScopes.BuildsInCode(axamlPath, viewName);
}

/// <summary>
///     Finds a REAL view model for a surface: first the live instance hanging off
///     the shell's own view-model graph, then the app's DI container. Never a
///     hand-built stand-in — an invented object would put invented content in the
///     contact sheet, and a screenshot of invented content is worse than no
///     screenshot.
/// </summary>
internal sealed class UiSweepDataContexts
{
    private readonly Dictionary<Type, object> _fromShellGraph = [];
    private readonly IServiceProvider _services;

    private UiSweepDataContexts(IServiceProvider services) => _services = services;

    /// <summary>
    ///     Builds the seeder over the running app. A real multi-arrangement
    ///     project is put into the shared <c>ProjectState</c> FIRST, through the
    ///     production property, so every collection the shell derives from a song
    ///     is populated by the app's own load path — an <c>ItemsControl</c> with
    ///     no items has no rows, and a sweep of empty chrome misses every item
    ///     template.
    /// </summary>
    public static UiSweepDataContexts Build(IServiceProvider services)
    {
        SeedProjectState(services);

        var seeder = new UiSweepDataContexts(services);
        var shell = services.GetService<DotCharter.UI.ViewModels.Shell.MainWindowViewModel>();
        if (shell != null) seeder.Harvest(shell, depth: 0, seen: []);
        return seeder;
    }

    /// <summary>
    ///     A three-path chart with real notes, beats, sections, lyrics and
    ///     showlights, built by the tests' own <see cref="StressChartFactory" />
    ///     so nothing here invents content that could pass for a real song. The
    ///     song is named for what it is.
    /// </summary>
    private static void SeedProjectState(IServiceProvider services)
    {
        var state = services.GetService<ProjectState>();
        if (state is null) return;

        var chart = StressChartFactory.Chart(soundCount: 240, lengthMinutes: 2);
        var project = new ChartProject
        {
            Chart = { SongName = "UI sweep fixture", Artist = "DotCharter test harness", AlbumName = "(synthetic)" }
        };
        foreach (var beat in chart.Beats) project.Beats.Add(beat);
        foreach (var showlight in chart.Showlights) project.Showlights.Add(showlight);

        foreach (var (name, type, strings) in new[]
                 {
                     ("Lead", ArrangementType.Lead, 6),
                     ("Rhythm", ArrangementType.Rhythm, 6),
                     ("Bass", ArrangementType.Bass, 4)
                 })
        {
            var arrangement = new Arrangement { Name = name, ArrangementType = type, StringCount = strings };
            var level = new ChartLevel { DifficultyIndex = 0 };
            foreach (var note in chart.Notes.Where(n => n.StringIndex < strings)) level.Notes.Add(note);
            foreach (var chord in chart.Chords.Where(c => c.ChordNotes.All(n => n.StringIndex < strings)))
                level.Chords.Add(chord);
            foreach (var shape in chart.HandShapes) level.HandShapes.Add(shape);
            foreach (var anchor in chart.Anchors) level.Anchors.Add(anchor);
            arrangement.Levels.Add(level);
            foreach (var point in chart.EventPoints) arrangement.EventPoints.Add(point);
            foreach (var tone in chart.Tones) arrangement.ToneChanges.Add(tone);
            project.Arrangements.Add(arrangement);
        }

        var vocals = new VocalTrack { Name = "Vocals" };
        foreach (var phrase in chart.LyricPhrases) vocals.Phrases.Add(phrase);
        project.VocalTracks.Add(vocals);

        state.ActiveProject = project;
        state.ActiveArrangement = project.Arrangements[0];
    }

    /// <summary>Records the first live instance of every view-model type
    /// reachable from the shell. Depth-limited: the graph reaches the whole app,
    /// and four levels already covers every surface's own view model.</summary>
    private void Harvest(object node, int depth, HashSet<object> seen)
    {
        if (depth > 4 || !seen.Add(node)) return;

        var type = node.GetType();
        if (type.Name.EndsWith("ViewModel", StringComparison.Ordinal)) _fromShellGraph.TryAdd(type, node);

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0 || !property.CanRead) continue;
            if (!property.PropertyType.IsClass || property.PropertyType == typeof(string)) continue;
            if (!property.PropertyType.FullName?.StartsWith("DotCharter.", StringComparison.Ordinal) ?? true)
                continue;

            object? value;
            try { value = property.GetValue(node); }
            catch (TargetInvocationException) { continue; }

            if (value != null) Harvest(value, depth + 1, seen);
        }
    }

    /// <summary>The instance to bind a surface to, and where it came from.</summary>
    public (object? Value, UiSweepDataContextSource Source) For(Type? declared)
    {
        if (declared is null) return (null, UiSweepDataContextSource.NotDeclared);
        if (_fromShellGraph.TryGetValue(declared, out var live))
            return (live, UiSweepDataContextSource.ShellGraph);

        object? resolved;
        try { resolved = _services.GetService(declared); }
        catch (InvalidOperationException) { resolved = null; }

        if (resolved != null) return (resolved, UiSweepDataContextSource.Container);

        // Not registered — the per-use view models the app news up itself when a
        // surface opens (the bend editor, the component manager). Built from the
        // REAL container, so every dependency is the app's own singleton; a
        // constructor the container cannot satisfy simply leaves the surface
        // unseeded rather than inventing one.
        try { resolved = ActivatorUtilities.CreateInstance(_services, declared); }
        catch (Exception ex) when (ex is InvalidOperationException or MissingMethodException
                                       or TargetInvocationException or ArgumentException)
        {
            resolved = null;
        }

        return resolved != null
            ? (resolved, UiSweepDataContextSource.Activated)
            : (null, UiSweepDataContextSource.Unavailable);
    }
}

/// <summary>
///     Puts ONE item into every list a surface draws but has no data for.
///     <para>
///         Without this the sweep grades an empty frame. A
///         <see cref="ItemsControl" /> with no items realizes no containers, so
///         its <c>ItemTemplate</c> — which is where a list surface keeps almost
///         all of its markup — is never constructed, never styled and never laid
///         out. Every check in the sweep then passes over it by having nothing to
///         look at, and the surface is reported CLEAN. That is how the recovery
///         dialog's row template shipped an overlap the sweep had "checked".
///     </para>
///     <para>
///         It builds the list's own ELEMENT TYPE — never a stand-in — from the
///         real container first, then from the type's own constructor, filling
///         <c>required</c> members recursively because a type that declares them
///         will not survive being left half-built. The values are the type's
///         DEFAULTS, which is the honest limit of this: it proves the template
///         lays out, not that it lays out under every string a user can produce.
///         A list it cannot build is REPORTED (<c>unseeded-list</c>) rather than
///         skipped silently — a reported gap can be closed, a silent one is what
///         this class exists to remove.
///     </para>
/// </summary>
internal static class UiSweepListSeeder
{
    /// <summary>How deep the recursive build may go before giving up. Two levels
    /// covers "a row view model over a model over its metadata", which is the
    /// shape every list in this app has.</summary>
    private const int MaxDepth = 3;

    /// <summary>
    ///     What every string in a seeded item is set to.
    ///     <para>
    ///         It is deliberately not <c>""</c>. An empty string measures zero, so
    ///         a row built from type defaults can never clip, never overflow and
    ///         never overlap anything — the sweep would lay the template out and
    ///         still see none of the faults the template can have. The recovery
    ///         dialog is the worked example: its row only collides once the name
    ///         plus its tag outgrow the cell, which no empty string does.
    ///     </para>
    ///     <para>
    ///         It is FIXED rather than random or timestamped so two sweeps of an
    ///         unchanged tree produce the same findings and the same contact
    ///         sheet, and it names itself so nothing in a capture can be mistaken
    ///         for real user data. Length is ordinary-label sized: long enough to
    ///         fill a row's flexible column, short enough not to declare every
    ///         list in the app clipped.
    ///     </para>
    /// </summary>
    internal const string SampleText = "UI sweep — a representative value";

    /// <summary>
    ///     Whether a string on screen came from <see cref="SampleText" /> —
    ///     directly, or composed into a sentence by the row's view model.
    ///     <para>
    ///         The clipped-text check needs this. That class is an ERROR since
    ///         0.53, and it must stay one, but a seeded row's text is 33
    ///         characters because THIS FILE says so — so failing the gate on it
    ///         would make the build depend on the harness's choice of string, not
    ///         on anything the product does. Reported separately instead.
    ///     </para>
    /// </summary>
    public static bool IsSeededValue(string? text) =>
        text is not null && text.Contains(SampleText, StringComparison.Ordinal);

    /// <summary>The instant every seeded date is set to. Fixed for the same
    /// reason <see cref="SampleText" /> is: a sweep must be reproducible, and
    /// `default` renders as nothing or is treated as "absent" by the view models
    /// (which is what hid the recovery row's autosave stamp entirely).</summary>
    private static readonly DateTimeOffset SampleInstant =
        new(2026, 1, 23, 14, 45, 0, TimeSpan.Zero);

    /// <summary>Seeds every empty list under <paramref name="view" />. The
    /// returned pairs MUST be undone by the caller: these collections hang off
    /// DI singletons, exactly like the <c>IsOpen</c> flags.</summary>
    public static List<(System.Collections.IList List, object Item)> Seed(Control view, IServiceProvider services,
        string surface, List<UiSweepFinding> findings)
    {
        var seeded = new List<(System.Collections.IList, object)>();
        var unseeded = new List<string>();

        foreach (var host in view.GetVisualDescendants().OfType<ItemsControl>())
        {
            // Nothing to prove for a list with no template: its rows are whatever
            // the items' ToString produces, and there is no markup to exercise.
            if (host.ItemTemplate is null) continue;
            if (host.ItemCount > 0) continue;
            if (host.ItemsSource is not System.Collections.IList { Count: 0 } list || list.IsReadOnly) continue;

            var elementType = ElementTypeOf(list);
            if (elementType is null)
            {
                unseeded.Add($"{host.GetType().Name}'s source is a non-generic {list.GetType().Name}");
                continue;
            }

            var item = TryBuild(services, elementType, depth: 0);
            if (item is null)
            {
                unseeded.Add($"{host.GetType().Name}'s {elementType.Name} could not be built — " +
                             "its item template was NOT laid out");
                continue;
            }

            try
            {
                list.Add(item);
                seeded.Add((list, item));
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException
                                           or ArgumentException or TargetInvocationException)
            {
                unseeded.Add($"{elementType.Name} was rejected by its own collection: {ex.GetType().Name}");
            }
        }

        UiSweepRunner.EmitFindings(surface, UiSweepSeverity.Warning, "unseeded-list", unseeded, findings);
        return seeded;
    }

    /// <summary>Puts every seeded collection back the way it was found.</summary>
    public static void Unseed(List<(System.Collections.IList List, object Item)> seeded)
    {
        for (var i = seeded.Count - 1; i >= 0; i--)
        {
            try { seeded[i].List.Remove(seeded[i].Item); }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException) { /* left as found is best effort */ }
        }
    }

    /// <summary>Whether a type belongs to this product, i.e. is a chart/session
    /// data type rather than a framework or third-party one. The recursion is
    /// confined to these.</summary>
    private static bool IsOwnType(Type type) =>
        type.Assembly.GetName().Name?.StartsWith("DotCharter", StringComparison.Ordinal) == true;

    /// <summary>An empty <c>List&lt;T&gt;</c> when <paramref name="type" /> is one
    /// of the generic collection interfaces a view model takes, else null.</summary>
    private static object? EmptyCollectionFor(Type type)
    {
        if (!type.IsInterface || !type.IsGenericType) return null;

        var definition = type.GetGenericTypeDefinition();
        if (definition != typeof(IReadOnlyList<>) && definition != typeof(IReadOnlyCollection<>) &&
            definition != typeof(IList<>) && definition != typeof(ICollection<>) &&
            definition != typeof(IEnumerable<>))
        {
            return null;
        }

        return Activator.CreateInstance(typeof(List<>).MakeGenericType(type.GetGenericArguments()[0]));
    }

    /// <summary>The T of an <c>IList&lt;T&gt;</c>, however it is spelled.</summary>
    private static Type? ElementTypeOf(System.Collections.IList list) =>
        list.GetType().GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IList<>))
            ?.GetGenericArguments()[0];

    private static object? TryBuild(IServiceProvider services, Type type, int depth)
    {
        if (depth > MaxDepth) return null;
        if (type == typeof(string)) return SampleText;
        if (type == typeof(DateTimeOffset)) return SampleInstant;
        if (type == typeof(DateTime)) return SampleInstant.UtcDateTime;
        if (type.IsValueType) return Activator.CreateInstance(type);

        // A collection parameter's honest "nothing yet" value is an EMPTY
        // collection, not null — several row view models take an
        // IReadOnlyList<T> of choices and throw on null, and handing them a
        // list of invented choices would be exactly the fabrication this
        // seeder refuses.
        if (EmptyCollectionFor(type) is { } empty) return empty;

        // The CONTAINER is asked exactly once, for the item type itself. Asking
        // it for the types found by walking constructors and properties resolves
        // arbitrary SERVICES — which is how a list seeder ends up constructing
        // the JUCE audio engine and taking the test host down with it. Below the
        // top level this builds DotCharter data types and nothing else, so the
        // walk cannot leave the model layer or fan out across the framework.
        if (depth == 0)
        {
            object? fromContainer;
            try { fromContainer = services.GetService(type); }
            catch (InvalidOperationException) { fromContainer = null; }

            if (fromContainer != null) return fromContainer;
        }

        if (type.IsAbstract || type.IsInterface) return null;
        if (!IsOwnType(type)) return null;

        foreach (var ctor in type.GetConstructors().OrderBy(c => c.GetParameters().Length))
        {
            var parameters = ctor.GetParameters();
            var args = new object?[parameters.Length];
            for (var i = 0; i < parameters.Length; i++)
            {
                args[i] = parameters[i].HasDefaultValue
                    ? parameters[i].DefaultValue
                    : TryBuild(services, parameters[i].ParameterType, depth + 1);
            }

            object instance;
            try { instance = ctor.Invoke(args); }
            catch (Exception ex) when (ex is TargetInvocationException or ArgumentException
                                           or MemberAccessException or NotSupportedException)
            {
                continue;
            }

            FillPresentationMembers(services, instance, depth);
            return instance;
        }

        return null;
    }

    /// <summary>
    ///     Gives the freshly built object the text and dates a row needs in order
    ///     to have a layout at all — and nothing else.
    ///     <para>
    ///         Two reasons this cannot be limited to <c>required</c> members.
    ///         First, <c>required</c> is a COMPILE-TIME contract that reflection
    ///         walks straight past, so a reflectively built
    ///         <c>RecoverableSession</c> arrives with a null <c>Metadata</c> and
    ///         the row view model over it throws on its first line — those must be
    ///         filled or the list cannot be seeded at all. Second, and less
    ///         obviously, an ordinary property left at <c>""</c> or
    ///         <c>default(DateTimeOffset)</c> produces a row that renders NOTHING:
    ///         the recovery row's autosave stamp is bound to
    ///         <c>StringConverters.IsNotNullOrEmpty</c>, so a blank timestamp
    ///         hides the very control the row's name block used to collide with.
    ///         A seeded row that draws nothing is the empty list all over again.
    ///     </para>
    ///     <para>
    ///         Only STRINGS, DATES and nested objects are touched, and only where
    ///         the object left them empty. Booleans, enums and numbers are left
    ///         exactly as the type constructed them — those are the fields that
    ///         decide what a row MEANS (which tag it wears, whether it is
    ///         enabled), and writing them would be inventing state rather than
    ///         giving the existing state something to draw. Collections are left
    ///         alone for the same reason.
    ///     </para>
    ///     <para>Init-only setters are ordinary setters at runtime, so this works
    ///     on them.</para>
    /// </summary>
    private static void FillPresentationMembers(IServiceProvider services, object instance, int depth)
    {
        foreach (var property in instance.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanWrite || !property.CanRead) continue;
            if (property.GetIndexParameters().Length > 0) continue;

            var type = property.PropertyType;
            var isText = type == typeof(string);
            var isDate = type == typeof(DateTimeOffset) || type == typeof(DateTime);
            // A nested object is followed only when it is one of OUR data types
            // and is not itself a collection — same containment rule as the
            // constructor walk, for the same reason.
            var isOwnObject = !type.IsValueType && !typeof(IEnumerable).IsAssignableFrom(type)
                                                && IsOwnType(type);
            if (!isText && !isDate && !isOwnObject) continue;

            try
            {
                var current = property.GetValue(instance);
                var isEmpty = current switch
                {
                    null => true,
                    string s => s.Length == 0,
                    DateTimeOffset d => d == default,
                    DateTime d => d == default,
                    _ => false
                };
                if (!isEmpty) continue;

                var value = TryBuild(services, type, depth + 1);
                if (value != null) property.SetValue(instance, value);
            }
            catch (Exception ex) when (ex is TargetInvocationException or ArgumentException
                                           or MethodAccessException or InvalidOperationException)
            {
                // A member that refuses to be filled leaves the item as built;
                // if that makes it unusable, the caller reports `unseeded-list`.
            }
        }
    }
}

/// <summary>
///     The sweep itself: build every surface, attach it to a real
///     <see cref="Window" />, show it, lay it out, and interrogate the resulting
///     visual tree.
///     <para>
///         Runs entirely under <c>Avalonia.Headless</c>; no window ever reaches
///         the desktop. The whole sweep runs inside ONE
///         <c>HeadlessUnitTestSession.Dispatch</c> — every dispatch can re-enter
///         <c>EnsureIsolatedApplication</c> on a different pool thread and past
///         ~60 of them the second thread to touch the compositor throws.
///     </para>
/// </summary>
public static class UiSweepRunner
{
    /// <summary>Set to 1 to also write a PNG per surface. Follows the
    /// <c>DOTCHARTER_GPU_DUMP</c> precedent — the assertions run either way, and
    /// capture costs a real Skia rasterization per surface.</summary>
    public const string CaptureEnvVar = "DOTCHARTER_UI_SWEEP_DUMP";

    private const double HostWidth = 1600;
    private const double HostHeight = 1000;

    /// <summary>How many findings of one kind to record per surface before
    /// summarizing the rest. One badly-wrapped list can otherwise contribute
    /// hundreds of identical lines and bury everything else.</summary>
    private const int MaxFindingsPerKindPerSurface = 6;

    /// <summary>True when the session was started with real (Skia) drawing, the
    /// only mode in which a frame can be captured.</summary>
    public static bool CaptureEnabled =>
        Environment.GetEnvironmentVariable(CaptureEnvVar) == "1";

    /// <summary>Where the contact sheet and its captures land.</summary>
    public static string ArtifactDirectory =>
        Path.Combine(AppContext.BaseDirectory, "TestResults", "ui-sweep");

    /// <summary>The one sweep this process performs. Several facts read the
    /// same report, and they must read the SAME one: the surfaces share the
    /// app's DI singletons, so a second sweep starts from state the first left
    /// behind and does not reproduce it.</summary>
    private static UiSweepReport? _report;

    /// <summary>
    ///     Sweeps every catalogued surface, once per process. MUST be called on
    ///     the headless session's dispatch thread.
    /// </summary>
    public static UiSweepReport Run() => _report ??= RunCore();

    private static UiSweepReport RunCore()
    {
        var services = App.Services
                       ?? throw new InvalidOperationException(
                           "App.Services is null — the headless app session did not boot.");

        var seeder = UiSweepDataContexts.Build(services);
        var shell = services.GetService<DotCharter.UI.ViewModels.Shell.MainWindowViewModel>();
        var sink = UiSweepLogSink.Attach();
        var results = new List<UiSweepResult>();

        Directory.CreateDirectory(ArtifactDirectory);

        try
        {
            // Drain whatever the project seeding above logged; it belongs to no
            // surface and would otherwise be blamed on the first one.
            sink.Drain();

            foreach (var viewType in UiSweepCatalog.ViewTypes())
                results.Add(Sweep(viewType, seeder, sink, shell));
        }
        finally
        {
            sink.Detach();
        }

        var report = new UiSweepReport(
            results,
            [.. UiSweepCatalog.Exclusions.Select(kv => (kv.Key, kv.Value))],
            CaptureEnabled);

        WriteContactSheet(report);
        return report;
    }

    private static UiSweepResult Sweep(Type viewType, UiSweepDataContexts seeder, UiSweepLogSink sink,
        object? shellViewModel)
    {
        var seededLists = new List<(System.Collections.IList List, object Item)>();
        var realizedRows = 0;
        var name = viewType.Name;
        var group = UiSourceTree.UiNamespace(viewType.Namespace)?.Replace("DotCharter.UI.Views", "", StringComparison.Ordinal)
            .TrimStart('.') is { Length: > 0 } g
            ? g
            : "Shell";

        var axaml = UiSweepCatalog.AxamlFor(viewType);
        var declared = axaml is null ? null : UiSweepCatalog.DeclaredDataType(axaml);
        var (dataContext, source) = seeder.For(declared);

        var findings = new List<UiSweepFinding>();
        var inheritedStyles = UiSweepCatalog.InheritedStyleIncludes(viewType);
        var stopwatch = Stopwatch.StartNew();

        Control? view = null;
        Window? window = null;
        Control? measured = null;
        Control? layoutRoot = null;
        string? image = null;
        IReadOnlyList<Control> hiddenSurfaces = [];
        var checkedControls = 0;
        AccessibilityAuditResult? accessibility = null;

        try
        {
            view = (Control)Activator.CreateInstance(viewType)!;
            if (dataContext != null)
            {
                // A dialog is a scrim bound to its view model's IsOpen, and 39
                // of the surfaces here are exactly that — closed, so their whole
                // card measures nothing and the sweep would grade an empty
                // overlay. Opening them through the product's own state is what
                // "click everything" means; 41 view models carry the property.
                SetIsOpen(dataContext, true);
                view.DataContext = dataContext;
            }
            else if (declared != null)
            {
                // Declared a type but nothing could build one. Pin the context to
                // null so the view does NOT inherit the host window's shell view
                // model — a compiled binding would then fail its cast and the
                // sweep would report the harness instead of the product.
                view.SetValue(StyledElement.DataContextProperty, null);
            }

            if (view is Window asWindow)
            {
                window = asWindow;
                window.Show();
                // Never Measure() a headless Window directly — it reports
                // "Invalid size returned for Measure". Its CONTENT is the tree.
                measured = window.Presenter?.Child ?? window.Content as Control;
                layoutRoot = measured;
                measured?.Measure(new Size(HostWidth, HostHeight));
                measured?.Arrange(new Rect(0, 0, HostWidth, HostHeight));
            }
            else
            {
                // A dialog view is only itself inside the production overlay
                // host: DialogContainerView is the single place
                // Views/Dialogs/DialogStyles.axaml is included, so a bare
                // dialog would fail every style lookup the grammar makes.
                Control host;
                if (viewType.Namespace == "DotCharter.UI.Views.Dialogs")
                {
                    var container = new DialogContainerView();
                    container.DialogPanel.Children.Clear();
                    container.DialogPanel.Children.Add(view);
                    host = container;
                }
                else
                {
                    host = view;
                }

                // The host window carries the SHELL view model, because in the
                // product these surfaces live inside MainWindow: a
                // `$parent[Window].DataContext…` binding — the dialog service is
                // reached that way from several views — resolves against it, and
                // a bare host would report every one of them as broken.
                // The product's MainWindow paints AppBackground on ITSELF and every
                // pane, popover, settings tab, HUD strip and editor face is
                // transparent over it. A bare Window has no fill, so a capture
                // of such a view came out on Avalonia's default white (2026-09-13:
                // 50 of the 100 sweep images) and read as a rendering fault. The
                // layout checks never cared; the pictures do.
                window = new Window
                {
                    Width = HostWidth, Height = HostHeight, Content = host, DataContext = shellViewModel,
                    Background = ShellBackground()
                };
                ApplyInheritedStyles(window, inheritedStyles);
                window.Show();
                layoutRoot = host;
                host.Measure(new Size(HostWidth, HostHeight));
                host.Arrange(new Rect(0, 0, HostWidth, HostHeight));
                measured = view;
            }

            // A list surface only HAS its markup once it holds an item — an
            // empty ItemsControl instantiates no ItemTemplate, so every check
            // below would pass by finding nothing to look at. The layout above
            // is what makes the ItemsControls reachable and their sources bound;
            // seeding then needs a second pass to realize the rows.
            //
            // This runs BEFORE the hidden sub-surfaces are realized, and the
            // order is what makes the two mechanisms compound: an ItemsControl
            // inside a gated screen is in the visual tree the whole time (a
            // hidden element is skipped by MeasureCore, not removed), so it is
            // found and seeded here, and its rows are then built by the
            // realization below. Seed after, and a gated list is measured empty.
            if (measured != null && layoutRoot != null)
            {
                seededLists = UiSweepListSeeder.Seed(measured, App.Services!, name, findings);
                if (seededLists.Count > 0)
                {
                    // UpdateLayout, NOT another Measure/Arrange. Adding an item
                    // invalidates the ItemsControl and queues it with the layout
                    // manager; it does NOT mark its ancestors invalid. So
                    // `layoutRoot.Measure(sameSize)` returns immediately — the
                    // root is still measure-valid — and the invalidated control
                    // is never reached. The rows silently stayed unrealized and
                    // the surface was reported clean all over again. Only a
                    // layout PASS drains that queue.
                    layoutRoot.UpdateLayout();
                    layoutRoot.Measure(new Size(HostWidth, HostHeight));
                    layoutRoot.Arrange(new Rect(0, 0, HostWidth, HostHeight));
                }
            }

            stopwatch.Stop();

            if (measured is null)
            {
                findings.Add(new UiSweepFinding(name, UiSweepSeverity.Error, "no-content",
                    "the window presented no content to lay out"));
            }
            else
            {
                checkedControls = Inspect(name, measured, findings, source);
                hiddenSurfaces = RealizeHiddenSurfaces(name, measured, findings);
                checkedControls += InspectHiddenSurfaces(name, hiddenSurfaces, findings, source);

                // What a screen reader sees of the same realized tree (#311); read-only.
                accessibility = AccessibilityAudit.Audit(name, measured, hiddenSurfaces);

                // Counted here, not in the seeding block: realizing a gated
                // screen is what builds the rows of any list inside it, so a
                // count taken before that misses every one of them.
                realizedRows = measured.GetVisualDescendants().OfType<ItemsControl>()
                    .Sum(items => items.ItemsPanelRoot?.Children.Count ?? 0);

                if (CaptureEnabled && window != null) image = Capture(window, name, findings);
            }
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            findings.Add(new UiSweepFinding(name, UiSweepSeverity.Error, "construction-failed",
                $"{ex.GetType().Name}: {FirstLine(ex.Message)}"));
        }
        finally
        {
            try { window?.Close(); }
            catch (InvalidOperationException) { /* a window that never opened */ }

            // Same reason as the IsOpen restore below: these collections belong
            // to DI singletons, so a seeded row left behind would show up in
            // every later surface that binds the same list.
            UiSweepListSeeder.Unseed(seededLists);

            // Dialog view models are DI SINGLETONS, so one left open stays open
            // for the rest of the sweep — it then shows through the scrim of
            // every later dialog's capture and mixes its binding diagnostics
            // into theirs. Put it back the way it was found.
            if (dataContext != null) SetIsOpen(dataContext, false);
        }

        CollectBindingFindings(name, sink, findings);

        return new UiSweepResult(
            name, group, source, declared?.Name ?? "(none declared)",
            measured?.Bounds ?? default,
            measured is null ? 0 : measured.GetVisualDescendants().Count(),
            checkedControls,
            hiddenSurfaces.Count,
            inheritedStyles,
            seededLists.Count,
            realizedRows,
            stopwatch.Elapsed.TotalMilliseconds,
            image,
            findings,
            accessibility);
    }

    /// <summary>The shell's window fill, resolved from the app's theme resources
    /// (<c>AppBackground</c> in <c>Styles/Resources/ThemeSurfaces.axaml</c>) so a
    /// capture reads like the app; the rig dock's flattened ground as a fallback
    /// when the resource cannot be resolved, which is still not white.</summary>
    internal static IBrush ShellBackground()
    {
        var app = Application.Current;
        if (app != null && app.TryGetResource("AppBackground", app.ActualThemeVariant, out var value)
            && value is IBrush brush)
        {
            return brush;
        }

        return new SolidColorBrush(Color.Parse("#0E0B15"));
    }

    /// <summary>
    ///     Gives the host window the grammars this view inherits from the view
    ///     that hosts it in the product (see
    ///     <see cref="UiSweepCatalog.InheritedStyleIncludes" />). Window scope is
    ///     the right level: it is exactly where the parent view's
    ///     <c>UserControl.Styles</c> would sit relative to the surface under test,
    ///     and the window is thrown away with the surface.
    /// </summary>
    private static void ApplyInheritedStyles(Window window, IReadOnlyList<string> sources)
    {
        foreach (var source in sources)
        {
            window.Styles.Add(new StyleInclude(new Uri("avares://DotCharter/"))
            {
                Source = new Uri(source)
            });
        }
    }

    /// <summary>Flips a view model's own <c>IsOpen</c> so its surface is in the
    /// state the user sees it in. No-op for anything that has no such property,
    /// and it never invents one.</summary>
    private static void SetIsOpen(object viewModel, bool open)
    {
        var property = viewModel.GetType().GetProperty("IsOpen",
            BindingFlags.Public | BindingFlags.Instance);
        if (property?.PropertyType != typeof(bool) || !property.CanWrite) return;

        try { property.SetValue(viewModel, open); }
        catch (TargetInvocationException) { /* an opener that needs state we do not have */ }
    }

    // ── hidden surfaces ───────────────────────────────────────────────────

    /// <summary>
    ///     Realizes the <c>IsVisible=false</c> sub-surfaces of a swept view, so
    ///     the checks below reach them.
    ///     <para>
    ///         <b>Without this the sweep grades whichever screen happens to be
    ///         current and nothing else.</b> <c>PlayModeView</c> is 8,900 lines
    ///         over sixteen named <c>*ScreenRoot</c>s behind 280 <c>IsVisible</c>
    ///         gates; only the launch screen is up when the view is built, so
    ///         Menu, Song Select, Co-op Setup, Get Ready, Countdown, Playing,
    ///         Pause, Failed, Results, History, Trends, Network
    ///         and the Amp &amp; FX rig were never measured at all. Avalonia skips
    ///         <c>MeasureCore</c> for an invisible element, so an unrealized
    ///         subtree has no styles, no template and no text layout — every check
    ///         here silently passes over it.
    ///     </para>
    ///     <para>
    ///         <b>It must NOT write <c>IsVisible</c> to do that</b>, for the same
    ///         reasons the product's own prewarm must not: five dialogs grab
    ///         keyboard focus on the transition, and the property is owned by a binding. The mechanism is the
    ///         product's own <see cref="HiddenSubtreePrewarmer.Realize" /> —
    ///         apply the root's template, measure its VISIBLE children — which
    ///         means the sweep is also a live test of the path that keeps the
    ///         first switch inside its budget.
    ///     </para>
    ///     <para>
    ///         Surfaces come from <see cref="IPrewarmableSurfaces" /> where the
    ///         view declares them (parent before child, which is the order that
    ///         interface promises), and otherwise from the hidden controls named
    ///         <c>*Root</c> — the repo's convention for a gated region.
    ///     </para>
    /// </summary>
    private static IReadOnlyList<Control> RealizeHiddenSurfaces(string name, Control view,
        List<UiSweepFinding> findings)
    {
        // The same A/B switch the shell's own prewarm honours, for the same
        // reason: the only way to state what this buys is to run without it.
        if (Environment.GetEnvironmentVariable("DOTCHARTER_NO_SURFACE_PREWARM") == "1") return [];

        var fallback = new Size(HostWidth, HostHeight);
        var candidates = view is IPrewarmableSurfaces owner
            ? owner.HiddenSurfaces()
            : NamedHiddenRoots(view);

        var realized = new List<Control>();
        foreach (var part in candidates)
        {
            if (RealizeAndReport(part, fallback, name, findings))
                realized.Add(part);
        }

        // …and a SETTLING pass, which is not belt-and-braces: without it the
        // sweep invents `clipped-text` errors that no user can see.
        //
        // Several views declare no styles of their own because their HOST
        // carries them (`SoundInspectorView` and its six siblings rely on
        // `InspectorView` including `InspectorStyles.axaml`), so this harness
        // adds them at WINDOW scope in ApplyInheritedStyles. In the product they
        // are in the host's `<UserControl.Styles>` and are applied before any
        // layout runs; here they attach during the FIRST measure of a subtree
        // that has never been measured. So that first pass sizes a control from
        // the pre-style font, the style's font then widens the text inside a
        // slot already arranged, and Realize — one Measure + one Arrange, by
        // design, because that IS the product's cost model — never revisits it.
        // Measured 2026-08-29: 15 phantom clipped-text errors in
        // SoundChordTechniqueCard, every one of them exactly 3 px, and not one
        // of them reproducible with the same card VISIBLE under the same styles.
        // A real layout pass would settle it; a hidden root gets no layout pass,
        // so it is settled here.
        foreach (var part in realized)
            HiddenSubtreePrewarmer.Realize(part, fallback);

        return realized;
    }

    /// <summary>The hidden <c>*Root</c> regions of a view that declares no
    /// <see cref="IPrewarmableSurfaces" />. Depth-first, so a gated parent is
    /// returned before its gated children — the ordering
    /// <see cref="HiddenSubtreePrewarmer" /> needs to size a child against a slot
    /// that has been arranged.</summary>
    private static IReadOnlyList<Control> NamedHiddenRoots(Control view) =>
    [
        .. view.GetVisualDescendants().OfType<Control>()
            .Where(c => !c.IsVisible && c.Name?.EndsWith("Root", StringComparison.Ordinal) == true)
    ];

    /// <summary>
    ///     Realizes one hidden root through the product's own prewarm, and turns a
    ///     refusal into a finding.
    ///     <para>
    ///         <see cref="HiddenSubtreePrewarmer.Realize" /> deliberately NEVER
    ///         throws — a surface that cannot measure in isolation must not take
    ///         the window down, it just stays cold. That is right for the product
    ///         and useless for a sweep, so a refusal is repeated here, where the
    ///         exception can be reported as the <c>construction-failed</c> it is.
    ///     </para>
    /// </summary>
    private static bool RealizeAndReport(Control part, Size fallback, string name,
        List<UiSweepFinding> findings)
    {
        // Already visible: it was laid out with the rest of the view and the
        // ordinary pass has covered it.
        if (part.IsVisible) return false;
        if (HiddenSubtreePrewarmer.Realize(part, fallback)) return true;

        try
        {
            var available = HiddenSubtreePrewarmer.SlotSizeOf(part, fallback);
            part.ApplyTemplate();
            foreach (var child in part.GetVisualChildren().OfType<Layoutable>())
            {
                child.Measure(available);
                child.Arrange(new Rect(available));
            }
        }
        catch (Exception ex)
        {
            findings.Add(new UiSweepFinding(name, UiSweepSeverity.Error, "construction-failed",
                $"hidden surface '{Label(part)}' threw while being realized — " +
                $"{ex.GetType().Name}: {FirstLine(ex.Message)}"));
        }

        return false;
    }

    /// <summary>
    ///     Runs the descendant checks over the realized hidden surfaces.
    ///     <para>
    ///         Visibility is judged RELATIVE to each realized root: the root's own
    ///         <c>IsVisible</c> is false by definition, so
    ///         <c>IsEffectivelyVisible</c> is false for everything under it and
    ///         every check would skip the whole subtree. A control counts when
    ///         nothing BETWEEN it and the root is hidden — i.e. when it is what
    ///         the user sees the moment that surface is shown.
    ///     </para>
    ///     <para>
    ///         Roots are visited in prewarm order and a control is attributed to
    ///         the OUTERMOST root that can see it, so a nested gated pane (the
    ///         Progress hub's four views, the settings modal's seven panes) is not
    ///         reported twice. Everything is collected before anything is emitted
    ///         so the per-kind cap applies once per surface, not once per root.
    ///     </para>
    /// </summary>
    /// <returns>How many controls the checks actually reached.</returns>
    private static int InspectHiddenSurfaces(string name, IReadOnlyList<Control> roots,
        List<UiSweepFinding> findings, UiSweepDataContextSource dataContext)
    {
        if (roots.Count == 0) return 0;

        var attributed = new HashSet<Control>();
        var hits = new SubtreeHits();

        foreach (var root in roots)
        {
            var visible = new List<Control>();
            foreach (var control in root.GetVisualDescendants().OfType<Control>())
            {
                if (attributed.Contains(control) || !VisibleWithin(control, root)) continue;
                attributed.Add(control);
                visible.Add(control);
            }

            if (visible.Count > 0)
                CollectSubtree(visible, dataContext, $"[{RootLabel(root)}] ", hits, root);
        }

        hits.EmitInto(name, findings);
        return attributed.Count;
    }

    /// <summary>Whether <paramref name="control" /> would be on screen the moment
    /// <paramref name="root" /> is shown: nothing between the two is hidden. The
    /// root's own visibility is deliberately not consulted.</summary>
    private static bool VisibleWithin(Control control, Control root)
    {
        for (Visual? v = control; v != null && !ReferenceEquals(v, root); v = v.GetVisualParent())
        {
            if (!v.IsVisible)
                return false;
        }

        return true;
    }

    // ── the four checks ───────────────────────────────────────────────────

    /// <summary>
    ///     The four descendant checks' hits, held until the whole pass over a
    ///     surface has been walked so the per-kind cap applies once.
    /// </summary>
    private sealed class SubtreeHits
    {
        public List<string> Inert { get; } = [];
        public List<string> Clipped { get; } = [];

        /// <summary>Clipped text that is the list seeder's own sample string.
        /// Always a warning: its LENGTH is the harness's choice, so gating on it
        /// would gate on <see cref="UiSweepListSeeder.SampleText" />. Still
        /// reported — a slot that cannot hold 33 characters is worth knowing —
        /// but under its own kind so it can never be read as a product
        /// finding.</summary>
        public List<string> ClippedSeeded { get; } = [];

        public List<string> Overflow { get; } = [];
        public List<string> Overlap { get; } = [];

        public void EmitInto(string name, List<UiSweepFinding> findings)
        {
            Emit(name, UiSweepSeverity.Warning, "inert-action-control", Inert, findings);
            // Error on EVERY surface, hidden or visible, since 0.57 — see the
            // note where the demotion constant used to live.
            Emit(name, UiSweepSeverity.Error, "clipped-text", Clipped, findings);
            Emit(name, UiSweepSeverity.Warning, "clipped-seeded-text", ClippedSeeded, findings);
            Emit(name, UiSweepSeverity.Warning, "overflows-parent", Overflow, findings);
            Emit(name, UiSweepSeverity.Warning, "cell-content-overlap", Overlap, findings);
        }
    }

    // `clipped-text` on a realized HIDDEN surface used to be demoted to a
    // Warning by a `HiddenSurfaceClippedTextSeverity` constant while its backlog
    // stood at seven. That backlog is EMPTY as of 0.57 and the constant is gone,
    // so hidden surfaces are held to the same Error bar as visible ones — which
    // is the whole point of realizing them. The seven were: Get Ready's fixed
    // 404 px rail slicing "Standard" (60.8 px in 58), "100" (22.8 in 17) and
    // "2.3 S ON SCREEN" (106.5 in 92), and four stem-model names at 283.9-331.1
    // px in the settings shell's 275 px control column — none of which the tab
    // shows when swept ALONE at 1600 px wide. Fixed by a `.Dense` segment
    // variant, one wrap, and one wrap respectively.
    //
    // If this needs demoting again, put the backlog back with it: a class that
    // fails the run permanently is a class nobody reads.

    /// <returns>How many controls the checks actually reached.</returns>
    private static int Inspect(string name, Control view, List<UiSweepFinding> findings,
        UiSweepDataContextSource dataContext)
    {
        // 1. Measured non-zero. A view whose styles failed still ARRANGES — it
        //    just has no size — and that is the shape every silent XAML fault in
        //    this repo has taken.
        if (view.Bounds.Width <= 1 || view.Bounds.Height <= 1)
        {
            findings.Add(new UiSweepFinding(name, UiSweepSeverity.Error, "measured-zero",
                $"laid out as {view.Bounds} — it rendered nothing"));
            return 0;
        }

        // …and ARRANGED bounds alone are a weak signal, which is worth being
        // explicit about: a stretching UserControl fills whatever rect it is
        // arranged into whether or not anything inside it survived. DESIRED size
        // is what collapses when the content does — an `IsVisible=False` on a
        // root region measures 0 while its Bounds stay 1600x1000.
        //
        // It is a WARNING, not an error, because several surfaces here are
        // legitimately collapsed at rest: the dialog host with every dialog
        // closed, the command palette, the overlay strips. Telling those apart
        // from a broken view needs a hand-written list of "allowed to be empty",
        // and a list like that goes stale silently. The contact sheet shows them
        // as blank cards, which is the review the owner asked for.
        //
        // …but the finding now CARRIES its reason, derived from the tree
        // (CollapseReason). Five of these stood unexplained long enough to be
        // re-investigated from scratch more than once, and every time the answer
        // was one of the same two facts the tree already knows.
        if (view.DesiredSize.Width <= 1 || view.DesiredSize.Height <= 1)
        {
            findings.Add(new UiSweepFinding(name, UiSweepSeverity.Warning, "collapsed-at-rest",
                $"arranged {view.Bounds.Width:0}x{view.Bounds.Height:0} but DESIRED " +
                $"{view.DesiredSize.Width:0.#}x{view.DesiredSize.Height:0.#} — nothing inside it " +
                $"took space{CollapseReason(view)}"));
        }

        var hits = new SubtreeHits();
        var visible = view.GetVisualDescendants().OfType<Control>()
            .Where(c => c.IsEffectivelyVisible).ToList();
        CollectSubtree(visible, dataContext, prefix: "", hits);
        hits.EmitInto(name, findings);
        return visible.Count;
    }

    /// <summary>
    ///     Checks 2-4 over one already-visibility-filtered set of controls.
    ///     <para>
    ///         The caller decides what "visible" means, because there are two
    ///         answers: <c>IsEffectivelyVisible</c> for the tree that is up, and
    ///         "nothing between it and its realized root is hidden" for a surface
    ///         that has been prewarmed but not shown (see
    ///         <see cref="VisibleWithin" />).
    ///     </para>
    ///     <para>
    ///         <c>prefix</c> names the realized hidden root a hit came from, so a
    ///         finding on a surface with sixteen screens says which screen, and
    ///         <c>realizedRoot</c> is that root itself (<c>null</c> on the ordinary
    ///         visible pass). Only the overflow check consults it — see
    ///         <see cref="IsPrewarmArrangeRect" />.
    ///     </para>
    /// </summary>
    private static void CollectSubtree(IReadOnlyList<Control> descendants,
        UiSweepDataContextSource dataContext, string prefix, SubtreeHits hits,
        Control? realizedRoot = null)
    {
        // 2. Inert action controls: visible, hit-testable, and wired to nothing.
        //    The silent failure — the control draws and does nothing.
        //    Only meaningful on a surface that GOT a data context: with none,
        //    every `Command="{Binding …}"` is trivially null and the check would
        //    report the harness rather than the product.
        foreach (var control in dataContext is UiSweepDataContextSource.ShellGraph
                     or UiSweepDataContextSource.Container or UiSweepDataContextSource.Activated
                     ? descendants
                     : [])
        {
            if (control.Bounds.Width <= 1 || control.Bounds.Height <= 1) continue;
            // A DISABLED control is inert on purpose — that is what disabled
            // means. Only an enabled one that leads nowhere is the fault.
            if (!control.IsEffectivelyEnabled) continue;

            var command = control switch
            {
                Button b => b.Command,
                MenuItem m => m.Command,
                _ => null
            };
            if (command != null) continue;
            if (control is not (Button or MenuItem)) continue;

            // A ToggleButton/CheckBox/RadioButton acts through IsChecked, a
            // MenuItem with children is a submenu header, and a SplitButton's
            // own surface may only open its flyout — none of those are inert.
            if (control is ToggleButton) continue;
            if (control is MenuItem { ItemCount: > 0 }) continue;
            if (control is Button { Flyout: not null } or MenuItem { HasSubMenu: true }) continue;
            if (control.ContextFlyout != null) continue;
            if (HasClickHandler(control)) continue;

            hits.Inert.Add($"{prefix}{control.GetType().Name} '{Label(control)}' is enabled and " +
                           "visible with no Command and no Click handler");
        }

        // 3. Text clipped by its slot: laid out narrower than the text it holds,
        //    with neither wrapping nor an ellipsis to absorb it.
        //
        //    ERROR, not a warning, since 0.53. It was reported-only while the
        //    sweep still had a backlog: nine DEFAULT keybind chords sliced
        //    mid-word in Settings, and "Duplicate" sliced in ENGLISH in two
        //    three-across dialog rows. Those are fixed and the class came back
        //    empty across all 80 surfaces, so the only thing a warning buys now
        //    is that the next one goes unread. Note what the check does NOT
        //    forgive and should not: an ellipsis or wrapping makes text clipped
        //    DELIBERATELY, and both are excluded above — a finding here is text
        //    with no plan for not fitting.
        foreach (var text in descendants.OfType<TextBlock>())
        {
            if (string.IsNullOrEmpty(text.Text)) continue;
            if (text.TextWrapping != TextWrapping.NoWrap) continue;
            if (text.TextTrimming != TextTrimming.None) continue;
            if (text.Bounds.Width <= 1) continue;

            var slot = text.Bounds.Width - text.Padding.Left - text.Padding.Right;
            var needed = text.TextLayout.WidthIncludingTrailingWhitespace;
            if (needed <= slot + 1.0) continue;

            // Seeded sample text is bucketed apart and never fails the run —
            // see SubtreeHits.ClippedSeeded.
            var bucket = UiSweepListSeeder.IsSeededValue(text.Text) ? hits.ClippedSeeded : hits.Clipped;
            bucket.Add($"{prefix}\"{Ellipsize(text.Text)}\" needs {needed:0.#} px in a " +
                       $"{slot:0.#} px slot (NoWrap, no trimming)");
        }

        // 4. Overflow: a child arranged outside the box its parent gave it.
        foreach (var control in descendants)
        {
            if (control.Bounds is { Width: <= 1, Height: <= 1 }) continue;
            if (control.GetVisualParent() is not Control parent) continue;
            if (!OverflowIsMeaningful(control, parent)) continue;

            // The PREWARM's own arrange rect, not a layout the product ever
            // performs — see IsPrewarmArrangeRect.
            if (realizedRoot != null && ReferenceEquals(parent, realizedRoot) &&
                IsPrewarmArrangeRect(control, realizedRoot))
            {
                continue;
            }

            var b = control.Bounds;
            var overshootX = Math.Max(b.Right - parent.Bounds.Width, -b.X);
            var overshootY = Math.Max(b.Bottom - parent.Bounds.Height, -b.Y);
            if (overshootX <= 1.0 && overshootY <= 1.0) continue;

            hits.Overflow.Add($"{prefix}{Describe(control)} escapes its {Describe(parent)} " +
                              $"({parent.Bounds.Width:0.#}x{parent.Bounds.Height:0.#}) by " +
                              $"{Math.Max(overshootX, 0):0.#}x{Math.Max(overshootY, 0):0.#} px " +
                              $"— child at {b.X:0.#},{b.Y:0.#} {b.Width:0.#}x{b.Height:0.#}" +
                              $", under {NearestNamed(parent)}");
        }

        // 5. One Grid cell's CONTENT painted into another cell's.
        //
        //    This is its own detection class because nothing above can see it.
        //    Check 4 compares a control against its OWN parent, and stands down
        //    entirely for anything under a ScrollViewer — which is every list
        //    row in the app. And arranged bounds do not show it at all: a
        //    horizontal StackPanel measures its children with INFINITE width, so
        //    its DesiredSize is clamped to the slot it was given while its
        //    children are arranged at their own widths from x=0 and nothing
        //    clips. The panel therefore reports a perfectly correct rectangle
        //    while painting outside it, into the neighbouring cell.
        //
        //    That is exactly how the recovery dialog painted its autosave stamp
        //    over the session name for a release: the kind tag was arranged 68 px
        //    past a 216 px cell and the stamp, declared later, drew on top.
        //
        //    Children of the SAME cell are skipped — stacking two mutually
        //    exclusive tags, or a badge over a tile, is how a Grid is used on
        //    purpose. Only a cell reaching into a cell it does not occupy is a
        //    mistake.
        //
        //    Visibility is the CALLER's definition, like every check above: the
        //    pre-filtered set is the authority, so this runs unchanged over a
        //    realized hidden surface where `IsEffectivelyVisible` is false for
        //    everything.
        var shown = new HashSet<Control>(descendants);
        foreach (var grid in descendants.OfType<Grid>())
        {
            if (grid.ColumnDefinitions.Count < 2 && grid.RowDefinitions.Count < 2) continue;
            if (grid.Bounds.Width <= 1 || grid.Bounds.Height <= 1) continue;

            var cells = grid.GetVisualChildren().OfType<Control>()
                .Where(c => shown.Contains(c) && c.RenderTransform is null)
                .Where(c => c.Bounds is { Width: > 1, Height: > 1 })
                .Where(c => c.Margin is { Left: >= 0, Top: >= 0, Right: >= 0, Bottom: >= 0 })
                // A Popup is not painted where it sits: it reports the
                // PLACEMENT rectangle of a separate top-level window. Every
                // ComboBox template puts one in cell 0,0 next to its dropdown
                // glyph, which was 31 of the 31 findings on the first run — the
                // same reason `OverflowIsMeaningful` exempts it. A GridSplitter
                // deliberately straddles the columns it resizes.
                .Where(c => c is not (Popup or GridSplitter))
                .Select(c => (
                    Col: Grid.GetColumn(c), ColSpan: Math.Max(1, Grid.GetColumnSpan(c)),
                    Row: Grid.GetRow(c), RowSpan: Math.Max(1, Grid.GetRowSpan(c)),
                    Control: c, Painted: PaintedBounds(c, grid, shown)))
                .ToList();

            for (var i = 0; i < cells.Count; i++)
            {
                for (var j = i + 1; j < cells.Count; j++)
                {
                    var (aCol, aColSpan, aRow, aRowSpan, aControl, aPainted) = cells[i];
                    var (bCol, bColSpan, bRow, bRowSpan, bControl, bPainted) = cells[j];

                    if (SpansMeet(aCol, aColSpan, bCol, bColSpan) &&
                        SpansMeet(aRow, aRowSpan, bRow, bRowSpan))
                    {
                        continue;
                    }

                    var hit = aPainted.Intersect(bPainted);
                    if (hit.Width <= 1 || hit.Height <= 1) continue;

                    hits.Overlap.Add(
                        $"{prefix}{Describe(aControl)} (cell {aCol},{aRow}) paints {Fmt(aPainted)} and " +
                        $"{Describe(bControl)} (cell {bCol},{bRow}) paints {Fmt(bPainted)} — " +
                        $"they overlap by {hit.Width:0.#}x{hit.Height:0.#} px");
                }
            }
        }
    }

    private static bool SpansMeet(int aStart, int aSpan, int bStart, int bSpan) =>
        aStart < bStart + bSpan && bStart < aStart + aSpan;

    /// <summary>
    ///     Everything a control's subtree actually PAINTS, in
    ///     <paramref name="space" />'s coordinates.
    ///     <para>
    ///         The walk STOPS at a clipping boundary — a <c>ClipToBounds</c>
    ///         element or a scroll presenter — because content past one of those
    ///         is not drawn, and reporting it would turn the sanctioned fix for
    ///         this fault class into a finding of its own.
    ///     </para>
    ///     <para>
    ///         <paramref name="shown" /> is the caller's set of visible controls,
    ///         not <c>IsEffectivelyVisible</c> — on a realized HIDDEN surface
    ///         that property is false for every control in the subtree, so
    ///         consulting it would collapse the walk to nothing exactly where the
    ///         sweep just did the work to see something. A visual that is not a
    ///         <see cref="Control" /> cannot be gated on its own and is walked.
    ///     </para>
    /// </summary>
    private static Rect PaintedBounds(Visual root, Visual space, HashSet<Control> shown)
    {
        var rect = BoundsIn(root, space);
        if (!root.ClipToBounds && root is not ScrollContentPresenter) Accumulate(root, space, shown, ref rect);
        return rect;
    }

    private static void Accumulate(Visual node, Visual space, HashSet<Control> shown, ref Rect rect)
    {
        foreach (var child in node.GetVisualChildren())
        {
            if (child is Control control && !shown.Contains(control)) continue;
            if (child.Bounds is { Width: <= 0 } or { Height: <= 0 }) continue;

            if (child is Popup) continue;

            rect = rect.Union(BoundsIn(child, space));
            if (child.ClipToBounds || child is ScrollContentPresenter) continue;
            Accumulate(child, space, shown, ref rect);
        }
    }

    private static Rect BoundsIn(Visual visual, Visual space) =>
        new(visual.TranslatePoint(new Point(0, 0), space) ?? default, visual.Bounds.Size);

    /// <summary>A control in a finding. Its <c>x:Name</c> when it has one, and
    /// otherwise its STYLE CLASSES — "Grid escapes its Grid" names two of the
    /// hundred Grids on the surface and is not something anyone can go and find,
    /// while "Grid.FirePopoverRow" is a grep away from the markup. Pseudoclasses
    /// (<c>:pointerover</c>, <c>:checked</c>) are dropped: they are state, not
    /// identity, and they differ between two runs of the same tree.</summary>
    private static string Describe(Control control)
    {
        var type = control.GetType().Name;
        if (control.Name is { Length: > 0 } n) return $"{type} '{n}'";

        var classes = string.Join(".", control.Classes.Where(c => !c.StartsWith(':')));
        return classes.Length == 0 ? type : $"{type}.{Ellipsize(classes, 48)}";
    }

    /// <summary>The nearest ancestor of <paramref name="control" /> that the
    /// MARKUP names — an <c>x:Name</c> is the only handle a reader has for
    /// finding a control inside a 684 KB view, and a template part's own
    /// <c>PART_*</c> name says which control it belongs to. Walks up to eight
    /// levels; a deeper anonymous run means the surrounding markup names
    /// nothing.</summary>
    private static string NearestNamed(Control control)
    {
        var depth = 0;
        for (var v = control.GetVisualParent(); v != null && depth++ < 8; v = v.GetVisualParent())
        {
            if (v is Control { Name: { Length: > 0 } n } c)
                return $"{c.GetType().Name} '{n}'";
        }

        return "nothing named within 8 levels";
    }

    private static string Fmt(Rect r) =>
        $"[{r.X:0}, {r.Y:0} {r.Width:0}x{r.Height:0}]";

    /// <summary>Public face of <see cref="Emit" /> for the list seeder, which
    /// produces findings of its own.</summary>
    internal static void EmitFindings(string surface, UiSweepSeverity severity, string kind,
        List<string> details, List<UiSweepFinding> findings) =>
        Emit(surface, severity, kind, details, findings);

    /// <summary>
    ///     Records a check's hits, collapsing IDENTICAL ones into a count and
    ///     capping the rest. Nineteen download buttons in one list are one
    ///     finding, not nineteen; a hundred distinct ones are six plus a tally.
    /// </summary>
    private static void Emit(string surface, UiSweepSeverity severity, string kind,
        List<string> details, List<UiSweepFinding> findings)
    {
        var grouped = details
            .GroupBy(d => d, StringComparer.Ordinal)
            .Select(g => g.Count() == 1 ? g.Key : $"{g.Key} (x{g.Count()})")
            .ToList();

        foreach (var detail in grouped.Take(MaxFindingsPerKindPerSurface))
            findings.Add(new UiSweepFinding(surface, severity, kind, detail));

        if (grouped.Count > MaxFindingsPerKindPerSurface)
        {
            findings.Add(new UiSweepFinding(surface, severity, kind,
                $"...and {grouped.Count - MaxFindingsPerKindPerSurface} more distinct cases"));
        }
    }

    /// <summary>
    ///     Turns Avalonia's own binding diagnostics into findings.
    ///     <para>
    ///         A failed binding onto <c>Command</c> is an ERROR — that is
    ///         precisely "the markup named a command and the control got none",
    ///         i.e. a button that draws and does nothing. Every other failed
    ///         binding is a warning: real, worth reporting, but not by itself
    ///         proof the surface is broken.
    ///     </para>
    ///     <para>
    ///         <b>"Value is null" is dropped entirely, and that is what makes
    ///         the rest readable.</b> Avalonia logs one of those for every step
    ///         of a path whose intermediate is null — nothing selected, no song
    ///         loaded, no opponent on the podium — which is an ordinary app
    ///         state, not a fault. Left in, the first run drowned four real
    ///         diagnostics in ninety of them. A path that does not EXIST, a
    ///         converter that threw and an indexer out of range all log
    ///         differently and all survive this filter.
    ///     </para>
    /// </summary>
    private static void CollectBindingFindings(string name, UiSweepLogSink sink, List<UiSweepFinding> findings)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var extra = 0;
        var nullSourced = 0;
        foreach (var (area, message) in sink.Drain())
        {
            if (!seen.Add(message)) continue;
            if (message.EndsWith("Value is null.", StringComparison.Ordinal))
            {
                nullSourced++;
                continue;
            }

            var isCommand = message.Contains("binding Command to ", StringComparison.Ordinal);
            var severity = area == LogArea.Binding && isCommand
                ? UiSweepSeverity.Error
                : UiSweepSeverity.Warning;
            var kind = area == LogArea.Binding
                ? isCommand ? "dead-command-binding" : "binding-error"
                : $"{area.ToLowerInvariant()}-warning";

            if (severity == UiSweepSeverity.Error ||
                findings.Count(f => f.Kind == kind) < MaxFindingsPerKindPerSurface)
            {
                findings.Add(new UiSweepFinding(name, severity, kind, FirstLine(message)));
            }
            else
            {
                extra++;
            }
        }

        if (extra > 0)
        {
            findings.Add(new UiSweepFinding(name, UiSweepSeverity.Warning, "binding-error",
                $"...and {extra} more distinct binding diagnostics"));
        }

        // Counted, never listed: the surface simply had no selection/song/peer.
        if (nullSourced > 0)
        {
            findings.Add(new UiSweepFinding(name, UiSweepSeverity.Warning, "null-source-binding",
                $"{nullSourced} binding paths hit a null intermediate (nothing selected/loaded — not a fault)"));
        }
    }

    /// <summary>
    ///     Whether a child escaping its parent's box means anything. Scrollers,
    ///     canvases and virtualizing item hosts arrange outside on purpose;
    ///     negative margins and render transforms are deliberate offsets; and a
    ///     parent that clips has already handled it.
    /// </summary>
    private static bool OverflowIsMeaningful(Control child, Control parent)
    {
        if (parent is Canvas or ScrollContentPresenter or ItemsPresenter) return false;
        if (parent.ClipToBounds) return false;
        if (child.RenderTransform != null) return false;
        if (child.Margin.Left < 0 || child.Margin.Top < 0 ||
            child.Margin.Right < 0 || child.Margin.Bottom < 0)
        {
            return false;
        }

        if (parent.Bounds.Width <= 1 || parent.Bounds.Height <= 1) return false;

        // Anything under a scroller is expected to exceed the viewport.
        for (Visual? v = child; v != null; v = v.GetVisualParent())
        {
            if (v is ScrollViewer or Canvas or Popup)
                return false;
        }

        return true;
    }

    /// <summary>
    ///     Why a surface has no desired size, DERIVED from the tree rather than
    ///     from a hand list of "known empty" views. A list would go stale
    ///     silently; these two facts cannot.
    ///     <para>
    ///         Both shapes are benign and both are in the sweep today. An
    ///         <c>ItemsControl</c> with no items is a host whose ROWS are declared
    ///         by the surface that uses it, so swept on its own it has nothing to
    ///         lay out (<c>EditorMenu</c> — a <c>Menu</c> subclass whose eight
    ///         headers live in <c>MainWindow.axaml</c>). An all-hidden subtree is a
    ///         host with nothing open in it (<c>DialogContainerView</c> with every
    ///         dialog closed).
    ///         Anything that matches NEITHER is the case worth reading.
    ///     </para>
    /// </summary>
    private static string CollapseReason(Control view)
    {
        if (view is ItemsControl { ItemCount: 0 })
        {
            return " — it is an ItemsControl with NO items: swept on its own, the surface that " +
                   "declares its rows is elsewhere";
        }

        var descendants = view.GetVisualDescendants().OfType<Control>().ToList();
        var hidden = descendants.Count(c => !c.IsVisible);
        if (hidden == 0) return "";

        // "IsVisible false", never "IsVisible=…": UI/UiSweepCoverageTests lints
        // this file for a write to that property and it reads STRINGS as well as
        // code, which is right — it must not be talked out of a real find.
        return $" — {hidden} of its {descendants.Count} descendants are hidden (IsVisible false): " +
               "a HOST with nothing open in it, which is what this class almost always means";
    }

    /// <summary>
    ///     Whether <paramref name="child" />'s rectangle is the one
    ///     <see cref="HiddenSubtreePrewarmer.Realize" /> WROTE rather than one any
    ///     layout pass produced — in which case comparing it against
    ///     <paramref name="root" />'s own box says nothing about the product.
    ///     <para>
    ///         <b>The mechanism, because this cost a full investigation once.</b>
    ///         <c>Realize</c> measures and arranges each direct visual child of a
    ///         hidden root at <c>Rect(SlotSizeOf(root))</c> — the root's whole
    ///         layout SLOT. For the usual gated surface that is exactly right: the
    ///         root stretches, so its own bounds ARE the slot and nothing looks
    ///         out of place. But a root that is content-sized —
    ///         <c>SongBrowserView</c>'s <c>BrowserEmptyStates</c> is
    ///         <c>HorizontalAlignment="Center" VerticalAlignment="Center"
    ///         MaxWidth="460"</c>, arranged 409x105 inside an 809x425 cell — keeps
    ///         the small bounds a real pass gave it while its child is handed the
    ///         full slot. The sweep then read a 400x320 escape that no user can
    ///         ever see: the product re-measures the whole cell when
    ///         <c>IsEmptyStateVisible</c> flips, and the card lands centred.
    ///     </para>
    ///     <para>
    ///         Two conditions, and BOTH are needed. The root's own box must differ
    ///         from the slot — a root that stretches has bounds equal to it, and
    ///         then a child escaping is a real finding. And the child must sit
    ///         entirely INSIDE the slot rect, which is the rectangle
    ///         <c>Realize</c> handed it: <c>Arrange</c> resolves the child's own
    ///         alignment within that rect, so a centred card lands at an offset
    ///         (400,320 in the case above) rather than at the origin. A direct
    ///         child that escapes the SLOT is still reported.
    ///     </para>
    /// </summary>
    private static bool IsPrewarmArrangeRect(Control child, Control root)
    {
        var slot = HiddenSubtreePrewarmer.SlotSizeOf(root, new Size(HostWidth, HostHeight));

        if (Math.Abs(root.Bounds.Width - slot.Width) < 0.5 &&
            Math.Abs(root.Bounds.Height - slot.Height) < 0.5)
        {
            return false;
        }

        var b = child.Bounds;
        return b.X >= -0.5 && b.Y >= -0.5 &&
               b.Right <= slot.Width + 0.5 && b.Bottom <= slot.Height + 0.5;
    }

    /// <summary>Reads the private handler table <c>Interactive</c> keeps, which
    /// is the only place a XAML <c>Click="..."</c> subscription is visible. A
    /// missing field means "cannot tell" — and the check then stays silent,
    /// because a false "this button is dead" is worse than no report.</summary>
    private static bool HasClickHandler(Control control)
    {
        var field = typeof(Avalonia.Interactivity.Interactive).GetField("_eventHandlers",
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (field is null) return true;

        return field.GetValue(control) is System.Collections.IDictionary { Count: > 0 };
    }

    // ── artifacts ─────────────────────────────────────────────────────────

    private static string? Capture(Window window, string name, List<UiSweepFinding> findings)
    {
        try
        {
            var frame = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window);
            if (frame is null)
            {
                findings.Add(new UiSweepFinding(name, UiSweepSeverity.Warning, "no-capture",
                    "the headless window rendered no frame — is the session using headless drawing?"));
                return null;
            }

            var file = $"{name}.png";
            frame.Save(Path.Combine(ArtifactDirectory, file), new PngBitmapEncoderOptions());
            return file;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or IOException)
        {
            findings.Add(new UiSweepFinding(name, UiSweepSeverity.Warning, "no-capture",
                $"{ex.GetType().Name}: {FirstLine(ex.Message)}"));
            return null;
        }
    }

    /// <summary>
    ///     The contact sheet: every surface, its capture and its verdict on one
    ///     scrollable page, so the whole app can be scanned in one pass instead
    ///     of one screenshot at a time.
    /// </summary>
    private static void WriteContactSheet(UiSweepReport report)
    {
        Directory.CreateDirectory(ArtifactDirectory);
        File.WriteAllText(Path.Combine(ArtifactDirectory, "summary.txt"), report.Summary());

        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html><meta charset=\"utf-8\">");
        sb.AppendLine("<title>DotCharter UI sweep</title>");
        sb.AppendLine("<style>");
        sb.AppendLine("""
                      :root { color-scheme: dark light; }
                      body { font: 14px/1.5 system-ui, sans-serif; margin: 24px;
                             background: #14161a; color: #e6e8ec; }
                      h1 { font-size: 20px; margin: 0 0 4px; }
                      .meta { color: #9aa0aa; margin-bottom: 20px; white-space: pre-wrap;
                              font: 12px/1.5 ui-monospace, monospace; }
                      .grid { display: grid; gap: 16px;
                              grid-template-columns: repeat(auto-fill, minmax(320px, 1fr)); }
                      .card { border: 1px solid #2a2e36; border-radius: 8px; overflow: hidden;
                              background: #1b1e24; }
                      .card.fail { border-color: #b4433a; }
                      .shot { display: block; width: 100%; background: #0e0f12; }
                      .noshot { padding: 40px 12px; text-align: center; color: #6b7280;
                                font: 12px ui-monospace, monospace; }
                      .body { padding: 10px 12px 12px; }
                      .name { font-weight: 600; }
                      .group { color: #9aa0aa; font-size: 12px; }
                      .dc { color: #7d8794; font: 11px ui-monospace, monospace; margin-top: 4px; }
                      ul { margin: 8px 0 0; padding-left: 18px; }
                      li { font-size: 12px; }
                      li.err { color: #ff8a80; }
                      li.warn { color: #ffcc80; }
                      .ok { color: #7bd88f; font-size: 12px; margin-top: 6px; }
                      """);
        sb.AppendLine("</style>");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"<h1>DotCharter UI sweep — {report.Results.Count} surfaces</h1>");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"<div class=\"meta\">{WebUtility.HtmlEncode(report.Summary())}</div>");
        sb.AppendLine("<div class=\"grid\">");

        foreach (var r in report.Results.OrderBy(r => r.Passed).ThenBy(r => r.Group, StringComparer.Ordinal)
                     .ThenBy(r => r.Surface, StringComparer.Ordinal))
        {
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"<div class=\"card{(r.Passed ? "" : " fail")}\">");
            sb.AppendLine(r.ImageFile != null
                ? $"<img class=\"shot\" loading=\"lazy\" src=\"{WebUtility.HtmlEncode(r.ImageFile)}\" alt=\"{WebUtility.HtmlEncode(r.Surface)}\">"
                : $"<div class=\"noshot\">no capture — set {CaptureEnvVar}=1</div>");
            sb.AppendLine("<div class=\"body\">");
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"<div class=\"name\">{WebUtility.HtmlEncode(r.Surface)}</div>");
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"<div class=\"group\">{WebUtility.HtmlEncode(r.Group)} · " +
                $"{r.Bounds.Width:0}x{r.Bounds.Height:0} px · {r.VisualCount:N0} visuals · " +
                $"{r.ControlsChecked:N0} checked · {r.HiddenSurfacesRealized} hidden surfaces · " +
                $"{r.ElapsedMs:0.0} ms</div>");
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"<div class=\"dc\">DataContext: {WebUtility.HtmlEncode(r.DataContextType)} ({r.DataContextSource})</div>");

            if (r.Findings.Count == 0)
            {
                sb.AppendLine("<div class=\"ok\">clean</div>");
            }
            else
            {
                sb.AppendLine("<ul>");
                foreach (var f in r.Findings)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture,
                        $"<li class=\"{(f.Severity == UiSweepSeverity.Error ? "err" : "warn")}\">" +
                        $"<b>{WebUtility.HtmlEncode(f.Kind)}</b> — {WebUtility.HtmlEncode(f.Detail)}</li>");
                }

                sb.AppendLine("</ul>");
            }

            sb.AppendLine("</div></div>");
        }

        sb.AppendLine("</div>");
        File.WriteAllText(Path.Combine(ArtifactDirectory, "index.html"), sb.ToString());
    }

    // ── small helpers ─────────────────────────────────────────────────────

    private static string Label(Control control) => control switch
    {
        MenuItem { Header: string h } => Ellipsize(h),
        ContentControl { Content: string s } => Ellipsize(s),
        ContentControl { Content: TextBlock { Text: { } t } } => Ellipsize(t),
        _ => control.Name ?? "(unnamed)"
    };

    /// <summary>Names a realized hidden root in a finding. Its <c>x:Name</c> when
    /// it has one — that is what the markup and the prewarm lists call it — and
    /// otherwise its type, because a settings tab or a dialog view is named by
    /// being one.</summary>
    private static string RootLabel(Control root) =>
        string.IsNullOrEmpty(root.Name) ? root.GetType().Name : root.Name;

    private static string Ellipsize(string value, int max = 40) =>
        value.Length <= max ? value : value[..max] + "…";

    private static string FirstLine(string value)
    {
        var newline = value.IndexOfAny(['\r', '\n']);
        return newline < 0 ? value : value[..newline];
    }
}
