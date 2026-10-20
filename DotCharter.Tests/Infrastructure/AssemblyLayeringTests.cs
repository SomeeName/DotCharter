using System.Reflection;
using DotCharter.Analysis.AudioToChart;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.PlayMode;
using DotCharter.Presentation.Fakes;
using DotCharter.Rendering.Engines;
using DotCharter.Services.Plugins;
using DotCharter.UI;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     The dependency direction stated in the root <c>AGENTS.md</c> is an
///     ABSOLUTE, and until 0.41 exactly ONE of its claims was tested:
///     <c>RenderingLayeringTests</c> asserted that <c>DotCharter.Rendering</c>
///     does not reference the UI or the service layer, and it had no siblings.
///     Nothing asserted that <c>Analysis</c> stays off <c>Services</c>, or that
///     <c>Core</c> keeps zero <c>DotCharter.*</c> references — both of which the
///     guides state as things that "may never" happen.
///     <para>
///         A layering breach is a one-line csproj edit that builds perfectly
///         clean and is invisible in review; the cost only shows up later, when
///         the lower project can no longer be tested, reused or reasoned about
///         on its own. So the whole direction is a Theory here, one row per
///         assembly, each carrying the EXACT set of first-party assemblies it is
///         allowed to reference.
///     </para>
///     <para>
///         The check is on <see cref="Assembly.GetReferencedAssemblies" />, i.e.
///         on what the compiler actually emitted, not on the csproj text — a
///         declared-but-unused reference is harmless, and a reference that
///         arrives TRANSITIVELY is still a real dependency. Hence
///         subset-of-allowed rather than set equality.
///     </para>
/// </summary>
public sealed class AssemblyLayeringTests
{
    /// <summary>
    ///     The UI assembly is named <c>DotCharter</c>, not <c>DotCharter.UI</c>
    ///     (<c>&lt;AssemblyName&gt;</c> in its csproj) — spelling only the dotted
    ///     name would let every "does anything reference the UI?" check pass
    ///     vacuously.
    /// </summary>
    private const string UiAssemblyName = "DotCharter";

    /// <summary>
    ///     Assembly under test → the first-party assemblies it MAY reference.
    /// </summary>
    private static readonly (string Name, string[] Allowed)[] LayerTable =
    [
        // The bottom of the graph. Zero first-party references, full stop —
        // that is what makes the domain model testable and reusable on its own.
        ("DotCharter.Core", []),
        // A leaf too (it references nothing), but NOT unreferenced: Services and
        // UI both depend on it. Those are two different claims, and the root
        // guide's "standalone leaf" wording used to conflate them.
        ("DotCharter.Localization", []),
        // Core + JuceSharp.Dsp only. Services references Analysis (the audio-lint
        // wrapper over ChartAudioLintAnalyzer), so the reverse edge is a cycle.
        ("DotCharter.Analysis", ["DotCharter.Core"]),
        // Core only, and now referenced by the UI alone: the replay video
        // exporter was the one thing in Services that reached for
        // HeadlessHighwayRenderer, and it went in 0.59 — so the ProjectReference
        // went with it and the Services row below no longer names Rendering. A
        // reverse edge would still be a cycle if Rendering ever grew one.
        ("DotCharter.Rendering", ["DotCharter.Core"]),
        // The tone rig and the audio host, split out of Services so the
        // standalone rig (DotCharter.RigLab) ships without the charting stack.
        // Core + Localization only: never Services, never Analysis (note
        // detection), and see the package fact further down.
        ("DotCharter.Tone", ["DotCharter.Core", "DotCharter.Localization"]),
        // Everything below it — but never the UI. The composition root lives up
        // there; a service that reaches for it cannot be constructed in a test.
        ("DotCharter.Services",
            ["DotCharter.Core", "DotCharter.Analysis", "DotCharter.Localization", "DotCharter.Tone"]),
        // Screen contracts and view models (docs/agents/ui/target-architecture.md § 2).
        // Services abstractions only; the UI framework and the audio ABI are
        // held off by the fact further down.
        ("DotCharter.Presentation",
            ["DotCharter.Core", "DotCharter.Localization", "DotCharter.Services", "DotCharter.Tone"]),
        // Services-free fakes of those contracts, for the gallery and the tests.
        ("DotCharter.Presentation.Fakes",
            ["DotCharter.Presentation", "DotCharter.Core", "DotCharter.Localization", "DotCharter.Services", "DotCharter.Tone"])
    ];

    public static TheoryData<string, string[]> Layers
    {
        get
        {
            var data = new TheoryData<string, string[]>();
            foreach (var (name, allowed) in LayerTable) data.Add(name, allowed);
            return data;
        }
    }

    private static Assembly AssemblyNamed(string name) => name switch
    {
        "DotCharter.Core" => typeof(Chord).Assembly,
        "DotCharter.Localization" => typeof(LocalizationService).Assembly,
        "DotCharter.Analysis" => typeof(ChartAudioLintAnalyzer).Assembly,
        "DotCharter.Rendering" => typeof(Fretboard3DEngine).Assembly,
        "DotCharter.Services" => typeof(ProjectState).Assembly,
        "DotCharter.Tone" => typeof(RigConfig).Assembly,
        "DotCharter.Presentation" => typeof(IStartScreen).Assembly,
        "DotCharter.Presentation.Fakes" => typeof(FakeVariantNames).Assembly,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "no anchor type for that assembly")
    };

    [Theory]
    [MemberData(nameof(Layers))]
    public void Each_layer_references_only_what_it_is_allowed_to(string assemblyName, string[] allowed)
    {
        var assembly = AssemblyNamed(assemblyName);
        // The anchor type must really live in the assembly the row names,
        // otherwise a renamed project would silently test the wrong binary.
        Assert.Equal(assemblyName, assembly.GetName().Name);

        var illegal = FirstPartyReferencesOf(assembly)
            .Except(allowed, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(illegal.Length == 0,
            $"{assemblyName} references {string.Join(", ", illegal)} — allowed: " +
            (allowed.Length == 0 ? "(nothing first-party)" : string.Join(", ", allowed)));
    }

    /// <summary>
    ///     The rule the whole table exists for, spelled once so a failure reads
    ///     as "something below the UI reached up" rather than as a row index.
    /// </summary>
    [Theory]
    [MemberData(nameof(Layers))]
    public void No_layer_below_the_UI_references_the_UI_assembly(string assemblyName, string[] allowed)
    {
        _ = allowed;
        Assert.DoesNotContain(UiAssemblyName, FirstPartyReferencesOf(AssemblyNamed(assemblyName)));
    }

    /// <summary>
    ///     An optional plugin (plugins/&lt;name&gt;/, declared on the app's assembly) sits ABOVE
    ///     Tone and Services and below the UI: it may use the layers it extends and nothing
    ///     else, and none of the layers in <see cref="LayerTable" /> may name it (their
    ///     allowlists don't). It exists only in a build that has a plugin, so the row applies
    ///     then; a public clone and <c>Invoke-Gate.ps1 -Public</c> skip it.
    /// </summary>
    [Fact]
    public void The_optional_plugins_reference_only_the_layers_they_extend()
    {
        var plugins = PluginLoader.Declared(typeof(App).Assembly);
        Assert.SkipWhen(plugins.Count == 0, "this build has no plugin (plugins/ is empty or DotCharterPlugins=false)");
        string[] allowed = ["DotCharter.Core", "DotCharter.Localization", "DotCharter.Tone", "DotCharter.Services"];

        foreach (var plugin in plugins)
        {
            var references = FirstPartyReferencesOf(Assembly.Load(new AssemblyName(plugin)));

            // Non-vacuity: it really is built on the layers it extends.
            Assert.Contains("DotCharter.Services", references);
            Assert.Empty(references.Except(allowed, StringComparer.Ordinal));
            Assert.DoesNotContain(UiAssemblyName, references);
            foreach (var (name, _) in LayerTable) Assert.DoesNotContain(plugin, FirstPartyReferencesOf(AssemblyNamed(name)));
        }
    }


    /// <summary>
    ///     Guards the guard. An allowlist naming the UI — or an assembly that is
    ///     not itself a row of this table — would let the theory above pass while
    ///     the layering is broken.
    /// </summary>
    [Fact]
    public void The_allowlists_themselves_stay_below_the_UI()
    {
        var known = LayerTable.Select(l => l.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var (name, allowed) in LayerTable)
        {
            Assert.DoesNotContain(UiAssemblyName, allowed);
            foreach (var dependency in allowed)
            {
                Assert.True(known.Contains(dependency),
                    $"{name}'s allowlist names '{dependency}', which is not a row of this table.");
            }
        }
    }

    /// <summary>
    ///     Name prefixes the Presentation assemblies may never reference: the UI
    ///     framework, the dock, the drawing library and the audio ABI (which
    ///     stays behind Services). A prefix, so <c>Avalonia.Base</c> and
    ///     <c>JuceSharp.Dsp</c> count as much as the root assemblies do.
    /// </summary>
    internal static readonly string[] PresentationForbiddenPrefixes = ["Avalonia", "Dock", "SkiaSharp", "JuceSharp"];

    public static TheoryData<string> PresentationAssemblies => new("DotCharter.Presentation", "DotCharter.Presentation.Fakes");

    /// <summary>
    ///     Services references Avalonia and JuceSharp itself, so both arrive
    ///     TRANSITIVELY in the Presentation projects and a stray
    ///     <c>using Avalonia</c> in a view model compiles. What the compiler
    ///     emitted is the evidence; this reads it.
    /// </summary>
    [Theory]
    [MemberData(nameof(PresentationAssemblies))]
    public void Presentation_references_no_UI_framework_and_no_audio_ABI(string assemblyName)
    {
        var offending = ForbiddenReferencesOf(AssemblyNamed(assemblyName));
        Assert.True(offending.Length == 0,
            $"{assemblyName} references {string.Join(", ", offending)} — view models bind through " +
            "contracts and reach the engine through Services (target-architecture.md § 2).");
    }

    /// <summary>
    ///     The prefix match itself, run against an assembly that DOES reference
    ///     Avalonia and JuceSharp — the rig's UI — so the fact above cannot pass
    ///     because the match is broken. (It used to be the app's UI assembly,
    ///     whose only JuceSharp reference was the stem orchestrator's registration;
    ///     #662 deleted that, and the UI now reaches JuceSharp only through
    ///     Services and Tone.)
    /// </summary>
    [Fact]
    public void The_forbidden_reference_match_finds_the_rig_UI_assemblys_framework_references()
    {
        var found = ForbiddenReferencesOf(typeof(DotCharter.Tone.UI.Views.PlayMode.AmpFxRigView).Assembly);
        Assert.Equal("DotCharter.Tone.UI", typeof(DotCharter.Tone.UI.Views.PlayMode.AmpFxRigView).Assembly.GetName().Name);
        Assert.Contains(found, n => n.StartsWith("Avalonia", StringComparison.Ordinal));
        Assert.Contains(found, n => n.StartsWith("JuceSharp", StringComparison.Ordinal));
    }

    /// <summary>
    ///     A fake in the product assembly is one <c>DataContext</c> typo away
    ///     from shipping. Checked twice: in the emitted metadata, and in the
    ///     csproj, because an unused ProjectReference emits nothing and would
    ///     still let the next line of product code compile against a fake.
    /// </summary>
    [Fact]
    public void The_UI_assembly_does_not_reference_the_Presentation_fakes()
    {
        var ui = typeof(App).Assembly;
        Assert.Equal(UiAssemblyName, ui.GetName().Name);
        Assert.DoesNotContain("DotCharter.Presentation.Fakes", FirstPartyReferencesOf(ui));
        // Non-vacuity: the UI does reference the contracts project, so the list is not empty by accident.
        Assert.Contains("DotCharter.Presentation", FirstPartyReferencesOf(ui));

        var csproj = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "DotCharter.UI.csproj")));
        var references = System.Text.RegularExpressions.Regex.Matches(csproj, "<ProjectReference\\s+Include=\"([^\"]+)\"")
            .Select(m => m.Groups[1].Value)
            .ToList();
        Assert.Contains(references, r => r.EndsWith("DotCharter.Presentation.csproj", StringComparison.Ordinal));
        Assert.DoesNotContain(references, r => r.Contains("Presentation.Fakes", StringComparison.Ordinal));
    }

    /// <summary>
    ///     Name prefixes <c>DotCharter.Tone</c> may never reference: the charting
    ///     stack the standalone rig exists to leave behind — stem separation
    ///     (ONNX Runtime and JuceSharp's separators), note detection and chart
    ///     analysis (<c>DotCharter.Analysis</c>), sheet music, the psarc library
    ///     index, the Wwise tools and the UI framework. (The Anthropic SDK is
    ///     allowed since 2026-09-26: the text-to-tone designer moved here so the
    ///     standalone rig can design tones, and its Claude provider needs it.)
    /// </summary>
    internal static readonly string[] ToneForbiddenPrefixes =
    [
        "DotCharter.Analysis", "DotCharter.Services", "Microsoft.ML.OnnxRuntime",
        "JuceSharp.StemSeparation", "AlphaTab", "AlphaSkia", "Microsoft.Data.Sqlite", "SQLitePCL",
        "LWI.WwiseTools", "Ww2Ogg", "NVorbis", "Avalonia", "SkiaSharp"
    ];

    [Fact]
    public void Tone_references_none_of_the_charting_stack()
    {
        var tone = AssemblyNamed("DotCharter.Tone");
        var references = tone.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty).ToArray();
        // Non-vacuity: the rig really does sit on the native engine.
        Assert.Contains("JuceSharp", references);

        var offending = references
            .Where(n => ToneForbiddenPrefixes.Any(p => n.StartsWith(p, StringComparison.Ordinal)))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.True(offending.Length == 0,
            $"DotCharter.Tone references {string.Join(", ", offending)} — the standalone rig would ship it. " +
            "Keep the rig on Core + Localization + JuceSharp, and put the charting-side half in Services.");
    }

    private static string[] ForbiddenReferencesOf(Assembly assembly) =>
        assembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(n => PresentationForbiddenPrefixes.Any(p => n.StartsWith(p, StringComparison.Ordinal)))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string[] FirstPartyReferencesOf(Assembly assembly) =>
        assembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(n => n is UiAssemblyName || n.StartsWith("DotCharter.", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
}
