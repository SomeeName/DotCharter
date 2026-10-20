using System.Reflection;

namespace DotCharter.Services.Infrastructure;

/// <summary>The app's release identity for screens, diagnostics and persisted provenance.</summary>
public static class AppVersionInfo
{
    public static string Current => FromAssembly(
        AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "DotCharter")
        ?? Assembly.GetEntryAssembly());

    public static string FromAssembly(Assembly? assembly)
    {
        var informational = assembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational)) return informational.Split('+')[0];
        return assembly?.GetName().Version?.ToString(3) ?? "dev";
    }
}
