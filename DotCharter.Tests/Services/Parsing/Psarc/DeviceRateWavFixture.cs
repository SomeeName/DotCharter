namespace DotCharter.Tests.Services.Parsing.Psarc;

/// <summary>
///     #435: WAV bytes in the two shapes a cached song can have -- an ordinary 16-bit
///     WAV, and JuceSharp's device-rate WAV (ABI v43: <c>fmt </c>, a 24-byte
///     <c>dcps</c> chunk, <c>data</c>; <c>src/JuceSharp.Native/src/DeviceRateWav.h</c>).
///     The samples are silence: these tests are about headers.
/// </summary>
internal static class DeviceRateWavFixture
{
    public static byte[] Plain(int rate, int frames = 256, int channels = 2)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        var data = frames * channels * 2;
        w.Write("RIFF"u8); w.Write(36 + data); w.Write("WAVE"u8);
        WriteFmt(w, rate, channels);
        w.Write("data"u8); w.Write(data);
        w.Write(new byte[data]);
        w.Flush();
        return ms.ToArray();
    }

    public static byte[] DeviceRate(int rate, int frames = 256, int channels = 2, float step = 1.1f / 32768f,
        int sourceRate = 44_100)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        var data = frames * channels * 2;
        w.Write("RIFF"u8); w.Write(4 + 24 + 32 + 8 + data); w.Write("WAVE"u8);
        WriteFmt(w, rate, channels);
        w.Write("dcps"u8); w.Write(24);
        w.Write(1u); w.Write(step); w.Write((ulong)frames); w.Write((uint)sourceRate); w.Write(0u);
        w.Write("data"u8); w.Write(data);
        w.Write(new byte[data]);
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>A device-rate file with its last <paramref name="bytes"/> cut off.</summary>
    public static byte[] Truncated(int rate, int bytes = 100) => DeviceRate(rate)[..^bytes];

    private static void WriteFmt(BinaryWriter w, int rate, int channels)
    {
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)channels); w.Write(rate);
        w.Write(rate * channels * 2); w.Write((short)(channels * 2)); w.Write((short)16);
    }
}
