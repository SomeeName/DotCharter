using DotCharter.Tone.Audio.Engine;
using Ownership = DotCharter.Tone.Audio.Engine.MediaSourceOwnership;

namespace DotCharter.Tests.Services.Audio;

/// <summary>Literal state/event decisions cover all four axes and request identity relationships.</summary>
public sealed class MediaSourceOwnershipTests
{
    private static readonly Dictionary<string, MediaLoadRequest?> Requests = new()
    {
        ["-"] = null, ["E"] = new(null, 0), ["A"] = new("song-a.wav", 1),
        ["B"] = new("song-b.wav", 2), ["T"] = new("take-1.wav", 3),
        ["U"] = new("take-2.wav", 4), ["R"] = new("song-a.wav", 5),
        ["N"] = new("song-new.wav", 6), ["eA"] = new(null, 1),
        ["eB"] = new(null, 2), ["eT"] = new(null, 3), ["eU"] = new(null, 4),
        ["eR"] = new(null, 5), ["eN"] = new(null, 6)
    };

    // R/L/T/restoration; worker request/previous; replay snapshot; owns published T.
    // The first sixteen rows cross every present/absent combination of the four axes.
    // The last fifteen distinguish owner/current/stale generations beyond presence alone.
    private static readonly Dictionary<string, string> States = new()
    {
        ["Empty"] = "E,E,-,0;A,-;E;0",
        ["EmptyRestoring"] = "E,E,-,1;A,-;E;0",
        ["EmptyTakeQueued"] = "E,E,T,0;T,A;E;0",
        ["EmptyTakeRestoring"] = "E,E,T,1;T,A;E;0",
        ["EmptyLoaded"] = "E,A,-,0;A,-;E;0",
        ["EmptyLoadedRestoring"] = "E,A,-,1;A,-;E;0",
        ["EmptyLoadedTake"] = "E,A,T,0;T,A;E;0",
        ["EmptyLoadedTakeRestoring"] = "E,A,T,1;T,A;E;0",
        ["SongDetached"] = "A,E,-,0;A,-;A;0",
        ["SongDetachedRestoring"] = "A,E,-,1;A,-;A;0",
        ["SongDetachedTake"] = "A,E,T,0;T,A;A;0",
        ["SongDetachedTakeRestoring"] = "A,E,T,1;T,A;A;0",
        ["SongLoaded"] = "A,A,-,0;A,-;A;0",
        ["SongLoadedRestoring"] = "A,A,-,1;A,-;A;0",
        ["SongLoadedTake"] = "A,A,T,0;T,A;A;0",
        ["SongLoadedTakeRestoring"] = "A,A,T,1;T,A;A;0",
        ["OwnedTake"] = "T,T,T,0;T,A;T;1",
        ["TornDownTake"] = "T,T,T,0;T,A;T;1",
        ["PendingSongAndTake"] = "B,A,T,0;T,B;B;0",
        ["FailedSongAndTake"] = "eB,eB,T,0;T,B;B;0",
        ["FailedSong"] = "eB,eB,-,0;B,-;B;0",
        ["PendingRestore"] = "R,T,-,1;R,-;R;0",
        ["NewTakeAfterOwned"] = "T,T,U,0;U,T;T;1",
        ["PendingSong"] = "B,A,-,0;B,-;B;0",
        ["EarlierSongBeforeTake"] = "B,A,T,0;B,-;B;0",
        ["EarlierTakeBeforeNewTake"] = "B,A,U,0;T,B;B;0",
        ["TornDownReplay"] = "T,T,T,0;T,-;T;1",
        ["NewerExplicitBeforeOldSong"] = "N,A,-,0;B,-;B;0",
        ["NewerExplicitBeforeOldTake"] = "N,T,-,0;T,B;T;0",
        ["TakeCandidateSettled"] = "T,T,-,0;U,T;T;1",
        ["FreeRequested"] = "E,T,-,0;T,A;T;0",
    };

    // ExplicitLoadQueued | TakeQueued | WorkerStarts | DecodeOk | DecodeFail | Swap |
    // StopHandBack | EngineTeardown | EngineReplay | NewerRequest.
    // Each cell literally states requested, loaded, take owner, restoration / action.
    private const string Table = """
        Empty | N,E,-,0/Queue | E,E,U,0/Queue | E,E,-,0/None | E,E,-,0/None | E,E,-,0/ReportFailure | E,E,-,0/None | E,E,-,0/None | E,E,-,0/Release | E,E,-,0/None | N,E,-,0/Queue
        EmptyRestoring | N,E,-,0/Queue | E,E,U,1/Queue | E,E,-,1/None | E,E,-,1/None | E,E,-,1/ReportFailure | E,E,-,1/None | E,E,-,1/None | E,E,-,1/Release | E,E,-,1/None | N,E,-,0/Queue
        EmptyTakeQueued | N,E,-,0/Queue | E,E,U,0/Queue | E,E,T,0/None | E,E,T,0/None | E,E,-,0/ReportFailure | E,E,T,0/None | E,E,T,0/None | E,E,T,0/Release | E,E,T,0/None | N,E,-,0/Queue
        EmptyTakeRestoring | N,E,-,0/Queue | E,E,U,1/Queue | E,E,T,1/None | E,E,T,1/None | E,E,-,1/ReportFailure | E,E,T,1/None | E,E,T,1/None | E,E,T,1/Release | E,E,T,1/None | N,E,-,0/Queue
        EmptyLoaded | N,A,-,0/Queue | E,A,U,0/Queue | E,A,-,0/None | E,A,-,0/None | E,A,-,0/ReportFailure | E,A,-,0/None | E,A,-,0/None | E,E,-,0/Release | E,A,-,0/None | N,A,-,0/Queue
        EmptyLoadedRestoring | N,A,-,0/Queue | E,A,U,1/Queue | E,A,-,1/None | E,A,-,1/None | E,A,-,1/ReportFailure | E,A,-,1/None | E,A,-,1/None | E,E,-,1/Release | E,A,-,1/None | N,A,-,0/Queue
        EmptyLoadedTake | N,A,-,0/Queue | E,A,U,0/Queue | E,A,T,0/None | E,A,T,0/None | E,A,-,0/ReportFailure | E,A,T,0/None | E,A,T,0/None | E,E,T,0/Release | E,A,T,0/None | N,A,-,0/Queue
        EmptyLoadedTakeRestoring | N,A,-,0/Queue | E,A,U,1/Queue | E,A,T,1/None | E,A,T,1/None | E,A,-,1/ReportFailure | E,A,T,1/None | E,A,T,1/None | E,E,T,1/Release | E,A,T,1/None | N,A,-,0/Queue
        SongDetached | N,E,-,0/Queue | A,E,U,0/Queue | A,eA,-,0/DecodeReplacing | A,E,-,0/ReadyToSwap | eA,eA,-,0/ReportFailure | A,A,-,0/PublishReplacing | A,E,-,0/None | A,E,-,0/Release | A,E,-,0/None | N,E,-,0/Queue
        SongDetachedRestoring | N,E,-,0/Queue | A,E,U,1/Queue | A,eA,-,1/DecodeReplacing | A,E,-,1/ReadyToSwap | eA,eA,-,1/ReportFailure | A,A,-,0/PublishReplacing | A,E,-,1/None | A,E,-,1/Release | A,E,-,1/None | N,E,-,0/Queue
        SongDetachedTake | N,E,-,0/Queue | A,E,U,0/Queue | A,E,T,0/DecodePreserving | A,E,T,0/ReadyToSwap | A,E,-,0/ReportFailure | T,T,T,0/PublishPreserving | A,E,T,0/None | A,E,T,0/Release | A,E,T,0/None | N,E,-,0/Queue
        SongDetachedTakeRestoring | N,E,-,0/Queue | A,E,U,1/Queue | A,E,T,1/DecodePreserving | A,E,T,1/ReadyToSwap | A,E,-,1/ReportFailure | T,T,T,0/PublishPreserving | A,E,T,1/None | A,E,T,1/Release | A,E,T,1/None | N,E,-,0/Queue
        SongLoaded | N,A,-,0/Queue | A,A,U,0/Queue | A,eA,-,0/DecodeReplacing | A,A,-,0/ReadyToSwap | eA,eA,-,0/ReportFailure | A,A,-,0/PublishReplacing | A,A,-,0/None | A,A,-,0/Release | A,A,-,0/Replay | N,A,-,0/Queue
        SongLoadedRestoring | N,A,-,0/Queue | A,A,U,1/Queue | A,eA,-,1/DecodeReplacing | A,A,-,1/ReadyToSwap | eA,eA,-,1/ReportFailure | A,A,-,0/PublishReplacing | A,A,-,1/None | A,A,-,1/Release | A,A,-,1/Replay | N,A,-,0/Queue
        SongLoadedTake | N,A,-,0/Queue | A,A,U,0/Queue | A,A,T,0/DecodePreserving | A,A,T,0/ReadyToSwap | A,A,-,0/ReportFailure | T,T,T,0/PublishPreserving | A,A,T,0/None | A,A,T,0/Release | A,A,T,0/Replay | N,A,-,0/Queue
        SongLoadedTakeRestoring | N,A,-,0/Queue | A,A,U,1/Queue | A,A,T,1/DecodePreserving | A,A,T,1/ReadyToSwap | A,A,-,1/ReportFailure | T,T,T,0/PublishPreserving | A,A,T,1/None | A,A,T,1/Release | A,A,T,1/Replay | N,A,-,0/Queue
        OwnedTake | N,T,-,0/Queue | T,T,U,0/Queue | T,T,T,0/None | T,T,T,0/None | T,T,-,0/ReportFailure | T,T,T,0/None | R,T,-,1/QueueRestoration | T,T,T,0/Release | T,T,T,0/Replay | N,T,-,0/Queue
        TornDownTake | N,T,-,0/Queue | T,T,U,0/Queue | T,T,T,0/None | T,T,T,0/None | T,T,-,0/ReportFailure | T,T,T,0/None | R,T,-,1/QueueRestoration | T,T,T,0/Release | T,T,T,0/Replay | N,T,-,0/Queue
        PendingSongAndTake | N,A,-,0/Queue | B,A,U,0/Queue | B,A,T,0/DecodePreserving | B,A,T,0/ReadyToSwap | B,A,-,0/ReportFailure | T,T,T,0/PublishPreserving | B,A,T,0/None | B,A,T,0/Release | B,A,T,0/None | N,A,-,0/Queue
        FailedSongAndTake | N,eB,-,0/Queue | eB,eB,U,0/Queue | eB,eB,T,0/DecodePreserving | eB,eB,T,0/ReadyToSwap | eB,eB,-,0/ReportFailure | T,T,T,0/PublishPreserving | eB,eB,T,0/None | eB,eB,T,0/Release | eB,eB,T,0/None | N,eB,-,0/Queue
        FailedSong | N,eB,-,0/Queue | eB,eB,U,0/Queue | eB,eB,-,0/None | eB,eB,-,0/None | eB,eB,-,0/ReportFailure | eB,eB,-,0/None | eB,eB,-,0/None | eB,eB,-,0/Release | eB,eB,-,0/None | N,eB,-,0/Queue
        PendingRestore | N,T,-,0/Queue | R,T,U,1/Queue | R,eR,-,1/DecodeReplacing | R,T,-,1/ReadyToSwap | eR,eR,-,1/ReportFailure | R,R,-,0/PublishReplacing | R,T,-,1/None | R,T,-,1/Release | R,T,-,1/None | N,T,-,0/Queue
        NewTakeAfterOwned | N,T,-,0/Queue | T,T,U,0/Queue | T,T,U,0/DecodePreserving | T,T,U,0/ReadyToSwap | T,T,-,0/ReportFailure | U,U,U,0/PublishPreserving | T,T,U,0/None | T,T,U,0/Release | T,T,U,0/Replay | N,T,-,0/Queue
        PendingSong | N,A,-,0/Queue | B,A,U,0/Queue | B,eB,-,0/DecodeReplacing | B,A,-,0/ReadyToSwap | eB,eB,-,0/ReportFailure | B,B,-,0/PublishReplacing | B,A,-,0/None | B,A,-,0/Release | B,A,-,0/None | N,A,-,0/Queue
        EarlierSongBeforeTake | N,A,-,0/Queue | B,A,U,0/Queue | B,eB,T,0/DecodeReplacing | B,A,T,0/ReadyToSwap | eB,eB,T,0/ReportFailure | B,B,T,0/PublishReplacing | B,A,T,0/None | B,A,T,0/Release | B,A,T,0/None | N,A,-,0/Queue
        EarlierTakeBeforeNewTake | N,A,-,0/Queue | B,A,U,0/Queue | B,A,U,0/None | B,A,U,0/None | B,A,U,0/ReportFailure | B,A,U,0/None | B,A,U,0/None | B,A,U,0/Release | B,A,U,0/None | N,A,-,0/Queue
        TornDownReplay | N,T,-,0/Queue | T,T,U,0/Queue | T,eT,T,0/DecodeReplacing | T,T,T,0/ReadyToSwap | eT,eT,T,0/ReportFailure | T,T,T,0/PublishReplacing | R,T,-,1/QueueRestoration | T,T,T,0/Release | T,T,T,0/Replay | N,T,-,0/Queue
        NewerExplicitBeforeOldSong | N,A,-,0/Queue | N,A,U,0/Queue | N,A,-,0/None | N,A,-,0/None | N,A,-,0/ReportFailure | N,A,-,0/None | N,A,-,0/None | N,A,-,0/Release | N,A,-,0/None | N,A,-,0/Queue
        NewerExplicitBeforeOldTake | N,T,-,0/Queue | N,T,U,0/Queue | N,T,-,0/None | N,T,-,0/None | N,T,-,0/ReportFailure | N,T,-,0/None | N,T,-,0/None | N,T,-,0/Release | N,T,-,0/None | N,T,-,0/Queue
        TakeCandidateSettled | N,T,-,0/Queue | T,T,U,0/Queue | T,T,-,0/None | T,T,-,0/None | T,T,-,0/ReportFailure | T,T,-,0/None | R,T,-,1/QueueRestoration | T,T,-,0/Release | T,T,-,0/Replay | N,T,-,0/Queue
        FreeRequested | N,T,-,0/Queue | E,T,U,0/Queue | E,T,-,0/None | E,T,-,0/None | E,T,-,0/ReportFailure | E,T,-,0/None | E,T,-,0/None | E,E,-,0/Release | E,T,-,0/None | N,T,-,0/Queue
        """;

    public static TheoryData<string, string, string> DecisionRows
    {
        get
        {
            var rows = new TheoryData<string, string, string>();
            var events = Enum.GetNames<Ownership.Event>();
            foreach (var line in Table.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var cells = line.Split('|', StringSplitOptions.TrimEntries);
                Assert.Equal(events.Length + 1, cells.Length);
                for (var i = 0; i < events.Length; i++) rows.Add(cells[0], events[i], cells[i + 1]);
            }
            return rows;
        }
    }

    [Theory]
    [MemberData(nameof(DecisionRows))]
    public void EveryStateAndEvent_HasTheWrittenNextStateAndAction(string name, string eventName, string expected)
    {
        var fixture = States[name].Split(';');
        var state = ParseState(fixture[0]);
        var worker = fixture[1].Split(',');
        var @event = Enum.Parse<Ownership.Event>(eventName);
        var request = @event switch
        {
            Ownership.Event.ExplicitLoadQueued or Ownership.Event.NewerRequest => Request("N"),
            Ownership.Event.TakeQueued => Request("U"),
            Ownership.Event.StopHandBack => Request("R"),
            Ownership.Event.EngineReplay => Request(fixture[2]),
            _ => Request(worker[0])
        };
        var previous = @event == Ownership.Event.StopHandBack ? Request("T") : Requests[worker[1]];
        var actual = Ownership.Decide(state, @event, request, previous);
        var wanted = expected.Split('/');

        Assert.Equal(ParseState(wanted[0]), actual.Next);
        Assert.Equal(Enum.Parse<Ownership.Action>(wanted[1]), actual.Action);
    }

    [Fact]
    public void TableCoversEveryAxisCombinationAndEveryEventExactlyOnce()
    {
        var rows = DecisionRows;
        Assert.Equal(310, rows.Count);
        Assert.Equal(31, States.Count);
        Assert.Equal(16, States.Values.Select(s => ParseState(s.Split(';')[0]))
            .Select(s => (s.Requested.Identity is not null, s.Loaded.Identity is not null,
                s.TemporaryOwner is not null, s.RestorationRegistered)).Distinct().Count());
        var lines = Table.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(States.Keys.Order(), lines.Select(l => l.Split('|')[0].Trim()).Order());
        Assert.All(lines, l => Assert.Equal(Enum.GetNames<Ownership.Event>().Length + 1, l.Split('|').Length));
    }

    [Fact]
    public void OwnershipDistinguishesQueuedTakesFromPublishedTakesAfterTeardown()
    {
        foreach (var fixture in States.Values)
        {
            var cells = fixture.Split(';');
            Assert.Equal(cells[3] == "1", Ownership.Owns(ParseState(cells[0]), "take-1.wav", 3));
        }
    }

    [Theory]
    [InlineData("OwnedTake", "E,E,-,0/Release")]
    [InlineData("TornDownTake", "E,E,-,0/Release")]
    [InlineData("SongLoadedTake", "A,A,T,0/None")]
    public void HandBackWithoutAnOriginalMaster_CompletesRestoration(string name, string expected)
    {
        var state = ParseState(States[name].Split(';')[0]);
        var actual = Ownership.Decide(state, Ownership.Event.StopHandBack, Request("E"), Request("T"));
        var wanted = expected.Split('/');
        Assert.Equal(ParseState(wanted[0]), actual.Next);
        Assert.Equal(Enum.Parse<Ownership.Action>(wanted[1]), actual.Action);
    }

    [Fact]
    public void LoadedMemoryDisplayAlias_CanOwnAndHandBackItsPublishedSource()
    {
        var state = ParseState("A,A,-,0");
        const string display = "<memory:wav:1024 bytes>";
        Assert.True(Ownership.Owns(state, display, 1, display));
        var actual = Ownership.Decide(state, Ownership.Event.StopHandBack, Request("R"), new(display, 1), display);
        Assert.Equal(ParseState("R,A,-,1"), actual.Next);
        Assert.Equal(Ownership.Action.QueueRestoration, actual.Action);
    }

    private static MediaLoadRequest Request(string name) => Requests[name]!;

    private static Ownership.State ParseState(string text)
    {
        var cells = text.Split(',');
        return new(Request(cells[0]), Request(cells[1]), Requests[cells[2]], cells[3] == "1");
    }
}
