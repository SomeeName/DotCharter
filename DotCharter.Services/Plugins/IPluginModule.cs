using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Services.Plugins;

/// <summary>
/// The plugin contract. A plugin assembly holds exactly one public module type; the app finds
///     it through the assembly's <see cref="PluginAssemblyAttribute" /> declaration on the
///     host (<see cref="PluginLoader.Declared" />), never by a compile-time reference from
///     product code. A module must report the <see cref="PluginApiVersion.Current" /> it was
///     built against: a module whose <see cref="ApiVersion" /> differs is not registered and
///     the app starts without it.
/// </summary>
public interface IPluginModule
{
    /// <summary>Stable kebab-case id, shown in the log and the "plugin outdated" notice.</summary>
    string Id { get; }

    /// <summary>The <see cref="PluginApiVersion.Current" /> the module was compiled against.
    /// A module returns that constant, so a stale binary reports the OLD value.</summary>
    int ApiVersion { get; }

    /// <summary>Adds the module's services. Called once, before the provider is built.</summary>
    void Register(IServiceCollection services);
}

/// <summary>The plugin API version. Bump it whenever <see cref="IPluginModule" /> or a seam a plugin
/// registers against changes shape; a plugin built against another value is refused, never half-loaded.</summary>
public static class PluginApiVersion
{
    public const int Current = 2;
}

/// <summary>
///     Declares one plugin assembly, by simple name, on the host assembly that references it.
///     <c>DotCharter.UI.csproj</c> writes one per project at the top of a <c>plugins/&lt;name&gt;/</c>
///     folder; a public build writes none.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class PluginAssemblyAttribute(string assemblyName) : Attribute
{
    public string AssemblyName { get; } = assemblyName;
}
