using DotCharter.Tools.RigDataset;

namespace DotCharter.Tests.Ai.Dataset;

/// <summary>
///     The stage-B device rule: name the compute card, pin it by uuid, and REFUSE the
///     owner's gaming card.
///     <para>
///         This box has an RTX 3080 (compute) and an RTX 5070 Ti (the owner's gaming
///         GPU), and the three numberings in play disagree — <c>nvidia-smi</c> is PCI
///         ordered (3080 = 0), CUDA defaults to <c>FASTEST_FIRST</c>, and torch calls
///         the 5070 Ti <c>cuda:0</c>. A pipeline that hard-codes an index is one
///         driver update from taking over the machine somebody is playing on, and
///         nothing about that failure prints a message.
///     </para>
/// </summary>
public sealed class RigGpuSelectorTests
{
    private static readonly GpuDevice Compute =
        new(0, "NVIDIA GeForce RTX 3080", "GPU-11111111-2222-3333-4444-555555555555");

    private static readonly GpuDevice Gaming =
        new(1, "NVIDIA GeForce RTX 5070 Ti", "GPU-99999999-8888-7777-6666-555555555555");

    private static readonly GpuDevice[] ThisBox = [Compute, Gaming];

    [Fact]
    public void ItPicksTheNamedCard_AndPinsItByUuidRatherThanIndex()
    {
        var choice = GpuSelector.Select(ThisBox, GpuSelector.DefaultWantedName, allowGamingGpu: false);

        Assert.Equal(Compute, choice.Device);
        Assert.Equal(Compute.Uuid, choice.VisibleDevices);
        Assert.Contains("by uuid", choice.Reason, StringComparison.Ordinal);
        Assert.Contains("3080", choice.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TheIndexIsOnlyUsedWhenThereIsNoUuid()
    {
        var noUuid = new GpuDevice(0, "NVIDIA GeForce RTX 3080", string.Empty);
        var choice = GpuSelector.Select([noUuid, Gaming], "3080", allowGamingGpu: false);
        Assert.Equal("0", choice.VisibleDevices);
    }

    [Fact]
    public void ItRefusesTheGamingGpu_UnlessItIsExplicitlyAllowed()
    {
        var ex = Assert.Throws<GpuSelectionException>(
            () => GpuSelector.Select(ThisBox, "5070", allowGamingGpu: false));
        Assert.Contains("GAMING GPU", ex.Message, StringComparison.Ordinal);
        Assert.Contains("--allow-gaming-gpu", ex.Message, StringComparison.Ordinal);

        // …and the escape hatch works, or the refusal is a wall rather than a guard.
        var allowed = GpuSelector.Select(ThisBox, "5070", allowGamingGpu: true);
        Assert.Equal(Gaming, allowed.Device);
    }

    [Fact]
    public void AskingForACardTheBoxDoesNotHave_FailsLoudlyAndListsWhatItHas()
    {
        var ex = Assert.Throws<GpuSelectionException>(
            () => GpuSelector.Select(ThisBox, "4090", allowGamingGpu: false));
        Assert.Contains("4090", ex.Message, StringComparison.Ordinal);
        Assert.Contains("RTX 3080", ex.Message, StringComparison.Ordinal);
        Assert.Contains("RTX 5070 Ti", ex.Message, StringComparison.Ordinal);

        Assert.Throws<GpuSelectionException>(
            () => GpuSelector.Select([], "3080", allowGamingGpu: false));
    }

    [Fact]
    public void ItParsesWhatNvidiaSmiActuallyPrints_HeaderOrNot()
    {
        const string withHeader = """
            index, name, uuid
            0, NVIDIA GeForce RTX 3080, GPU-aaaa-bbbb
            1, NVIDIA GeForce RTX 5070 Ti, GPU-cccc-dddd
            """;

        var parsed = GpuSelector.ParseNvidiaSmi(withHeader);
        Assert.Equal(2, parsed.Count);
        Assert.Equal(0, parsed[0].Index);
        Assert.Equal("NVIDIA GeForce RTX 3080", parsed[0].Name);
        Assert.Equal("GPU-aaaa-bbbb", parsed[0].Uuid);
        Assert.Equal("NVIDIA GeForce RTX 5070 Ti", parsed[1].Name);

        Assert.Empty(GpuSelector.ParseNvidiaSmi(null));
        Assert.Empty(GpuSelector.ParseNvidiaSmi("   "));
    }

    [Fact]
    public void TheGamingMarkerIsNotBroaderThanItLooks()
    {
        // A marker matched too widely would refuse the compute card too, which reads
        // as "stage B cannot run" and is the failure nobody debugs.
        Assert.True(GpuSelector.IsGamingGpu("NVIDIA GeForce RTX 5070 Ti"));
        Assert.False(GpuSelector.IsGamingGpu("NVIDIA GeForce RTX 3080"));
        Assert.False(GpuSelector.IsGamingGpu("NVIDIA RTX A5000"));
    }

    /// <summary>
    ///     The ONNX path runs on DirectML, whose device id is the DXGI adapter index —
    ///     on this box adapter 0 is the 5070 Ti, the GAMING card, while nvidia-smi
    ///     calls the 3080 card 0. The chosen name must land on the 3080's DXGI index.
    /// </summary>
    [Fact]
    public void TheChosenCard_MapsToItsDxgiAdapter_ByName()
    {
        (int, string)[] dxgi = [(0, "NVIDIA GeForce RTX 5070 Ti"), (1, "NVIDIA GeForce RTX 3080"), (2, "Microsoft Basic Render Driver")];
        var choice = GpuSelector.Select(ThisBox, GpuSelector.DefaultWantedName, allowGamingGpu: false);
        Assert.Equal(1, GpuSelector.DirectMlDeviceFor(choice.Device.Name, dxgi));
    }

    /// <summary>Two identical cards, or none, refuse: guessing lands on adapter 0, the display card.</summary>
    [Fact]
    public void AnAmbiguousOrMissingCard_MapsToNothing()
    {
        (int, string)[] twins = [(0, "NVIDIA GeForce RTX 3080"), (1, "NVIDIA GeForce RTX 3080")];
        Assert.Null(GpuSelector.DirectMlDeviceFor("NVIDIA GeForce RTX 3080", twins));
        Assert.Null(GpuSelector.DirectMlDeviceFor("NVIDIA GeForce RTX 3080", [(0, "AMD Radeon RX 7900 XTX")]));
    }
}
