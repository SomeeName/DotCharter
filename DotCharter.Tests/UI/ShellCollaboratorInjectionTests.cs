using System.Reflection;
using DotCharter.Tone.Audio.Abstractions;
using DotCharter.Services.Editing.Highway;
using DotCharter.Services.Infrastructure.Documents;
using DotCharter.Services.Session;
using DotCharter.UI;
using DotCharter.UI.Services;
using DotCharter.UI.ViewModels.Diagnostics;
using DotCharter.UI.Views;
using DotCharter.UI.Views.Shell;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.UI;

/// <summary>
///     The window's collaborators and the diagnostics dialog service used to fetch
///     their services from the static <c>App.Services</c> locator. They take them
///     through their constructors now — as <see cref="Func{TResult}" /> factories,
///     so a service is still resolved at the moment it is used and never because
///     the window was built (the prewarm runs after first paint; the perf
///     autopilot's audio service must not be constructed by the shell).
/// </summary>
public sealed class ShellCollaboratorInjectionTests
{
    [Fact]
    public void MainWindowPrewarm_TakesTheWindowAndServiceFactories()
    {
        Assert.Equal(
            [typeof(MainWindow), typeof(Func<IServiceProvider>), typeof(Func<IAppLogger>)],
            ConstructorParameterTypes(typeof(MainWindowPrewarm)));
    }

    [Fact]
    public void MainWindowDiagnostics_TakesTheWindowAndServiceFactories()
    {
        Assert.Equal(
            [
                typeof(MainWindow),
                typeof(Func<IAppLogger>),
                typeof(Func<IProjectSessionService>),
                typeof(Func<IAudioPlaybackService>),
                typeof(Func<IHighwayEditingService>),
                typeof(Func<IHistoryService>),
                typeof(Func<IProjectDocumentService>),
                typeof(Func<ProjectState>),
            ],
            ConstructorParameterTypes(typeof(MainWindowDiagnostics)));
    }

    [Fact]
    public void ConstructingTheCollaborators_ResolvesNothing()
    {
        var resolutions = 0;

        _ = new MainWindowPrewarm(null!, Counted<IServiceProvider>(), Counted<IAppLogger>());
        _ = new MainWindowDiagnostics(null!,
            Counted<IAppLogger>(),
            Counted<IProjectSessionService>(),
            Counted<IAudioPlaybackService>(),
            Counted<IHighwayEditingService>(),
            Counted<IHistoryService>(),
            Counted<IProjectDocumentService>(),
            Counted<ProjectState>());

        Assert.Equal(0, resolutions);

        Func<T?> Counted<T>() where T : class
        {
            return () =>
            {
                resolutions++;
                return null;
            };
        }
    }

    [Fact]
    public void LogPopupHostTransparency_AsksTheInjectedLogger()
    {
        var asked = 0;
        var diagnostics = new MainWindowDiagnostics(null!,
            () =>
            {
                asked++;
                return null;
            },
            () => null, () => null, () => null, () => null, () => null, () => null);

        diagnostics.LogPopupHostTransparency();

        Assert.Equal(1, asked);
    }

    [Fact]
    public void DiagnosticsDialogService_TakesAViewModelFactory() => Assert.Equal([typeof(Func<DiagnosticsViewModel>)], ConstructorParameterTypes(typeof(DiagnosticsDialogService)));

    [Fact]
    public void DiagnosticsDialogService_BuildsNoViewModelUntilAsked()
    {
        var built = 0;
        var service = new DiagnosticsDialogService(() =>
        {
            built++;
            return null!;
        });

        Assert.Null(service.Open);
        Assert.Equal(0, built);
    }

    [Fact]
    public void TheCompositionRoot_RegistersTheDiagnosticsDialogService()
    {
        var services = new ServiceCollection();
        App.ConfigureServices(services, new JsonConfigurationService());
        using var provider = services.BuildServiceProvider();

        Assert.IsType<DiagnosticsDialogService>(provider.GetRequiredService<IDiagnosticsDialogService>());
    }

    private static Type[] ConstructorParameterTypes(Type type)
    {
        var constructor = Assert.Single(type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
        return constructor.GetParameters().Select(p => p.ParameterType).ToArray();
    }
}
