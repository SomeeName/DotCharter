using System.Xml.Linq;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     Pins the publish settings that keep the release payload small WITHOUT
///     slowing startup (#410): which assemblies ship without ReadyToRun, which
///     are trimmed, and how the trim is switched on.
/// </summary>
/// <remarks>
///     <para>
///         Each of these is a one-line csproj edit that builds clean and publishes
///         clean, and only shows up later as a slower start (a startup assembly
///         added to the ReadyToRun exclude list JITs on every launch), a broken
///         feature (a trimmed assembly something reflects over), or a different
///         app (<c>PublishTrimmed</c> as a property turns on the linker's
///         trimmed-app feature switches, among them "System.Text.Json reflection
///         off", for the WHOLE app). The release script re-checks the published
///         result (<c>Test-PublishTrimSettings</c>); this catches the edit.
///     </para>
/// </remarks>
public sealed class PublishPayloadSettingsTests
{
    /// <summary>The only assemblies the publish may trim: COM bindings with no reflection.</summary>
    private static readonly string[] TrimmedAssemblies = ["Silk.NET.Direct3D11", "Silk.NET.DXGI"];

    /// <summary>
    ///     Assemblies on the way to the <c>StartupPhases:</c> line (measured by
    ///     mapping the published app's loaded images, #410). None may lose
    ///     ReadyToRun. Matched as a file-name prefix.
    /// </summary>
    private static readonly string[] StartupAssemblyPrefixes =
    [
        "DotCharter", "JuceSharp", "Avalonia.Base", "Avalonia.Controls", "Avalonia.Markup", "Avalonia.Skia",
        "Avalonia.Win32", "Avalonia.Themes", "Avalonia.Desktop", "Avalonia.Vulkan", "Avalonia.OpenGL",
        "Avalonia.HarfBuzz", "SkiaSharp", "HarfBuzzSharp", "Silk.NET.Core", "Silk.NET.Vulkan", "Dock.",
        "CommunityToolkit", "Microsoft.Extensions.DependencyInjection", "Serilog", "Microsoft.Data.Sqlite"
    ];

    private static XDocument UiProject() =>
        XDocument.Load(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "DotCharter.UI.csproj")));

    private static IEnumerable<XElement> Named(XDocument doc, string name) =>
        doc.Descendants().Where(e => e.Name.LocalName == name);

    private static bool InsideTarget(XElement e) => e.Ancestors().Any(a => a.Name.LocalName == "Target");

    [Fact]
    public void The_release_publish_is_still_ReadyToRun()
    {
        var r2r = Named(UiProject(), "PublishReadyToRun").Where(e => !InsideTarget(e)).ToList();

        Assert.Contains(r2r, e => e.Value.Trim() == "true" && ((string?)e.Attribute("Condition") ?? "").Contains("win-x64", StringComparison.Ordinal));
    }

    [Fact]
    public void No_startup_assembly_is_excluded_from_ReadyToRun()
    {
        var excluded = Named(UiProject(), "PublishReadyToRunExclude")
            .SelectMany(e => ((string?)e.Attribute("Include") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToList();

        Assert.NotEmpty(excluded);
        var offenders = excluded
            .Where(name => StartupAssemblyPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        Assert.True(offenders.Count == 0,
            "These assemblies load before the window is interactive; without ReadyToRun every launch JIT-compiles them: " +
            string.Join(", ", offenders));
    }

    [Fact]
    public void PublishTrimmed_is_only_ever_set_inside_a_target()
    {
        var doc = UiProject();

        var asProperty = Named(doc, "PublishTrimmed").Where(e => !InsideTarget(e)).ToList();
        Assert.True(asProperty.Count == 0,
            "PublishTrimmed set as a PROPERTY turns on the linker's trimmed-app feature switches (System.Text.Json " +
            "reflection, DefaultValueAttribute, Serilog structure capture, startup hooks ...) for the whole app. " +
            "Set it inside the DotCharterEnableBindingTrim target.");
        Assert.Contains(Named(doc, "PublishTrimmed"), e => InsideTarget(e) && e.Value.Trim() == "true");
        Assert.Contains(Named(doc, "_RequiresILLinkPack"), e => !InsideTarget(e) && e.Value.Trim() == "true");
    }

    [Fact]
    public void Only_the_COM_bindings_are_trimmable_and_everything_else_is_copied()
    {
        var links = Named(UiProject(), "ManagedAssemblyToLink").Where(InsideTarget).ToList();

        var trimmable = links.Where(e => e.Elements().Any(m => m.Name.LocalName == "IsTrimmable" && m.Value.Trim() == "true")).ToList();
        var copied = links.Where(e => e.Elements().Any(m => m.Name.LocalName == "IsTrimmable" && m.Value.Trim() == "false")).ToList();
        Assert.Single(trimmable);
        Assert.Single(copied);

        var opted = (string?)trimmable[0].Attribute("Condition") ?? "";
        var rest = (string?)copied[0].Attribute("Condition") ?? "";
        var named = System.Text.RegularExpressions.Regex.Matches(opted, "'([A-Za-z][^'%$]*)'").Select(m => m.Groups[1].Value).Order().ToArray();
        Assert.Equal(TrimmedAssemblies.Order().ToArray(), named);
        foreach (var assembly in TrimmedAssemblies)
            Assert.Contains($"'%(Filename)' != '{assembly}'", rest, StringComparison.Ordinal);
        Assert.DoesNotContain(" or ", rest, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_release_script_checks_the_published_trim()
    {
        var script = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "tools", "release", "New-Release.ps1"));

        Assert.Contains("function Test-PublishTrimSettings", script, StringComparison.Ordinal);
        Assert.Contains("Test-PublishTrimSettings -PublishDir $PublishDir", script, StringComparison.Ordinal);
    }
}
