using System.Reflection;
using System.Runtime.CompilerServices;
using DotCharter.Analysis.AudioToChart;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.PlayMode;
using DotCharter.Presentation.Fakes;
using DotCharter.Rendering.Engines;
using DotCharter.Tone.UI.Views.PlayMode;
using DotCharter.UI;
using DotCharter.UI.Gallery;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     Every first-party type sits in a namespace that starts with its own
///     project's name (#836). Until then <c>DotCharter.Tone</c> declared
///     <c>DotCharter.Services.*</c> and <c>DotCharter.Tone.UI</c> declared
///     <c>DotCharter.UI.*</c>, so a reader looking for
///     <c>DotCharter.Services.Audio.JuceAudioPlaybackService</c> opened the wrong
///     project, and a namespace was split across two assemblies.
///     <para>
///         The check reads the COMPILED types, so it sees partials, generated
///         code-behind and linked files exactly as the compiler placed them. Two
///         projects' assembly names differ from their project names (the app is
///         <c>DotCharter</c>, the gallery <c>DotCharter.Gallery</c>); for those the
///         project name is the root. Namespaces that source generators emit into
///         (Avalonia's compiled XAML, the MVVM toolkit's internals) are not ours.
///     </para>
/// </summary>
public sealed class NamespaceMatchesAssemblyTests
{
    private static readonly string[] GeneratorNamespaces =
    [
        "CompiledAvaloniaXaml",
        "CommunityToolkit.Mvvm.ComponentModel.__Internals",
        "Microsoft.CodeAnalysis",
        "System.Runtime.CompilerServices"
    ];

    public static TheoryData<string> Assemblies =>
    [
        "DotCharter.Core", "DotCharter.Localization", "DotCharter.Analysis", "DotCharter.Rendering",
        "DotCharter.Tone", "DotCharter.Services", "DotCharter.Presentation", "DotCharter.Presentation.Fakes",
        "DotCharter.Tone.UI", "DotCharter", "DotCharter.RigLab", "DotCharter.Gallery"
    ];

    private static Assembly AssemblyNamed(string name) => name switch
    {
        "DotCharter.Core" => typeof(Chord).Assembly,
        "DotCharter.Localization" => typeof(LocalizationService).Assembly,
        "DotCharter.Analysis" => typeof(ChartAudioLintAnalyzer).Assembly,
        "DotCharter.Rendering" => typeof(Fretboard3DEngine).Assembly,
        "DotCharter.Tone" => typeof(RigConfig).Assembly,
        "DotCharter.Services" => typeof(ProjectState).Assembly,
        "DotCharter.Presentation" => typeof(IStartScreen).Assembly,
        "DotCharter.Presentation.Fakes" => typeof(FakeVariantNames).Assembly,
        "DotCharter.Tone.UI" => typeof(AmpFxRigView).Assembly,
        "DotCharter" => typeof(App).Assembly,
        "DotCharter.RigLab" => typeof(DotCharter.RigLab.RigLabHost).Assembly,
        "DotCharter.Gallery" => typeof(GalleryWindow).Assembly,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "no anchor type for that assembly")
    };

    /// <summary>The namespace root a type in <paramref name="assemblyName" /> must use: its project's name.</summary>
    internal static string RootOf(string assemblyName) => assemblyName switch
    {
        "DotCharter" => "DotCharter.UI",
        "DotCharter.Gallery" => "DotCharter.UI.Gallery",
        _ => assemblyName
    };

    internal static IReadOnlyList<string> Offenders(IEnumerable<Type> types, string root) =>
        types
            .Where(t => !t.IsNested && !t.Name.Contains('<', StringComparison.Ordinal))
            .Where(t => t.GetCustomAttribute<CompilerGeneratedAttribute>() is null)
            .Where(t => !GeneratorNamespaces.Any(g => t.Namespace == g || t.Namespace?.StartsWith(g + ".", StringComparison.Ordinal) == true))
            .Where(t => !(t.Namespace == root || t.Namespace?.StartsWith(root + ".", StringComparison.Ordinal) == true))
            .Select(t => $"{t.FullName}: namespace {t.Namespace ?? "(global)"} is not under {root}")
            .Order(StringComparer.Ordinal)
            .ToList();

    [Theory]
    [MemberData(nameof(Assemblies))]
    public void Every_type_is_in_a_namespace_under_its_projects_name(string assemblyName)
    {
        var assembly = AssemblyNamed(assemblyName);
        Assert.Equal(assemblyName, assembly.GetName().Name);

        var offenders = Offenders(assembly.GetTypes(), RootOf(assemblyName));

        Assert.True(offenders.Count == 0, LintReport.Of(
            $"{assemblyName}: every type's namespace starts with {RootOf(assemblyName)}",
            offenders,
            "Declare the type in a namespace under its project's name (the folder path below the project), and update its users."));
    }

    /// <summary>The check is not vacuous: a type outside its root is reported, one inside is not.</summary>
    [Fact]
    public void A_type_outside_its_projects_namespace_is_reported()
    {
        var offenders = Offenders([typeof(RigConfig), typeof(ProjectState)], "DotCharter.Tone");

        Assert.Equal([$"{typeof(ProjectState).FullName}: namespace {typeof(ProjectState).Namespace} is not under DotCharter.Tone"], offenders);
    }
}
