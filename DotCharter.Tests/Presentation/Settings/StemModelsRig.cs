using DotCharter.Presentation.Dialogs;
using DotCharter.Presentation.Screens.Settings;
using DotCharter.Services.Audio.StemSeparation;
using DotCharter.Services.Audio.StemSeparation.Cache;
using DotCharter.Services.Audio.StemSeparation.Gpu;
using DotCharter.Tests.Presentation.Dialogs;
using DotCharter.Tone.Diagnostics;

namespace DotCharter.Tests.Presentation.Settings;

/// <summary>
///     Builds Settings ▸ Stem models the way the composition root does — every
///     block's services optional — so a case names only the one it is about.
/// </summary>
internal static class StemModelsRig
{
    internal static StemModelsSettingsViewModel Create(
        StemModelManager manager,
        IConfigurationService? config = null,
        IStemSeparationService? separation = null,
        LegacyGpuRuntimeFolder? legacyCudaRuntime = null,
        StemCacheStore? stemCache = null,
        IUiDispatcher? ui = null,
        IAppLogger? logger = null,
        IStemGpuEnvironment? gpu = null,
        IDialogs? dialogs = null)
    {
        config ??= new FakeConfigurationService();
        ui ??= new InlineUiDispatcher();
        var localization = FakeLocalization.English();
        return new StemModelsSettingsViewModel(
            manager, localization, ui,
            new StemModelsBlocks(
                new StemBackendViewModel(manager, config, localization, separation, logger, gpu),
                new GpuRuntimeViewModel(localization, ui, legacyCudaRuntime, logger),
                new StemCacheViewModel(localization, ui, stemCache, logger)),
            dialogs ?? new ScriptedDialogs(_ => throw new InvalidOperationException("no dialog expected")),
            logger);
    }

    /// <summary>Runs every dispatched action on the calling thread, so nothing in a case has to wait.</summary>
    private sealed class InlineUiDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;

        public void Post(Action action) => action();

        public void PostNormal(Action action) => action();

        public Task InvokeAsync(Action action)
        {
            action();
            return Task.CompletedTask;
        }

        public Task<T> InvokeAsync<T>(Func<T> func) => Task.FromResult(func());

        public Task<T> InvokeAsync<T>(Func<Task<T>> func) => func();
    }
}
