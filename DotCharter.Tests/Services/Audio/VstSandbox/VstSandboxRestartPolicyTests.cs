using DotCharter.Tone.Audio.Vst.Sandbox;

namespace DotCharter.Tests.Services.Audio.VstSandbox;

public class VstSandboxRestartPolicyTests
{
    private static TimeSpan T(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void FirstCrashRestartsImmediately()
    {
        var policy = new VstSandboxRestartPolicy();
        Assert.Equal(TimeSpan.Zero, policy.OnCrash(T(10)));
    }

    [Fact]
    public void DelaysEscalatePerCrashInsideWindow()
    {
        var policy = new VstSandboxRestartPolicy();
        Assert.Equal(TimeSpan.Zero, policy.OnCrash(T(0)));
        Assert.Equal(TimeSpan.FromSeconds(1), policy.OnCrash(T(1)));
        Assert.Equal(TimeSpan.FromSeconds(2), policy.OnCrash(T(2)));
        Assert.Equal(TimeSpan.FromSeconds(5), policy.OnCrash(T(3)));
        Assert.Equal(TimeSpan.FromSeconds(10), policy.OnCrash(T(4)));
    }

    [Fact]
    public void ExceedingBudgetGivesUp()
    {
        var policy = new VstSandboxRestartPolicy(maxCrashesPerWindow: 3);
        Assert.NotNull(policy.OnCrash(T(0)));
        Assert.NotNull(policy.OnCrash(T(1)));
        Assert.NotNull(policy.OnCrash(T(2)));
        Assert.Null(policy.OnCrash(T(3)));
        Assert.True(policy.GaveUp);
        // Once given up, further crashes stay refused.
        Assert.Null(policy.OnCrash(T(4)));
    }

    [Fact]
    public void CrashesOutsideWindowAreForgotten()
    {
        var policy = new VstSandboxRestartPolicy(maxCrashesPerWindow: 2, window: T(60));
        Assert.NotNull(policy.OnCrash(T(0)));
        Assert.NotNull(policy.OnCrash(T(10)));
        // 100 s later both crashes have aged out — full budget again.
        Assert.Equal(TimeSpan.Zero, policy.OnCrash(T(110)));
        Assert.Equal(1, policy.RecentCrashCount(T(110)));
    }

    [Fact]
    public void HealthyStretchResetsHistoryAndDelay()
    {
        var policy = new VstSandboxRestartPolicy(healthyResetAfter: T(30));
        policy.OnCrash(T(0));
        policy.OnCrash(T(1));
        policy.OnHealthy(T(10)); // not long enough yet
        Assert.Equal(2, policy.RecentCrashCount(T(10)));
        policy.OnHealthy(T(40)); // 39 s since last crash — clears
        Assert.Equal(0, policy.RecentCrashCount(T(40)));
        Assert.Equal(TimeSpan.Zero, policy.OnCrash(T(41)));
    }

    [Fact]
    public void HealthyStretchUnGivesUp()
    {
        var policy = new VstSandboxRestartPolicy(maxCrashesPerWindow: 1, window: T(60), healthyResetAfter: T(30));
        Assert.NotNull(policy.OnCrash(T(0)));
        Assert.Null(policy.OnCrash(T(1)));
        Assert.True(policy.GaveUp);
        policy.OnHealthy(T(100));
        Assert.False(policy.GaveUp);
        Assert.NotNull(policy.OnCrash(T(101)));
    }

    [Fact]
    public void ResetClearsEverything()
    {
        var policy = new VstSandboxRestartPolicy(maxCrashesPerWindow: 1);
        policy.OnCrash(T(0));
        Assert.Null(policy.OnCrash(T(1)));
        policy.Reset();
        Assert.False(policy.GaveUp);
        Assert.Equal(0, policy.RecentCrashCount(T(1)));
        Assert.Equal(TimeSpan.Zero, policy.OnCrash(T(2)));
    }
}
