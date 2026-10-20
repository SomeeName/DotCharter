using DotCharter.Tone.Audio.Vst;
using DotCharter.Tone.Audio.Vst.Sandbox;

namespace DotCharter.Tests.Services.Audio.VstSandbox;

public class VstSandboxProtocolTests
{
    [Fact]
    public void RequestSerializesCamelCaseAndOmitsNulls()
    {
        var json = VstSandboxProtocol.Serialize(new VstSandboxRequest
        {
            Id = 7, Op = "load", Slot = "abc", Path = @"C:\x.vst3", Bypass = false,
        });
        Assert.Contains("\"id\":7", json);
        Assert.Contains("\"op\":\"load\"", json);
        Assert.Contains("\"slot\":\"abc\"", json);
        Assert.Contains("\"bypass\":false", json);
        // Unset optional fields must not appear (the child treats a missing
        // property as void, not as null).
        Assert.DoesNotContain("state", json);
        Assert.DoesNotContain("slots", json);
        Assert.DoesNotContain("null", json);
    }

    [Fact]
    public void ResponseLineFromChildParses()
    {
        // Exactly what juce::JSON::toString(allOnOneLine) emits for a load reply.
        var msg = VstSandboxProtocol.TryParse("{\"id\": 3, \"ok\": true, \"latency\": 128, \"name\": \"BIAS FX 2\"}");
        Assert.NotNull(msg);
        Assert.True(msg!.IsResponse);
        Assert.Equal(3, msg.Id);
        Assert.True(msg.Ok);
        Assert.Equal(128, msg.Latency);
        Assert.Equal("BIAS FX 2", msg.Name);
    }

    [Fact]
    public void HelloAndEventLinesAreClassified()
    {
        var hello = VstSandboxProtocol.TryParse("{\"op\": \"hello\", \"pid\": 4242, \"bridgeVersion\": 1}");
        Assert.NotNull(hello);
        Assert.True(hello!.IsHello);
        Assert.False(hello.IsResponse);
        Assert.Equal(4242, hello.Pid);
        Assert.Equal(1, hello.BridgeVersion);

        var ev = VstSandboxProtocol.TryParse("{\"op\": \"event\", \"event\": \"editorClosed\", \"slot\": \"instrument\"}");
        Assert.NotNull(ev);
        Assert.True(ev!.IsEvent);
        Assert.Equal("editorClosed", ev.Event);
        Assert.Equal(VstSandboxProtocol.InstrumentSlotId, ev.Slot);
    }

    [Fact]
    public void ErrorResponseCarriesMessage()
    {
        var msg = VstSandboxProtocol.TryParse("{\"id\": 9, \"ok\": false, \"error\": \"VST3: no descriptors for C:\\\\nope.vst3\"}");
        Assert.NotNull(msg);
        Assert.False(msg!.Ok);
        Assert.Contains("no descriptors", msg.Error);
    }

    [Fact]
    public void GarbageLinesReturnNullInsteadOfThrowing()
    {
        Assert.Null(VstSandboxProtocol.TryParse(""));
        Assert.Null(VstSandboxProtocol.TryParse("   "));
        Assert.Null(VstSandboxProtocol.TryParse("not json at all"));
        Assert.Null(VstSandboxProtocol.TryParse("{\"op\": unterminated"));
    }

    [Fact]
    public void ChainStateRoundTripsThroughJson()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var state = new VstSandboxChainState
        {
            Chain =
            {
                new VstChainEntry { Id = id1, PluginPath = @"C:\VST3\BIAS FX 2.vst3", Bypass = false },
                new VstChainEntry { Id = id2, PluginPath = @"C:\VST3\NeuralDsp.vst3", Bypass = true },
            },
            InstrumentPath = @"C:\VST3\AGPF.vst3",
            PluginStates = { ["instrument"] = "AAECAw==" },
        };

        var restored = VstSandboxChainState.FromJson(state.ToJson());
        Assert.NotNull(restored);
        Assert.Equal(2, restored!.Chain.Count);
        Assert.Equal(id1, restored.Chain[0].Id);
        Assert.False(restored.Chain[0].Bypass);
        Assert.Equal(id2, restored.Chain[1].Id);
        Assert.True(restored.Chain[1].Bypass);
        Assert.Equal(state.InstrumentPath, restored.InstrumentPath);
        Assert.Equal("AAECAw==", restored.PluginStates["instrument"]);
        Assert.False(restored.IsEmpty);
    }

    [Fact]
    public void ChainStateCloneIsDeep()
    {
        var state = new VstSandboxChainState
        {
            Chain = { new VstChainEntry { PluginPath = "a" } },
            InstrumentPath = "i",
            PluginStates = { ["x"] = "y" },
        };
        var clone = state.Clone();
        clone.Chain[0].PluginPath = "changed";
        clone.PluginStates["x"] = "changed";
        clone.InstrumentPath = "changed";

        Assert.Equal("a", state.Chain[0].PluginPath);
        Assert.Equal("y", state.PluginStates["x"]);
        Assert.Equal("i", state.InstrumentPath);
    }
}
