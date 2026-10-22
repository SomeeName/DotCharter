using System.IO.MemoryMappedFiles;
using System.Runtime.Versioning;
using DotCharter.Tone.Audio.Tonelab;

namespace DotCharter.Tests.Audio.Tonelab;

/// <summary>
///     The draft's shared-memory layout, exercised from the host side against the mock
///     game: open the block BY NAME (as the native insert will), check the header, write
///     mono input blocks, and read back stereo blocks that are the input times the gain,
///     delayed. This pins the layout the native phase must implement; it moves no audio
///     in the app.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TonelabSharedMemoryMockTests
{
    [Fact]
    public void InputBlocks_ComeBack_ScaledAndDelayed_OnBothChannels()
    {
        const int block = 128;
        const int blocks = 4;
        using var game = new TonelabGameMock { Gain = 0.25f, DelaySamples = 5 };
        using var mock = game.CreateAudio(block);

        using var map = MemoryMappedFile.OpenExisting(TonelabProtocol.SharedMemoryNameFor(game.ProcessId));
        using var view = map.CreateViewAccessor();
        var magic = new byte[8];
        view.ReadArray(0, magic, 0, 8);
        Assert.Equal("TONELAB\0"u8.ToArray(), magic);
        Assert.Equal(48000u, view.ReadUInt32(12));
        Assert.Equal((uint)block, view.ReadUInt32(16));
        Assert.Equal(32u, view.ReadUInt32(20));
        Assert.Equal((1u, 2u), (view.ReadUInt32(24), view.ReadUInt32(28)));
        Assert.Equal(64, mock.InOffset);
        Assert.Equal(0, mock.OutOffset % 64);

        var input = Enumerable.Range(0, block * blocks).Select(i => (float)Math.Sin(i * 0.05)).ToArray();
        for (var b = 0; b < blocks; b++)
        {
            view.WriteArray(mock.InOffset + (b % mock.RingBlocks * block * 4), input, b * block, block);
            view.Write(TonelabSharedMemoryMock.InWriteOffset, (uint)(b + 1));
        }

        Assert.True(SpinWait.SpinUntil(() => view.ReadUInt32(TonelabSharedMemoryMock.OutWriteOffset) == blocks, TimeSpan.FromSeconds(60)));

        var output = new float[block * 2];
        for (var b = 0; b < blocks; b++)
        {
            view.ReadArray(mock.OutOffset + (b % mock.RingBlocks * block * 2 * 4), output, 0, output.Length);
            for (var i = 0; i < block; i++)
            {
                var n = (b * block) + i;
                var expected = n >= 5 ? input[n - 5] * 0.25f : 0f;
                Assert.Equal(expected, output[2 * i], 6);
                Assert.Equal(expected, output[(2 * i) + 1], 6);
            }
        }

        view.Write(TonelabSharedMemoryMock.OutReadOffset, (uint)blocks);
        Assert.Equal((uint)blocks, view.ReadUInt32(TonelabSharedMemoryMock.InReadOffset));
    }
}
