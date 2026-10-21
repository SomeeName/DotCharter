using DotCharter.Tone.Audio.Dsp;
using JuceSharp;

namespace DotCharter.Tests.Services.Audio.Dsp;

[Collection("NativeEngine")]
public sealed class DspStagedInsertionNativeTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void ParkedInsertion_RemainsDryUntilConfiguredAndActivated(int player)
    {
        JuceAudioEngine? engine = null;
        // The Null device, not the default one: the test renders offline only, and a
        // hosted CI runner has no audio endpoint (engine_create failed with Device).
        try
        {
            engine = new JuceAudioEngine(new JuceEngineConfig
            {
                EnableConsoleLog = false,
                PreferredDriver = AudioDriver.Wasapi,
                OutputTypeName = "Null",
                SampleRate = 48_000,
                OutputChannels = 2,
                EnableNullDevice = true,
            });
        }
        catch (DllNotFoundException) { Assert.Skip("Native audio library unavailable"); }
        using var lifetime = engine!;
        engine!.Stop(); // Offline rendering must be the only processor.
        var chain = new JuceDspChainService(player: player);
        chain.LoadCatalog();
        chain.AttachEngine(engine!);
        try
        {
            var node = Assert.IsType<DspNode>(chain.AddNode("gain", initiallyBypassed: true));
            Assert.True(node.Bypassed);
            Assert.True(chain.SetParam(node.NodeId, "gainDb", 12));
            var left = Enumerable.Repeat(0.1f, 512).ToArray();
            var right = (float[])left.Clone();
            Assert.True(chain.ProcessOfflineBuffer(left, right, left.Length, 48000));
            Assert.All(left, x => Assert.Equal(0.1f, x));
            Assert.Equal(left, right);

            Assert.True(chain.SetParam(node.NodeId, "gainDb", -12));
            Assert.True(chain.SetBypass(node.NodeId, false));
            for (var block = 0; block < 8; block++)
            {
                Array.Fill(left, 0.1f);
                Array.Fill(right, 0.1f);
                Assert.True(chain.ProcessOfflineBuffer(left, right, left.Length, 48000));
                Assert.All(left, x => Assert.InRange(x, 0, 0.10001f));
                Assert.Equal(left, right);
            }
            Assert.InRange(left[^1], 0.02511f, 0.02513f);
        }
        finally { chain.AttachEngine(nint.Zero); }
    }
}
