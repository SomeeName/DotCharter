using DotCharter.Services.Audio.LivePlay;

namespace DotCharter.Tests.Services.Audio.LivePlay;

/// <summary>
///     The worker → UI marshaling contract: at most one posted callback in
///     flight regardless of publish rate, payload-replace coalescing (an
///     unapplied payload is superseded, with the merge hook folding list state
///     forward), and re-arming after every drain. The post delegate is captured
///     instead of dispatched, so the tests drive the "UI thread" by hand.
/// </summary>
public class CoalescedUiChannelTests
{
    private sealed class Payload
    {
        public int Value;
        public List<string> Lines = [];
    }

    [Fact]
    public void Publishes_post_once_per_burst_and_apply_sees_only_the_merged_payload()
    {
        var posts = new List<Action>();
        var applied = new List<Payload>();
        var channel = new CoalescedUiChannel<Payload>(
            posts.Add,
            applied.Add,
            (old, incoming) =>
            {
                incoming.Lines.InsertRange(0, old.Lines);
                return incoming;
            });

        channel.Publish(new Payload { Value = 1, Lines = { "a" } });
        channel.Publish(new Payload { Value = 2, Lines = { "b" } });
        channel.Publish(new Payload { Value = 3, Lines = { "c" } });

        // A burst produces ONE post, no matter how many publishes landed.
        Assert.Single(posts);
        Assert.Empty(applied);

        posts[0]();

        // Intermediate scalar payloads were dropped (newest wins); list state
        // was folded forward by the merge hook, oldest first.
        var payload = Assert.Single(applied);
        Assert.Equal(3, payload.Value);
        Assert.Equal(new[] { "a", "b", "c" }, payload.Lines);
    }

    [Fact]
    public void Rearms_after_a_drain()
    {
        var posts = new List<Action>();
        var applied = new List<Payload>();
        var channel = new CoalescedUiChannel<Payload>(posts.Add, applied.Add);

        channel.Publish(new Payload { Value = 1 });
        posts[0]();
        channel.Publish(new Payload { Value = 2 });

        Assert.Equal(2, posts.Count);
        posts[1]();
        Assert.Equal(2, applied.Count);
        Assert.Equal(1, applied[0].Value);
        Assert.Equal(2, applied[1].Value);
    }

    [Fact]
    public void Without_a_merge_hook_the_newest_payload_replaces_outright()
    {
        var posts = new List<Action>();
        var applied = new List<Payload>();
        var channel = new CoalescedUiChannel<Payload>(posts.Add, applied.Add);

        channel.Publish(new Payload { Value = 1 });
        channel.Publish(new Payload { Value = 2 });

        Assert.Single(posts);
        posts[0]();
        Assert.Equal(2, Assert.Single(applied).Value);
    }

    [Fact]
    public void A_drain_with_nothing_pending_applies_nothing()
    {
        var posts = new List<Action>();
        var applied = new List<Payload>();
        var channel = new CoalescedUiChannel<Payload>(posts.Add, applied.Add);

        channel.Publish(new Payload { Value = 1 });
        posts[0]();
        posts[0](); // a duplicate drain (post scheduler quirk) must be harmless

        Assert.Single(applied);
    }
}
