namespace DotCharter.Tests.Services.Audio.VstSandbox;

public sealed class VstSandboxRejectedLoadTests
{
    [Fact]
    public async Task ARejectedOnlySlotStaysStoppedDuringAnUnrelatedSeatEdit()
    {
        using var f = new VstSandboxPerSeatFixture();
        var other = f.Entry(0);
        var rejected = f.Entry(1);
        f.ConfigureChild = child => child.RejectLoadSlots.Add(rejected.Id.ToString());
        await f.Chain(other, rejected);
        var child = f.Children[1];
        Assert.Null(f.Seat(1).Host);
        Assert.DoesNotContain(1, f.Bridge.EnabledSeats);
        Assert.False(child.IsRunning);
        Assert.Equal(1, child.Stops);
        var traffic = f.Traffic.Count;

        await f.Chain(f.Entry(0, other.Id, bypass: true), rejected);

        Assert.Equal(2, f.Children.Count);
        Assert.Single(child.Requests, request => request.Op == "load");
        Assert.DoesNotContain(f.Traffic.Skip(traffic), call => call.StartsWith("1:", StringComparison.Ordinal));
        Assert.Null(f.Seat(1).Host);
        Assert.DoesNotContain(1, f.Bridge.EnabledSeats);
    }

    [Fact]
    public async Task AMissingOnlySlotStaysStoppedDuringAnUnrelatedSeatEdit()
    {
        using var f = new VstSandboxPerSeatFixture();
        var other = f.Entry(0);
        var missing = f.Entry(1);
        missing.PluginPath += ".missing";
        await f.Chain(other, missing);
        Assert.Null(f.Seat(1).Host);
        Assert.DoesNotContain(1, f.Bridge.EnabledSeats);
        Assert.DoesNotContain(f.Children[1].Requests, request => request.Op == "load");

        await f.Chain(f.Entry(0, other.Id, bypass: true), missing);

        Assert.Equal(2, f.Children.Count);
        Assert.Null(f.Seat(1).Host);
        Assert.DoesNotContain(1, f.Bridge.EnabledSeats);
        Assert.Equal(1, f.Children[1].Stops);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangingTheFailedPathStartsAndLoadsTheSeat(bool missing)
    {
        using var f = new VstSandboxPerSeatFixture();
        var entry = f.Entry(1);
        if (missing) entry.PluginPath += ".missing";
        else f.ConfigureChild = child => child.RejectLoadSlots.Add(entry.Id.ToString());
        await f.Chain(entry);
        Assert.Null(f.Seat(1).Host);
        Assert.DoesNotContain(1, f.Bridge.EnabledSeats);
        await f.Chain(entry);
        Assert.Single(f.Children);
        f.ConfigureChild = null;
        var changed = f.Entry(1, entry.Id);
        if (!missing) changed.PluginPath = Path.GetDirectoryName(changed.PluginPath)!;

        await f.Chain(changed);

        Assert.Equal(2, f.Children.Count);
        Assert.True(f.Service.IsGuitarVstSlotLoaded(entry.Id));
        Assert.Contains(1, f.Bridge.EnabledSeats);
        Assert.Contains(f.Children[1].Requests, request => request.Op == "load" && request.Path == changed.PluginPath);
    }

    [Fact]
    public async Task AnUnrelatedEditOnTheSameSeatDoesNotReloadARejectedSlot()
    {
        using var f = new VstSandboxPerSeatFixture();
        var loaded = f.Entry(1);
        var rejected = f.Entry(1);
        f.ConfigureChild = child => child.RejectLoadSlots.Add(rejected.Id.ToString());
        await f.Chain(loaded, rejected);

        await f.Chain(f.Entry(1, loaded.Id, bypass: true), rejected);

        Assert.Single(f.Children);
        Assert.Single(f.Children[0].Requests, request => request.Op == "load" && request.Slot == rejected.Id.ToString());
        Assert.True(f.Service.IsGuitarVstSlotLoaded(loaded.Id));
    }
}
