using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Tests.Presentation.Dialogs;
using DotCharter.Tone.Diagnostics;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.Services.Diagnostics;
using DotCharter.Services.Session;
using DotCharter.Tone.Audio.Abstractions;

namespace DotCharter.Tests.Services.Session;

public sealed partial class ProjectSessionAttachAudioTests
{
    private sealed class AudioPause : IDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task WaitAsync()
        {
            Entered.TrySetResult();
            await _released.Task;
        }

        public void Wait(CancellationToken token)
        {
            Entered.TrySetResult();
            _release.Wait(token);
        }

        public void Open()
        {
            _release.Set();
            _released.TrySetResult();
        }

        public void Dispose()
        {
            Open();
            _release.Dispose();
        }
    }

    private sealed class ConfirmationPrompt : IMessagePrompt
    {
        private int _calls;
        public Task<bool> ShowAsync(string title, string message, MessageDialogKind kind = MessageDialogKind.Info,
            string? details = null, bool confirmation = false) => Task.FromResult(Interlocked.Increment(ref _calls) == 1);
    }

    [Theory]
    [InlineData("open")]
    [InlineData("close")]
    [InlineData("replace")]
    public async Task A_committed_download_stays_successful_after_measurement_loses_ownership(string next)
    {
        var (h, folder) = CreateAudiolessProject();
        var download = Path.Combine(folder, "download.mp3");
        File.WriteAllText(download, "downloaded audio");
        using var pause = new AudioPause();
        var token = TestContext.Current.CancellationToken;
        var measurement = 0;
        h.Logger.OnEntry = entry =>
        {
            if (entry.Message.Contains("Could not measure the volume", StringComparison.Ordinal) &&
                Interlocked.Increment(ref measurement) == 1)
            {
                pause.Wait(token);
            }
        };
        var attachment = h.Session.AttachAudioAsync(download);
        await pause.Entered.Task.WaitAsync(token);
        try
        {
            Assert.True(await h.Session.SaveProjectAsync(SaveInitiator.User));
            Assert.Equal("download.mp3", h.Documents.SavedAudioReference);
            if (next == "replace")
            {
                Assert.True(await h.Session.AttachAudioAsync(CreateSourceAudio("later.ogg")));
            }
            else
            {
                h.Session.FreeAudio();
                h.State.ActiveProject = next == "close" ? null : new ChartProject();
                h.State.CurrentAudioFileName = "";
            }
        }
        finally
        {
            pause.Open();
        }

        // The launcher deletes its download only on false, even if a save already names it.
        Assert.True(await attachment);
        Assert.True(File.Exists(download));
    }

    [Fact]
    public async Task A_declined_second_confirmation_does_not_abandon_an_inflight_decode()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_tempDir, "confirmation")).FullName;
        var previous = Path.Combine(folder, "original.ogg");
        File.WriteAllText(previous, "original");
        var h = new Harness(new ChartProject { AudioFileName = "original.ogg" },
            Path.Combine(folder, "project.rscp"), new ConfirmationPrompt());
        using var pause = new AudioPause();
        h.Audio.MemoryLoadPass = pause.WaitAsync;
        var first = h.Session.AttachAudioAsync(CreateSourceAudio("first.ogg"));
        await pause.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            Assert.False(await h.Session.AttachAudioAsync(CreateSourceAudio("declined.ogg")));
        }
        finally
        {
            pause.Open();
        }

        Assert.True(await first);
        Assert.Equal("song.ogg", h.State.ActiveProject!.AudioFileName);
        Assert.Equal(Path.Combine(folder, "song.ogg"), h.Audio.CompletedLoadPath);
        Assert.Equal(File.ReadAllBytes(CreateSourceAudio("first.ogg")), File.ReadAllBytes(h.Audio.CompletedLoadPath!));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Local_and_unsaved_corrupt_attachments_preserve_reference_and_playback(bool unsaved, bool waveformRefused)
    {
        var (h, folder) = CreateAudiolessProject();
        var previous = Path.Combine(folder, "original.ogg");
        var corrupt = Path.Combine(folder, "corrupt.ogg");
        File.WriteAllText(previous, "original");
        File.WriteAllText(corrupt, "corrupt");
        var reference = unsaved ? previous : "original.ogg";
        h.State.ActiveProject!.AudioFileName = reference;
        h.State.CurrentAudioFileName = reference;
        if (unsaved) h.State.CurrentProjectPath = "";
        await h.Session.LoadAudioFile(previous, false);
        var validWaveform = h.Audio.WaveformResult;
        h.Audio.WaveformForPath = path => path == corrupt && waveformRefused ? WaveformSamples.Empty : validWaveform;
        h.Audio.FileLoadPass = path =>
        {
            h.Audio.TotalAudioLengthMs = path == corrupt ? 0 : 1000;
            return Task.CompletedTask;
        };
        h.Audio.DuringMemoryLoad = () => h.Audio.TotalAudioLengthMs = 0;

        Assert.False(await h.Session.AttachAudioAsync(corrupt));

        Assert.Equal(reference, h.State.ActiveProject.AudioFileName);
        Assert.Equal(reference, h.State.CurrentAudioFileName);
        Assert.Equal(previous, h.Audio.CompletedLoadPath);
        Assert.Equal("original", File.ReadAllText(previous));
        Assert.False(h.Session.IsProjectDirty);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Closing_during_a_decode_leaves_the_engine_empty(bool silent)
    {
        var (h, _) = CreateAudiolessProject();
        using var pause = new AudioPause();
        h.Audio.MemoryLoadPass = pause.WaitAsync;
        h.Audio.FileLoadPass = _ => pause.WaitAsync();
        var loading = silent
            ? h.Session.LoadSilentTransportAsync(1000)
            : h.Session.AttachAudioAsync(CreateSourceAudio("closing.ogg"));
        await pause.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        h.State.ActiveProject = null;
        h.Session.FreeAudio();
        pause.Open();
        await loading;

        Assert.Null(h.Audio.CompletedLoadPath);
        Assert.Equal(0, h.State.TotalAudioLengthMs);
        Assert.False(h.State.WaveformData?.HasData ?? false);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Save_as_during_attachment_decode_does_not_commit_a_reference_to_the_previous_folder(bool rebaseDuringRestore)
    {
        var (h, folder) = CreateAudiolessProject();
        File.WriteAllText(Path.Combine(folder, "original.ogg"), "original");
        h.State.ActiveProject!.AudioFileName = "original.ogg";
        h.State.CurrentAudioFileName = "original.ogg";
        using var pause = new AudioPause();
        h.Audio.MemoryLoadPass = pause.WaitAsync;
        var attachment = h.Session.AttachAudioAsync(CreateSourceAudio("move.ogg"));
        await pause.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        var target = Directory.CreateDirectory(Path.Combine(_tempDir, "saved-as")).FullName;
        File.Copy(Path.Combine(folder, "original.ogg"), Path.Combine(target, "original.ogg"));
        h.State.CurrentProjectPath = Path.Combine(target, "project.rscp");
        var expectedReference = "original.ogg";
        using var restoring = new AudioPause();
        if (rebaseDuringRestore) h.Audio.FileLoadPass = _ => restoring.WaitAsync();
        try
        {
            pause.Open();
            if (rebaseDuringRestore)
            {
                var ready = await Task.WhenAny(attachment, restoring.Entered.Task).WaitAsync(TestContext.Current.CancellationToken);
                if (ReferenceEquals(ready, attachment))
                {
                    Assert.False(await attachment);
                    Assert.Equal(Path.Combine(target, "original.ogg"), h.Audio.CompletedLoadPath);
                }
                else
                {
                    // Save As rebases the same media to an absolute reference before moving the project path.
                    expectedReference = Path.Combine(target, "original.ogg");
                    h.State.ActiveProject!.AudioFileName = expectedReference;
                    h.State.CurrentAudioFileName = expectedReference;
                    var moved = Directory.CreateDirectory(Path.Combine(_tempDir, "saved-again")).FullName;
                    h.State.CurrentProjectPath = Path.Combine(moved, "project.rscp");
                }
            }
        }
        finally
        {
            pause.Open();
            restoring.Open();
        }

        Assert.False(await attachment);
        Assert.Equal(expectedReference, h.State.CurrentAudioFileName);
        Assert.Equal(Path.Combine(target, "original.ogg"), h.Audio.CompletedLoadPath);
        Assert.False(File.Exists(Path.Combine(folder, "song.ogg")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Opening_unavailable_audio_never_keeps_the_previous_projects_playback(bool noAudio)
    {
        var (h, folder) = CreateAudiolessProject();
        var original = Path.Combine(folder, "original.ogg");
        File.WriteAllText(original, "original");
        h.State.ActiveProject!.AudioFileName = "original.ogg";
        h.State.CurrentAudioFileName = "original.ogg";
        await h.Session.LoadAudioFile(original, false);
        Assert.Equal(original, h.Audio.CompletedLoadPath);
        Assert.True(h.State.WaveformData!.HasData);
        var opened = Path.Combine(folder, "unavailable.rscp");
        File.WriteAllText(opened, "project");
        var next = new ChartProject { AudioFileName = noAudio ? "" : "corrupt.ogg" };
        if (!noAudio)
        {
            File.WriteAllText(Path.Combine(folder, "corrupt.ogg"), "corrupt");
            h.Audio.LoadAudioFailure = new InvalidDataException("decode refused");
        }
        h.Documents.Load = _ => next;

        await h.Session.LoadFile(opened);

        Assert.Same(next, h.State.ActiveProject);
        Assert.Null(h.Audio.CompletedLoadPath);
        Assert.Equal(0, h.State.TotalAudioLengthMs);
        Assert.False(h.State.WaveformData?.HasData ?? false);
    }

    [Fact]
    public async Task An_older_project_parse_cannot_replace_a_newer_open()
    {
        var (h, _) = CreateAudiolessProject();
        var first = Path.Combine(_tempDir, "first.rscp");
        var second = Path.Combine(_tempDir, "second.rscp");
        File.WriteAllText(first, "first");
        File.WriteAllText(second, "second");
        var newest = new ChartProject();
        using var pause = new AudioPause();
        var token = TestContext.Current.CancellationToken;
        h.Documents.Load = path =>
        {
            if (path != first) return newest;
            pause.Wait(token);
            return new ChartProject();
        };
        var opening = h.Session.LoadFile(first);
        await pause.Entered.Task.WaitAsync(token);
        try
        {
            await h.Session.LoadFile(second);
        }
        finally
        {
            pause.Open();
        }
        await opening;

        Assert.Same(newest, h.State.ActiveProject);
        Assert.Equal(second, h.State.CurrentProjectPath);
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Saving_in_the_replacement_prompt_keeps_the_requested_operation(bool checkpoint, bool untitled)
    {
        var (h, folder) = CreateAudiolessProject();
        var original = h.State.ActiveProject!;
        var saved = Path.Combine(folder, "project.dcproj");
        if (untitled) h.State.CurrentProjectPath = "";
        h.Storage.NextSaveFile = saved;
        h.Session.MarkProjectDirty();
        var saves = 0;
        h.Session.ConfirmDiscardAsync = async () =>
        {
            Assert.Same(original, h.State.ActiveProject);
            Assert.True(await h.Session.SaveProjectAsync(SaveInitiator.User));
            Assert.Equal(saved, h.State.CurrentProjectPath);
            saves++;
            return true;
        };
        var next = new ChartProject();
        if (checkpoint)
        {
            var store = new CheckpointsViewModelTests.FakeCheckpointService { RestoredProject = next };
            var edit = new ChartEditDialogServices(h.Editor, h.History, h.State, h.Config,
                FakeLocalization.English(), h.Logger);
            var vm = new CheckpointsViewModel(store, h.Documents, edit, h.Transport, h.Session,
                new ScriptedPrompt());
            await ((IAsyncRelayCommand)vm.RestoreCommand).ExecuteAsync(vm.Checkpoints[0]);
            Assert.Equal([saved], store.RestoredIdentities);
        }
        else
        {
            var opened = Path.Combine(folder, "next.rscp");
            File.WriteAllText(opened, "next");
            h.Documents.Load = _ => next;
            await h.Session.LoadFile(opened);
            Assert.Equal(opened, h.State.CurrentProjectPath);
        }
        Assert.Equal(1, saves);
        Assert.True(File.Exists(saved));
        Assert.Same(next, h.State.ActiveProject);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_obsolete_master_cannot_clear_the_new_projects_completed_mixer_rebuild(bool attachment)
    {
        var (h, folder) = CreateAudiolessProject();
        var oldAudio = CreateSourceAudio("old.ogg");
        var nextAudio = CreateSourceAudio("next.ogg");
        var opened = Path.Combine(folder, "next.rscp");
        File.WriteAllText(opened, "next");
        var stemFile = Path.Combine(folder, "bass.wav");
        File.WriteAllBytes(stemFile, [0]);
        var next = new ChartProject { AudioFileName = nextAudio };
        next.Stems.Add(new Stem { Name = "bass", Path = stemFile });
        h.Documents.Load = _ => next;
        h.Session.ConfirmDiscardAsync = () => Task.FromResult(true);
        using var pause = new AudioPause();
        var waveform = 0;
        if (attachment) h.Audio.MemoryLoadPass = pause.WaitAsync;
        h.Audio.WaveformPass = () => !attachment && Interlocked.Increment(ref waveform) == 1
            ? pause.WaitAsync() : Task.CompletedTask;
        var oldLoad = attachment ? h.Session.AttachAudioAsync(oldAudio) : h.Session.LoadAudioFile(oldAudio, false);
        var token = TestContext.Current.CancellationToken;
        await pause.Entered.Task.WaitAsync(token);
        var rebuilt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ui = new MixerCompletionDispatcher();
        var mixer = new StemMixerViewModel(h.State, h.Audio, FakeLocalization.KeysOnly(), ui: ui);
        ui.AfterDispatch = () =>
        {
            if (ReferenceEquals(next, h.State.ActiveProject) && !mixer.IsRebuilding && mixer.HasStems)
                rebuilt.TrySetResult();
        };
        var opening = h.Session.LoadFile(opened);
        try
        {
            await rebuilt.Task.WaitAsync(token);
            Assert.Equal(["bass"], h.Audio.LoadedStemNames);
            Assert.Contains(ui.Read(() => mixer.Tracks.ToArray()), track => track.Name == "bass");
        }
        finally
        {
            pause.Open();
        }
        await Task.WhenAll(oldLoad, opening);
        Assert.Equal(nextAudio, h.Audio.CompletedLoadPath);
        Assert.Equal(["bass"], h.Audio.LoadedStemNames);
        Assert.Single(h.Audio.StemLoadCalls);
        await h.Session.ReloadProjectAudioAsync();
        Assert.Equal(["bass"], h.Audio.LoadedStemNames);
        Assert.Single(h.Audio.StemLoadCalls);
        mixer.Dispose();
    }

    private sealed class MixerCompletionDispatcher : IUiDispatcher
    {
        private readonly GatedInlineDispatcher _inner = new();
        public Action? AfterDispatch { get; set; }
        public bool CheckAccess() => true;
        public T Read<T>(Func<T> read) => _inner.Read(read);
        public void Post(Action action) => Run(action);
        public void PostNormal(Action action) => Run(action);
        public Task InvokeAsync(Action action)
        {
            Run(action);
            return Task.CompletedTask;
        }
        public Task<T> InvokeAsync<T>(Func<T> func) => Task.FromResult(Read(() =>
        {
            var value = func();
            AfterDispatch?.Invoke();
            return value;
        }));
        public Task<T> InvokeAsync<T>(Func<Task<T>> func) => func();
        private void Run(Action action) => Read(() =>
        {
            action();
            AfterDispatch?.Invoke();
            return true;
        });
    }

}
