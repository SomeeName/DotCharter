using DotCharter.Tone.Audio.Abstractions;
using DotCharter.Services.Infrastructure.Graphics;

namespace DotCharter.Tests.Infrastructure;

/// <summary>An <see cref="INativeEngineProbe" /> that answers what it is told and counts its calls — no DLL is loaded.</summary>
internal sealed class FakeNativeEngineProbe(NativeEngineCheck answer) : INativeEngineProbe
{
    public FakeNativeEngineProbe() : this(new NativeEngineCheck(NativeEngineState.Ready))
    {
    }

    public Exception? Throws { get; init; }

    public int Calls { get; private set; }

    public NativeEngineCheck Probe()
    {
        Calls++;
        if (Throws != null) throw Throws;
        return answer;
    }
}

/// <summary>An <see cref="IGpuRenderProbe" /> that answers what it is told and counts its calls — no Vulkan call is made.</summary>
internal sealed class FakeGpuRenderProbe(GpuRenderCheck answer) : IGpuRenderProbe
{
    public FakeGpuRenderProbe() : this(new GpuRenderCheck(GpuRenderState.Ready, "Fake GPU"))
    {
    }

    public Exception? Throws { get; init; }

    public int Calls { get; private set; }

    public GpuRenderCheck Probe()
    {
        Calls++;
        if (Throws != null) throw Throws;
        return answer;
    }
}
