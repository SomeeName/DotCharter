using DotCharter.Tests.UI.ViewModels.Editor;

namespace DotCharter.Tests.Services.Session;

public sealed partial class ProjectSessionAttachAudioTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AttachmentClearingStems_NotifiesEvenWhenTheDecodeFails(bool fail)
    {
        var (h, _) = CreateAudiolessProject();
        var cleared = 0;
        h.State.AudioCleared += () => cleared++;
        if (fail) h.Audio.MemoryLoadFailure = new InvalidDataException("decode refused");

        Assert.Equal(!fail, await h.Session.AttachAudioAsync(CreateSourceAudio("notify.ogg")));

        Assert.Equal(1, cleared);
    }

    [Fact]
    public void FreeAudio_NotifiesTheMixerAfterClearingPlayback()
    {
        var (h, _) = CreateAudiolessProject();
        var cleared = 0;
        h.State.AudioCleared += () => cleared++;

        h.Session.FreeAudio();

        Assert.Equal(1, cleared);
    }

    /// <summary>Guard (found holding): attaching a master to a project whose mixer already settled
    /// its stems clears them through AudioCleared, and the mixer loads the same set again.</summary>
    [Fact]
    public async Task AttachingAMaster_GetsTheSettledStemsBack()
    {
        var (h, dir) = CreateAudiolessProject();
        AddStem(h, dir);
        using var mixer = await Mixer(h);
        Assert.Contains("drums", h.Audio.LoadedStemNames);

        Assert.True(await h.Session.AttachAudioAsync(CreateSourceAudio("attached.ogg")));
        await StemMixerSettle.Async(() => h.Audio.LoadedStemNames.Contains("drums"));

        Assert.Equal(2, h.Audio.StemLoadCalls.Count);
    }

    [Fact]
    public async Task ReloadingAnEmptyMaster_PreservesTheCurrentProjectsLoadedStems()
    {
        var (h, dir) = CreateAudiolessProject();
        AddStem(h, dir);
        using var mixer = await Mixer(h);
        Assert.Contains("drums", h.Audio.LoadedStemNames);

        await h.Session.ReloadProjectAudioAsync();

        Assert.Contains("drums", h.Audio.LoadedStemNames);
        Assert.Single(h.Audio.StemLoadCalls);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueuedMasterNotifications_BelongToThePublishedProject(bool clearing)
    {
        var ui = new QueuedAudioDispatcher();
        var h = new Harness(new ChartProject(), "", uiDispatcher: ui);
        var notifications = 0;
        if (clearing) h.State.AudioCleared += () => notifications++;
        else h.State.AudioLoaded += () => notifications++;
        if (clearing) h.Session.FreeAudio();
        else await h.Session.LoadAudioFile(CreateSourceAudio("published.ogg"), false);
        h.Editor.ApplyProject(new ChartProject(), resetHistory: true);

        ui.Drain();

        Assert.Equal(0, notifications);
    }

    [Fact]
    public async Task QueuedMasterWaveform_CannotAnnounceAfterTheSameProjectsAudioWasFreed()
    {
        var ui = new QueuedAudioDispatcher();
        var h = new Harness(new ChartProject(), "", uiDispatcher: ui);
        var loaded = 0;
        h.State.AudioLoaded += () => loaded++;
        await h.Session.LoadAudioFile(CreateSourceAudio("obsolete.ogg"), false);
        h.Session.FreeAudio();

        ui.Drain();

        Assert.Equal(0, loaded);
    }

    [Fact]
    public async Task ANewerMasterRequest_DoesNotHideTheSameProjectsStemClear()
    {
        var ui = new QueuedAudioDispatcher();
        var h = new Harness(new ChartProject(), "", uiDispatcher: ui);
        var cleared = 0;
        h.State.AudioCleared += () => cleared++;
        h.Session.FreeAudio();
        await h.Session.LoadAudioFile(CreateSourceAudio("newer.ogg"), false);

        ui.Drain();

        Assert.Equal(1, cleared);
    }

    private sealed class QueuedAudioDispatcher : DotCharter.Tone.Diagnostics.IUiDispatcher
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _posts = new();
        private readonly TestUiDispatcher _inline = new();
        public bool CheckAccess() => true;
        public void Post(Action action) => _posts.Enqueue(action);
        public void PostNormal(Action action) => Post(action);
        public Task InvokeAsync(Action action) => _inline.InvokeAsync(action);
        public Task<T> InvokeAsync<T>(Func<T> func) => _inline.InvokeAsync(func);
        public Task<T> InvokeAsync<T>(Func<Task<T>> func) => _inline.InvokeAsync(func);
        public IDisposable SchedulePeriodic(TimeSpan period, Action tick) => _inline.SchedulePeriodic(period, tick);
        public void Drain()
        {
            while (_posts.TryDequeue(out var action)) action();
        }
    }

}
