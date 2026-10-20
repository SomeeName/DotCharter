using System.Reflection;
using System.Text.RegularExpressions;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     The public build holds no type that reads or decrypts a song package (#889, epic #882):
///     reflection over every <c>DotCharter.*</c> assembly the app ships, by the reader's type
///     names. A reader belongs in an optional plugin, which this check skips: the plugins the
///     app declares (<see cref="DotCharter.Services.Plugins.PluginLoader.Declared" />). When a
///     plugin is in the build, the reader names must be found THERE, which proves the pattern
///     still matches a reader.
/// </summary>
public sealed partial class PublicAssembliesHaveNoPackageReaderTests
{
    private static IReadOnlyList<string> Plugins =>
        DotCharter.Services.Plugins.PluginLoader.Declared(typeof(DotCharter.UI.App).Assembly);

    [Fact]
    public void No_public_assembly_defines_a_package_reader_type()
    {
        // DotCharter.dll is the app itself (DotCharter.UI's assembly name).
        var shipped = Directory.EnumerateFiles(AppContext.BaseDirectory, "DotCharter*.dll")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !name!.StartsWith("DotCharter.Tests", StringComparison.Ordinal) &&
                           !Plugins.Any(p => name.StartsWith(p, StringComparison.Ordinal)))
            .Order(StringComparer.Ordinal)
            .ToList();
        // The app's own assemblies are all here: a scan over nothing would pass.
        foreach (var expected in new[] { "DotCharter", "DotCharter.Core", "DotCharter.Services", "DotCharter.Tone", "DotCharter.Presentation" })
            Assert.Contains(expected, shipped);

        var offenders = shipped
            .SelectMany(name => TypesOf(Assembly.Load(new AssemblyName(name!))))
            .Where(type => ReaderTypeName().IsMatch(type.Name))
            .Select(type => $"{type.Assembly.GetName().Name}: {type.FullName}")
            .ToList();

        Assert.True(offenders.Count == 0, LintReport.Of(
            "The public build reads no song package (#889)",
            offenders,
            "a package reader belongs in an optional plugin; the public tree keeps the writer and the seams (Services/Plugins)",
            "docs/agents/services/parsing-psarc.md"));
    }

    [Fact]
    public void The_pattern_still_names_a_plugins_reader()
    {
        Assert.SkipWhen(Plugins.Count == 0, "No plugin in this build (a public build): nothing to match against.");

        var names = Plugins.SelectMany(p => TypesOf(Assembly.Load(new AssemblyName(p)))).Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var reader in new[] { "PsarcArchive", "RocksmithSngReader", "SngToDomainMapper", "PsarcManifestParser" })
        {
            Assert.Contains(reader, names);
            Assert.Matches(ReaderTypeName(), reader);
        }
    }

    private static IEnumerable<Type> TypesOf(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException partial)
        {
            return partial.Types.OfType<Type>();
        }
    }

    // The reader's container, SNG and library types (#885/#886 names), and any Psarc*Reader or *Decryptor.
    [GeneratedRegex(@"^(PsarcArchive|Psarc\w*Reader|RocksmithSngReader|SngToDomainMapper|PsarcManifestParser|PsarcLibrarySource|\w*Decryptor)$")]
    private static partial Regex ReaderTypeName();
}
