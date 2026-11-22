using System.Reflection;
using DotCharter.Tone.Audio;
using DotCharter.Tone.Audio.Engine;
using Ownership = DotCharter.Tone.Audio.Engine.MediaSourceOwnership;

namespace DotCharter.Tests.Services.Audio;

public sealed class FreeMasterMediaOwnershipTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void MasterReset_SettlesTheCanonicalLoadedIdentityAtTheCurrentRequestGeneration()
    {
        using var service = LoadedService();

        service.FreeMasterAudio();

        AssertSettled(service, 8);
    }

    [Fact]
    public async Task CancelledTemporaryLoadAfterMasterReset_CannotResurrectThePreviousLoadedIdentity()
    {
        using var service = LoadedService();
        service.FreeMasterAudio();
        var arguments = new object?[] { "take.wav", true, null };
        var request = Assert.IsType<MediaLoadRequest>(typeof(JuceAudioPlaybackService).GetMethod(
            "RequestMediaLoad", PrivateInstance, null,
            [typeof(string), typeof(bool), typeof(MediaLoadRequest).MakeByRefType()], null)!.Invoke(service, arguments));
        var previous = Assert.IsType<MediaLoadRequest>(arguments[2]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MediaSourceSwap.Queue(
            () => throw new InvalidOperationException("A cancelled decode must not start."), cancellation.Token,
            Field<object>(service, "_gate"),
            (@event, pending, preceding) => Assert.IsType<Ownership.Action>(typeof(JuceAudioPlaybackService)
                .GetMethod("DecideMedia", PrivateInstance)!.Invoke(service, [@event, pending, preceding])),
            request, previous));

        AssertSettled(service, 8);
        Assert.Null(Field<Ownership.State>(service, "_mediaOwnership").TemporaryOwner);
    }

    private static JuceAudioPlaybackService LoadedService()
    {
        // Constructor leaves the native engine absent; only logical ownership is seeded.
        var service = new JuceAudioPlaybackService(FakeLocalization.KeysOnly());
        var request = new MediaLoadRequest("song.wav", 7);
        SetField(service, "_mediaLoadGeneration", 7L);
        SetField(service, "_requestedMediaLoad", request);
        SetField(service, "_loadedMediaLoad", request);
        SetField(service, "_mediaOwnership", new Ownership.State(request, request));
        SetField(service, "_loadedPath", request.Identity!);
        return service;
    }

    private static void AssertSettled(JuceAudioPlaybackService service, long generation)
    {
        var state = Field<Ownership.State>(service, "_mediaOwnership");
        Assert.Null(state.Requested.Identity);
        Assert.Equal(generation, state.Requested.Generation);
        Assert.Equal(state.Requested, state.Loaded);
        Assert.Equal(state.Loaded, Field<MediaLoadRequest>(service, "_loadedMediaLoad"));
        Assert.Equal(generation, service.CurrentMediaGeneration);
        Assert.Equal("", service.CurrentAudioPath);
    }

    private static T Field<T>(JuceAudioPlaybackService service, string name) =>
        Assert.IsType<T>(typeof(JuceAudioPlaybackService).GetField(name, PrivateInstance)!.GetValue(service));

    private static void SetField(JuceAudioPlaybackService service, string name, object value) =>
        typeof(JuceAudioPlaybackService).GetField(name, PrivateInstance)!.SetValue(service, value);
}
