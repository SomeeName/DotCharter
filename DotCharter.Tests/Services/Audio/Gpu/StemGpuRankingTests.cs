using DotCharter.Services.Audio.StemSeparation.Gpu;

namespace DotCharter.Tests.Services.Audio.Gpu;

/// <summary>
///     Which GPU stem separation runs on, from an injected adapter list — no GPU needed.
///     The owner's machine lists the RTX 5070 Ti as adapter 0 and the RTX 3080 as
///     adapter 1; a laptop typically lists its integrated GPU FIRST, which is the case
///     "adapter 0 by accident" gets wrong.
/// </summary>
public sealed class StemGpuRankingTests
{
    private const ulong Gb = 1UL << 30;

    private static readonly StemGpuAdapter Igpu = new(0, "Intel(R) UHD Graphics 770", 128UL << 20, false);
    private static readonly StemGpuAdapter Rtx3080 = new(1, "NVIDIA GeForce RTX 3080", 10 * Gb, false);
    private static readonly StemGpuAdapter Rtx5070 = new(2, "NVIDIA GeForce RTX 5070 Ti", 16 * Gb, false);
    private static readonly StemGpuAdapter Warp = new(3, "Microsoft Basic Render Driver", 0, true);

    [Fact]
    public void Rank_PutsTheLargestUsableGpuFirst_NotAdapterZero()
    {
        var ranked = StemGpuRanking.Rank([Igpu, Rtx3080, Rtx5070, Warp]);

        Assert.Equal([Rtx5070, Rtx3080, Igpu], ranked.Select(r => r.Adapter));
        Assert.Equal(Rtx5070, StemGpuRanking.Resolve(ranked, null)!.Adapter);
    }

    [Fact]
    public void Rank_DropsTheSoftwareAdapter_SoOneGpuIsNeverListedAsTwo()
    {
        var ranked = StemGpuRanking.Rank([Rtx3080, Warp]);
        Assert.Equal([Rtx3080], ranked.Select(r => r.Adapter));
    }

    [Fact]
    public void AGpuWithTooLittleVideoMemory_IsListedButBlocked_AndNeverChosen()
    {
        var ranked = StemGpuRanking.Rank([Igpu]);

        var only = Assert.Single(ranked);
        Assert.False(only.IsUsable);
        Assert.Equal(StemGpuBlocker.TooLittleVideoMemory, only.Blocker);
        Assert.Null(StemGpuRanking.Resolve(ranked, null));
        Assert.Null(StemGpuRanking.Resolve(ranked, only.Key));
    }

    [Fact]
    public void TheMinimum_IsAtTheBoundary()
    {
        var exactly = new StemGpuAdapter(0, "Exactly 3 GB", StemGpuRanking.MinimumVideoMemoryBytes, false);
        var under = exactly with { Name = "Just under", DedicatedVideoMemoryBytes = StemGpuRanking.MinimumVideoMemoryBytes - 1 };
        Assert.True(StemGpuRanking.Rank([exactly]).Single().IsUsable);
        Assert.False(StemGpuRanking.Rank([under]).Single().IsUsable);
    }

    [Fact]
    public void EqualCards_AreOrderedByDeviceId()
    {
        var a = new StemGpuAdapter(1, "Twin", 8 * Gb, false);
        var b = new StemGpuAdapter(0, "Twin", 8 * Gb, false);
        Assert.Equal([0, 1], StemGpuRanking.Rank([a, b]).Select(r => r.Adapter.DeviceId));
    }

    [Fact]
    public void AStoredPick_WinsOverTheFastest()
    {
        var ranked = StemGpuRanking.Rank([Rtx3080, Rtx5070]);
        Assert.Equal(Rtx3080, StemGpuRanking.Resolve(ranked, StemGpuRanking.KeyFor(Rtx3080))!.Adapter);
    }

    /// <summary>A display change renumbers adapters; the stored pick follows its card by name.</summary>
    [Fact]
    public void AStoredPick_FollowsItsCardByName_WhenTheIdsMoved()
    {
        var stored = StemGpuRanking.KeyFor(Rtx3080); // "1:NVIDIA GeForce RTX 3080"
        var renumbered = StemGpuRanking.Rank([Rtx3080 with { DeviceId = 0 }, Rtx5070 with { DeviceId = 1 }]);
        Assert.Equal(0, StemGpuRanking.Resolve(renumbered, stored)!.Adapter.DeviceId);
    }

    /// <summary>Two identical cards share a name: the id is what tells them apart.</summary>
    [Fact]
    public void AStoredPick_TellsIdenticalCardsApartById()
    {
        var first = new StemGpuAdapter(0, "Twin", 8 * Gb, false);
        var second = new StemGpuAdapter(1, "Twin", 8 * Gb, false);
        var ranked = StemGpuRanking.Rank([first, second]);
        Assert.Equal(1, StemGpuRanking.Resolve(ranked, StemGpuRanking.KeyFor(second))!.Adapter.DeviceId);
    }

    [Fact]
    public void AStoredPickThatIsGoneOrBlocked_FallsBackToTheFastest()
    {
        var ranked = StemGpuRanking.Rank([Igpu, Rtx3080, Rtx5070]);
        Assert.Equal(Rtx5070, StemGpuRanking.Resolve(ranked, "7:Some eGPU that was unplugged")!.Adapter);
        Assert.Equal(Rtx5070, StemGpuRanking.Resolve(ranked, StemGpuRanking.KeyFor(Igpu))!.Adapter);
    }

    // ---- #463: the display GPU ------------------------------------------------------

    /// <summary>The owner's machine as DXGI listed it on 2026-10-08.</summary>
    private static readonly StemGpuAdapter Owner5070 = new(0, "NVIDIA GeForce RTX 5070 Ti", 16 * Gb, false, DrivesDisplay: true, Luid: 0x00010ee9);
    private static readonly StemGpuAdapter Owner3080 = new(1, "NVIDIA GeForce RTX 3080", 10 * Gb, false, DrivesDisplay: false, Luid: 0x0000fda7);
    private static readonly StemGpuAdapter OwnerWarp = new(2, "Microsoft Basic Render Driver", 0, true, Luid: 0x000122b0);

    [Fact]
    public void Automatic_WithADisplayGpuAndAnIdleOne_PicksTheIdleOne_EvenWithLessVideoMemory()
    {
        var choice = StemGpuRanking.Choose(StemGpuRanking.Rank([Owner5070, Owner3080, OwnerWarp]), "");

        Assert.Equal(Owner3080, choice.Gpu!.Adapter);
        Assert.Contains("drives no display", choice.Reason, StringComparison.Ordinal);
        Assert.StartsWith("automatic", choice.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Automatic_WithOneGpu_PicksAdapterZero_EvenThoughItDrivesTheDisplay()
    {
        var choice = StemGpuRanking.Choose(StemGpuRanking.Rank([Owner5070, OwnerWarp]), null);

        Assert.Equal(0, choice.Gpu!.Adapter.DeviceId);
        Assert.Equal("automatic: the only usable GPU", choice.Reason);
    }

    [Fact]
    public void Automatic_WhenEveryGpuDrivesADisplay_TakesTheMostVideoMemory()
    {
        var choice = StemGpuRanking.Choose(
            StemGpuRanking.Rank([Owner3080 with { DeviceId = 0, DrivesDisplay = true }, Owner5070 with { DeviceId = 1 }]), "");

        Assert.Equal("NVIDIA GeForce RTX 5070 Ti", choice.Gpu!.Adapter.Name);
        Assert.Contains("every GPU drives a display", choice.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AnExplicitChoice_WinsOverAutomatic_EvenOnTheDisplayGpu()
    {
        var ranked = StemGpuRanking.Rank([Owner5070, Owner3080]);

        var exact = StemGpuRanking.Choose(ranked, StemGpuRanking.KeyFor(Owner5070));
        Assert.Equal(Owner5070, exact.Gpu!.Adapter);
        Assert.Equal("chosen in Settings", exact.Reason);

        // A stored name with no id — or one whose id moved — still finds its card.
        var byName = StemGpuRanking.Choose(ranked, "NVIDIA GeForce RTX 5070 Ti");
        Assert.Equal(Owner5070, byName.Gpu!.Adapter);
        Assert.Contains("found by name", byName.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownName_FallsBackToAutomatic_AndSaysSo()
    {
        var choice = StemGpuRanking.Choose(StemGpuRanking.Rank([Owner5070, Owner3080]), "3:NVIDIA GeForce RTX 9090");

        Assert.Equal(Owner3080, choice.Gpu!.Adapter);
        Assert.Contains("('NVIDIA GeForce RTX 9090') is missing", choice.Reason, StringComparison.Ordinal);
        Assert.Contains("drives no display", choice.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void NoUsableGpu_IsNullWithAReason()
    {
        var choice = StemGpuRanking.Choose(StemGpuRanking.Rank([Igpu, Warp]), "");
        Assert.Null(choice.Gpu);
        Assert.Contains("3 GB", choice.Reason, StringComparison.Ordinal);
    }

    /// <summary>The log names every card the way Windows' GPU counters do, so a perf trace maps to a device id.</summary>
    [Fact]
    public void DescribeAdapters_ListsIndexNameMemoryLuidAndDisplay()
    {
        Assert.Equal(
            "#0 'NVIDIA GeForce RTX 5070 Ti' 16384 MB luid=0x00000000_0x00010ee9 drives a display; " +
            "#1 'NVIDIA GeForce RTX 3080' 10240 MB luid=0x00000000_0x0000fda7 no display; " +
            "#2 'Microsoft Basic Render Driver' 0 MB luid=0x00000000_0x000122b0 software",
            StemGpuRanking.DescribeAdapters([Owner5070, Owner3080, OwnerWarp]));
        Assert.Equal("0x00000001_0x00000002", new StemGpuAdapter(0, "x", 0, false, Luid: (1UL << 32) | 2).LuidText);
    }
}
