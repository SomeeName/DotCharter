using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Services.Plugins;

/// <summary>How one plugin candidate went.</summary>
public enum PluginLoadStatus
{
    /// <summary>The assembly is not there. Nothing registered, nothing shown.</summary>
    Absent,

    /// <summary>Found, matching <see cref="PluginApiVersion.Current" />, registered.</summary>
    Loaded,

    /// <summary>Found but built against another <see cref="PluginApiVersion" />: not registered, the user is told.</summary>
    Outdated,

    /// <summary>Found but unusable (no single module, constructor or <c>Register</c> threw): not registered, the user is told.</summary>
    Failed
}

/// <summary>One candidate's outcome. <paramref name="Name" /> is the plugin assembly's simple name;
/// <paramref name="Id" /> is the module's id when it got that far.</summary>
public sealed record PluginLoadResult(string Name, PluginLoadStatus Status, string? Id, int? ApiVersion, string Message);

/// <summary>What <see cref="PluginLoader.Load" /> did, registered as a singleton so startup can report it.</summary>
public sealed record PluginLoadReport(IReadOnlyList<PluginLoadResult> Results)
{
    public static PluginLoadReport Empty { get; } = new([]);

    public bool AnyLoaded => Results.Any(r => r.Status == PluginLoadStatus.Loaded);

    /// <summary>The candidates the user must hear about: present but refused.</summary>
    public IReadOnlyList<PluginLoadResult> Problems =>
        [.. Results.Where(r => r.Status is PluginLoadStatus.Outdated or PluginLoadStatus.Failed)];
}

/// <summary>
/// Loads the plugins a host assembly declares (<see cref="PluginAssemblyAttribute" />) and
///     registers the ones that match <see cref="PluginApiVersion.Current" />. Each plugin
///     assembly holds exactly one public <see cref="IPluginModule" />. A host that declares
///     none (a public build) registers nothing; an outdated or broken plugin is refused whole
///     (its registrations go into a scratch collection that is only copied over on success)
///     and reported, never thrown — the app always starts.
/// </summary>
public static class PluginLoader
{
    /// <summary>The plugin assembly names <paramref name="host" /> declares, in declaration order.</summary>
    public static IReadOnlyList<string> Declared(Assembly host) =>
        [.. host.GetCustomAttributes<PluginAssemblyAttribute>()
            .Select(a => a.AssemblyName)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>The file names (<c>&lt;name&gt;.dll</c>) of the plugins <paramref name="host" /> declares.</summary>
    public static IReadOnlyList<string> DeclaredFiles(Assembly? host) =>
        host is null ? [] : [.. Declared(host).Select(n => n + ".dll")];

    /// <param name="services">The app's collection; a loaded module's registrations land here.</param>
    /// <param name="assemblyNames">Plugin assembly simple names (<see cref="Declared" />).</param>
    /// <param name="resolveModule">Test seam: the module type of a plugin assembly, null when the
    /// assembly is absent. Defaults to <see cref="FindModule" />.</param>
    public static PluginLoadReport Load(
        IServiceCollection services, IEnumerable<string> assemblyNames, Func<string, Type?>? resolveModule = null)
    {
        resolveModule ??= FindModule;
        var results = new List<PluginLoadResult>();
        foreach (var name in assemblyNames) results.Add(LoadOne(services, name, resolveModule));
        return new PluginLoadReport(results);
    }

    /// <summary>
    ///     The one public, non-abstract <see cref="IPluginModule" /> in the assembly called
    ///     <paramref name="assemblyName" />; null when there is no such assembly. Throws when the
    ///     assembly holds none or several.
    /// </summary>
    public static Type? FindModule(string assemblyName)
    {
        Assembly assembly;
        try
        {
            assembly = Assembly.Load(new AssemblyName(assemblyName));
        }
        catch (FileNotFoundException)
        {
            return null;
        }

        var modules = assembly.GetExportedTypes()
            .Where(t => typeof(IPluginModule).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false })
            .ToList();
        return modules.Count switch
        {
            1 => modules[0],
            0 => throw new InvalidOperationException($"{assemblyName} holds no public {nameof(IPluginModule)}"),
            _ => throw new InvalidOperationException(
                $"{assemblyName} holds {modules.Count} {nameof(IPluginModule)} types; a plugin has exactly one"),
        };
    }

    private static PluginLoadResult LoadOne(IServiceCollection services, string name, Func<string, Type?> resolveModule)
    {
        Type? type;
        try
        {
            type = resolveModule(name);
        }
        catch (Exception ex)
        {
            // A present assembly that cannot load (built for another runtime, a missing
            // dependency, no single module) is a broken plugin, not an absent one.
            return new(name, PluginLoadStatus.Failed, null, null, $"could not be loaded: {ex.Message}");
        }

        if (type is null) return new(name, PluginLoadStatus.Absent, null, null, "not present");
        if (!typeof(IPluginModule).IsAssignableFrom(type) || type.IsAbstract)
            return new(name, PluginLoadStatus.Failed, null, null, $"{type.FullName} is not an {nameof(IPluginModule)}");

        IPluginModule module;
        string id;
        int apiVersion;
        try
        {
            module = (IPluginModule)Activator.CreateInstance(type)!;
            id = module.Id;
            apiVersion = module.ApiVersion;
        }
        catch (Exception ex)
        {
            return new(name, PluginLoadStatus.Failed, null, null, $"could not be created: {ex.Message}");
        }

        if (apiVersion != PluginApiVersion.Current)
        {
            return new(name, PluginLoadStatus.Outdated, id, apiVersion,
                $"built for plugin API {apiVersion}, this DotCharter needs {PluginApiVersion.Current}");
        }

        var scratch = new ServiceCollection();
        try
        {
            module.Register(scratch);
        }
        catch (Exception ex)
        {
            return new(name, PluginLoadStatus.Failed, id, apiVersion, $"failed to register: {ex.Message}");
        }

        foreach (var descriptor in scratch) services.Add(descriptor);
        return new(name, PluginLoadStatus.Loaded, id, apiVersion, $"loaded ({scratch.Count} services)");
    }
}
