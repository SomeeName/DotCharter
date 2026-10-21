namespace DotCharter.Tests.Perf;

/// <summary>Deterministic pins for <see cref="RenderQuiescence" /> (#55): no headless session involved.</summary>
public sealed class RenderQuiescenceTests
{
    [Fact]
    public void AlreadyCurrentScene_costsNoTicks()
    {
        var ticks = 0;
        Assert.Equal(0, RenderQuiescence.WaitUntil(() => true, () => ticks++, () => "x"));
        Assert.Equal(0, ticks);
    }

    [Fact]
    public void LaggingScene_isTickedUntilItCatchesUp()
    {
        var frame = 0;
        var used = RenderQuiescence.WaitUntil(() => frame >= 3, () => frame++, () => "x");
        Assert.Equal(3, used);
    }

    [Fact]
    public void SceneThatNeverCatchesUp_failsLoudlyAfterTheBound()
    {
        var ticks = 0;
        var ex = Assert.Throws<InvalidOperationException>(() =>
            RenderQuiescence.WaitUntil(() => false, () => ticks++, () => "hit Border instead of Slider", maxTicks: 4));
        Assert.Equal(4, ticks);
        Assert.Contains("did not catch up with layout after 4 render ticks", ex.Message);
        Assert.Contains("hit Border instead of Slider", ex.Message);
    }

    [Fact]
    public void SceneThatCatchesUpOnTheLastTick_isAccepted()
    {
        var frame = 0;
        Assert.Equal(4, RenderQuiescence.WaitUntil(() => frame >= 4, () => frame++, () => "x", maxTicks: 4));
    }
}
