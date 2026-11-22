using DotCharter.Tone.Audio.Vst;
using DotCharter.Tone.Audio.Vst.Sandbox;

namespace DotCharter.Tests.Services.Audio.VstSandbox;

/// <summary>
/// #646: a sandboxed effect belongs to its player's seat. The seat must reach
/// the child's <c>load</c> call (on the wire, as JSON), and a slot moved to
/// another player must be reloaded rather than kept on the old seat.
/// </summary>
public sealed class VstSandboxChainPlanTests
{
    private static VstChainEntry Entry(int player) => new()
    {
        Id = Guid.Parse("8e7c63a1-5b5c-4d1d-9a43-1f2a3b4c5d6e"),
        PluginPath = @"C:\Plugins\Amp.vst3",
        Bypass = false,
        Player = player,
    };

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void TheLoadRequest_CarriesTheSlotsSeat_OnTheWire(int player)
    {
        var request = VstSandboxChainPlan.LoadRequest(Entry(player));

        Assert.Equal("load", request.Op);
        Assert.Equal(player, request.Seat);
        Assert.Contains($"\"seat\":{player}", VstSandboxProtocol.Serialize(request), StringComparison.Ordinal);
    }

    [Fact]
    public void ASlotMovedToAnotherSeat_IsUnloadedSoItReloadsOnTheNewSeat()
    {
        var path = Entry(0).PluginPath;

        Assert.False(VstSandboxChainPlan.MustUnload(Entry(1), path, livePlayer: 1));
        Assert.True(VstSandboxChainPlan.MustUnload(Entry(1), path, livePlayer: 0));
        Assert.True(VstSandboxChainPlan.MustUnload(Entry(1), @"C:\Plugins\Other.vst3", livePlayer: 1));
        Assert.True(VstSandboxChainPlan.MustUnload(null, path, livePlayer: 1));
    }
}
