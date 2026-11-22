namespace DotCharter.Tone.Audio.Engine;

/// <summary>Logical transport authority, independent of native-source lifetime and decode I/O.</summary>
internal static class MediaSourceOwnership
{
    internal sealed record State(MediaLoadRequest Requested, MediaLoadRequest Loaded,
        MediaLoadRequest? TemporaryOwner = null, bool RestorationRegistered = false);

    internal enum Event
    {
        ExplicitLoadQueued, TakeQueued, WorkerStarts, DecodeOk, DecodeFail, Swap,
        StopHandBack, EngineTeardown, EngineReplay, NewerRequest
    }

    internal enum Action
    {
        None, Queue, DecodeReplacing, DecodePreserving, ReadyToSwap, ReportFailure,
        PublishReplacing, PublishPreserving, QueueRestoration, Release, Replay
    }

    internal sealed record Decision(State Next, Action Action);

    public static bool Owns(State state, string path, long generation, string? loadedPath = null) =>
        state.Requested.Generation == generation && state.Loaded.Generation == generation &&
        (Matches(state.Loaded, path, generation) ||
            (state.Loaded.Identity is not null && string.Equals(path, loadedPath, StringComparison.OrdinalIgnoreCase)));

    private static bool Matches(MediaLoadRequest request, string path, long generation) =>
        request.Generation == generation && string.Equals(request.Identity, path, StringComparison.OrdinalIgnoreCase);

    public static Decision Decide(State state, Event @event, MediaLoadRequest request, MediaLoadRequest? previous = null,
        string? loadedPath = null)
    {
        var temporary = previous is not null;
        var eligible = IsEligible(state, request, previous);
        switch (@event)
        {
            case Event.ExplicitLoadQueued:
            case Event.NewerRequest:
                return new(state with { Requested = request, TemporaryOwner = null, RestorationRegistered = false }, Action.Queue);
            case Event.TakeQueued:
                return new(state with { TemporaryOwner = request }, Action.Queue);
            case Event.WorkerStarts when eligible:
                return new(temporary ? state : state with { Loaded = new(null, request.Generation) },
                    temporary ? Action.DecodePreserving : Action.DecodeReplacing);
            case Event.DecodeOk when eligible:
                return new(state, Action.ReadyToSwap);
            case Event.DecodeFail:
                return new(SettleFailure(state, request, previous), Action.ReportFailure);
            case Event.Swap when eligible:
                return new(state with
                {
                    Requested = request,
                    Loaded = request,
                    RestorationRegistered = false
                }, temporary ? Action.PublishPreserving : Action.PublishReplacing);
            case Event.StopHandBack when CanHandBack(state, previous, loadedPath):
                return RegisterRestoration(state, request);
            case Event.EngineTeardown:
                return new(state.Requested.Identity is null ? state with { Loaded = state.Requested } : state, Action.Release);
            case Event.EngineReplay when CanReplay(state, request):
                return new(state, Action.Replay);
            default:
                return new(state, Action.None);
        }
    }

    private static bool IsEligible(State state, MediaLoadRequest request, MediaLoadRequest? previous) => previous is not null
        ? state.Requested.Generation == previous.Generation && ReferenceEquals(state.TemporaryOwner, request)
        : ReferenceEquals(state.Requested, request);

    private static State SettleFailure(State state, MediaLoadRequest request, MediaLoadRequest? previous)
    {
        if (previous is not null && ReferenceEquals(state.TemporaryOwner, request))
            return state with { TemporaryOwner = null };
        if (previous is not null || !IsEligible(state, request, previous)) return state;
        // Failure settles this request; it is not another user selection.
        var empty = new MediaLoadRequest(null, request.Generation);
        return state with { Requested = empty, Loaded = empty };
    }

    private static bool CanHandBack(State state, MediaLoadRequest? previous, string? loadedPath) =>
        previous is not null && Owns(state, previous.Identity!, previous.Generation, loadedPath) &&
        (state.TemporaryOwner is null || state.TemporaryOwner.Generation == previous.Generation);

    private static Decision RegisterRestoration(State state, MediaLoadRequest request) => new(state with
    {
        Requested = request,
        Loaded = request.Identity is null ? request : state.Loaded,
        TemporaryOwner = null,
        RestorationRegistered = request.Identity is not null
    }, request.Identity is null ? Action.Release : Action.QueueRestoration);

    private static bool CanReplay(State state, MediaLoadRequest request) =>
        request.Identity is not null && state.Requested == request && state.Loaded == request;
}
