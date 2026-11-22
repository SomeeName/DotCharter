using DotCharter.Services.Audio.StemSeparation;
using DotCharter.Services.Audio.StemSeparation.Cache;
using DotCharter.Tone.Diagnostics;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Shell;

namespace DotCharter.Tests.UI.ViewModels.Shell;

public sealed class StemCachedRestoreCancellationTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dc-cache-cancel-").FullName;

    public void Dispose() => BestEffortDelete.Directory(_dir);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancel_during_decode_or_queued_publish_never_registers_cached_stems(bool cancelAtPublish)
    {
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var ui = new CancelBeforeInvokeDispatcher();
        var encoder = new CopyEncoder(() =>
        {
            if (cancelAtPublish) ui.BeforeNextInvoke = caller.Cancel;
            else caller.Cancel();
        });
        var audio = Path.Combine(_dir, "song.wav");
        var voice = Path.Combine(_dir, "vocals.wav");
        File.WriteAllBytes(audio, [1, 2, 3]);
        File.WriteAllBytes(voice, [4, 5, 6]);
        var store = new StemCacheStore(encoder, rootOverride: Path.Combine(_dir, "cache"));
        var identity = StemCacheIdentity.ForAudioFile(audio);
        var signature = StemAudioSignature.ComputeFile(audio);
        await store.SaveAsync(StemCacheKey.Derive(identity, signature), identity, signature,
            [new StemTrack { Name = "vocals", Path = voice }], "model", "", caller.Token);
        var project = new ChartProject { AudioFileName = audio };
        var state = new ProjectState
        {
            ActiveProject = project,
            CurrentProjectPath = Path.Combine(_dir, "chart.dcproj"),
            CurrentAudioFileName = audio
        };
        using var mixer = new StemMixerViewModel(new ProjectState(), new FakeAudioPlaybackService(),
            FakeLocalization.KeysOnly(), ui: ui);
        var separator = new UnexpectedSeparator();
        var vm = new StemSeparationViewModel(state, null!, mixer, null!, ui, new FakeAppLogger(),
            FakeLocalization.KeysOnly(), separator, stemCacheCoordinator: new StemCacheCoordinator(store));
        var applied = 0;
        vm.StemsApplied += () => applied++;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => vm.SeparateAsync(null, caller.Token));

        Assert.True(encoder.DecodeToken.CanBeCanceled);
        Assert.True(encoder.DecodeToken.IsCancellationRequested);
        Assert.Empty(project.Stems);
        Assert.Equal(0, applied);
        Assert.Equal(0, separator.Calls);
        Assert.Null(vm.Running);
    }

    private sealed class CopyEncoder(Action afterDecode) : IStemEncoder
    {
        public StemCodec PreferredCodec => StemCodec.Opus;
        public bool IsExternalEncoderAvailable => true;
        public CancellationToken DecodeToken { get; private set; }

        public Task<string> EncodeAsync(string wavPath, string destPathWithoutExtension, StemCodec target,
            CancellationToken ct = default)
        {
            var destination = destPathWithoutExtension + "." + target.FileExtension();
            File.Copy(wavPath, destination, overwrite: true);
            return Task.FromResult(destination);
        }

        public Task<string> DecodeToWavAsync(string encodedPath, StemCodec codec, string destWavPath,
            CancellationToken ct = default)
        {
            DecodeToken = ct;
            File.Copy(encodedPath, destWavPath, overwrite: true);
            afterDecode();
            return Task.FromResult(destWavPath);
        }
    }

    private sealed class UnexpectedSeparator : IStemSeparationService
    {
        public int Calls { get; private set; }
        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<string> DescribeBackendAsync(CancellationToken ct = default) => Task.FromResult("unused");

        public Task<StemSeparationResult> SeparateAsync(string inputAudioPath, string outputDirectory,
            IProgress<double>? progress = null, CancellationToken ct = default)
        {
            Calls++;
            throw new InvalidOperationException("A cached restore must not separate again.");
        }
    }

    private sealed class CancelBeforeInvokeDispatcher : IUiDispatcher
    {
        public Action? BeforeNextInvoke { get; set; }
        public bool CheckAccess() => true;
        public void Post(Action action) => action();
        public void PostNormal(Action action) => action();

        public Task InvokeAsync(Action action)
        {
            BeforeInvoke();
            action();
            return Task.CompletedTask;
        }

        public Task<T> InvokeAsync<T>(Func<T> func)
        {
            BeforeInvoke();
            return Task.FromResult(func());
        }

        public Task<T> InvokeAsync<T>(Func<Task<T>> func)
        {
            BeforeInvoke();
            return func();
        }

        private void BeforeInvoke()
        {
            var before = BeforeNextInvoke;
            BeforeNextInvoke = null;
            before?.Invoke();
        }
    }
}
