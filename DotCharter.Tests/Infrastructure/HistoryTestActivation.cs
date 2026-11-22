using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Infrastructure;

/// <summary>Supplies history through DI so regression cases can run against either constructor shape.</summary>
internal static class HistoryTestActivation
{
    public static T Create<T>(IHistoryService history, params object[] arguments) where T : class
    {
        using var services = new ServiceCollection().AddSingleton(history).BuildServiceProvider();
        return ActivatorUtilities.CreateInstance<T>(services, arguments);
    }
}
