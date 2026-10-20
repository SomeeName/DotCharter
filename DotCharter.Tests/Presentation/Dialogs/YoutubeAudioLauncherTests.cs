using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Services.Session;

namespace DotCharter.Tests.Presentation.Dialogs;

/// <summary>
///     File ▸ Audio from YouTube's launcher (#820): no project means a status line and no
///     dialog; a download the attach does not keep is deleted again; a cancel attaches nothing.
/// </summary>
public sealed class YoutubeAudioLauncherTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "dc-yt-launch-" + Guid.NewGuid().ToString("N"));

    private sealed class AnsweringDialogs(string? answer) : IDialogs
    {
        public int Opens { get; private set; }

        public bool HasOpenDialog => false;

        public event EventHandler<DialogChange>? Changed { add { } remove { } }

        public Task<TResult?> ShowAsync<TContract, TResult>(Action<TContract>? init = null)
            where TContract : class, IDialogScreen<TResult>
        {
            Assert.Equal(typeof(IYoutubeAudioScreen), typeof(TContract));
            Opens++;
            return Task.FromResult((TResult?)(object?)answer);
        }

        public Task<bool> MessageAsync(string title, string message, bool isConfirmation = false) => Task.FromResult(false);

        public bool CloseTop() => false;
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private static YoutubeAudioLauncher Launcher(IDialogs dialogs, ProjectState state, IProjectSessionService session) =>
        new(dialogs, state, session, FakeLocalization.English(), new FakeAppLogger());

    [Fact]
    public async Task Without_a_project_it_says_so_and_opens_nothing()
    {
        var state = new ProjectState();
        var dialogs = new AnsweringDialogs("x.mp3");
        var (session, calls) = RecordingProxy.Create<IProjectSessionService>();

        Assert.False(await Launcher(dialogs, state, session).OpenAsync());

        Assert.Equal(0, dialogs.Opens);
        Assert.Equal(FakeLocalization.English()["Status_YoutubeAudioNoProject"], state.MainDisplayMessage);
        Assert.DoesNotContain(calls.Calls, c => c.StartsWith("AttachAudioAsync", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_download_the_attach_does_not_keep_is_deleted()
    {
        Directory.CreateDirectory(_folder);
        var mp3 = Path.Combine(_folder, "youtube-test.mp3");
        await File.WriteAllBytesAsync(mp3, [1, 2, 3], TestContext.Current.CancellationToken);
        var state = new ProjectState { ActiveProject = new ChartProject() };
        // The proxy answers false: the user declined to replace the song audio.
        var (session, calls) = RecordingProxy.Create<IProjectSessionService>();

        Assert.False(await Launcher(new AnsweringDialogs(mp3), state, session).OpenAsync());

        Assert.Contains($"AttachAudioAsync({mp3})", calls.Calls);
        Assert.False(File.Exists(mp3), "a download nobody kept was left on disk");
    }

    [Fact]
    public async Task A_cancel_attaches_nothing()
    {
        var state = new ProjectState { ActiveProject = new ChartProject() };
        var dialogs = new AnsweringDialogs(null);
        var (session, calls) = RecordingProxy.Create<IProjectSessionService>();

        Assert.False(await Launcher(dialogs, state, session).OpenAsync());

        Assert.Equal(1, dialogs.Opens);
        Assert.Empty(calls.Calls);
    }
}
