using DotCharter.Tests.Audio.Tonelab;
using DotCharter.Tone.Audio.Tonelab;
using DotCharter.UI;
using DotCharter.UI.ViewModels.Shell;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The headless application never probes a tonelab pipe on the machine. A real session discovers every
///     <c>tonelab-&lt;pid&gt;</c> pipe anywhere and connects to the newest, so two test processes side by side
///     (or a developer's running game) would be asked for status by a test run. The test host composes a
///     session that never touches a pipe (<see cref="App.ComposeLast" />); this opens a watcher under the name the
///     discovery would pick, starts what probes (the tone preview of the shell's tone rig) and asserts the
///     watcher saw nothing.
/// </summary>
[Collection("HeadlessUI")]
public sealed class HeadlessAppTonelabIsolationUiPerfSmokeTests
{
    [Fact]
    public async Task TheBootedApp_ProbesNoTonelabPipe_WhenTheTonePreviewStarts()
    {
        var ct = TestContext.Current.CancellationToken;
        // A pipe named for a LIVE process (this one): the session's exit watch lets go of a pid that is not running,
        // and discovery ranks a process that has a start time above the mocks of other tests, which have none.
        using var watcher = new TonelabGameMock(Environment.ProcessId);

        var sessionType = await HeadlessSessionFixture.Dispatch(async () =>
        {
            var services = App.Services ?? throw new InvalidOperationException("the headless app has no services");
            var session = services.GetRequiredService<ITonelabSession>();

            var preview = services.GetRequiredService<ToneRigViewModel>().TonePreview;
            Assert.NotNull(preview?.Rocksmith);
            // The explicit probe is the one the view model starts on first use, awaited (a hang guard, not a timing).
            await preview!.Rocksmith!.ProbeAsync().WaitAsync(TimeSpan.FromSeconds(60), ct);
            return session.GetType();
        }, ct);

        Assert.Equal(0, watcher.Connections);
        Assert.Empty(watcher.Received);
        Assert.Equal(typeof(FakeTonelabSession), sessionType);
    }
}
