namespace DotCharter.Services.Export.Audio;

/// <summary>
///     DLC Builder's "calculate volume" (<c>Rocksmith2014.Audio/Volume</c>): the file's BS.1770
///     integrated loudness against a reference of -16, as dB, rounded to one decimal
///     (half away from zero); a file with no measurable loudness reads 0. The measurement is
///     a port of the meter DLC Builder ports from R128Normalization (<see cref="Bs1770Meter" />),
///     fed the way DLC Builder feeds it: the file's own sample rate and channels, 50,000
///     samples a read.
/// </summary>
public static class RocksmithAutoVolume
{
    /// <summary>DLC Builder's reference loudness (LUFS).</summary>
    public const double ReferenceLoudness = -16.0;

    private const int ReadSamples = 50_000;

    /// <summary>
    ///     The volume for <paramref name="path" />. A <c>.wem</c> cannot be read: DLC Builder
    ///     measures the <c>.wav</c> or <c>.ogg</c> beside it, and so does this
    ///     (<see cref="SourceFor" />); with none, <see cref="NotSupportedException" />.
    /// </summary>
    public static double Calculate(string path)
    {
        var source = SourceFor(path);
        var warnings = new List<string>();
        var (samples, channels, rate) = Decode(source, warnings);
        return FromSamples(samples, channels, rate);
    }

    /// <summary>The file to measure for <paramref name="path" />: itself, or for a <c>.wem</c> the <c>.wav</c> / <c>.ogg</c> beside it.</summary>
    public static string SourceFor(string path)
    {
        if (!path.EndsWith(".wem", StringComparison.OrdinalIgnoreCase)) return path;
        foreach (var extension in new[] { "wav", "ogg" })
        {
            var sibling = Path.ChangeExtension(path, extension);
            if (File.Exists(sibling)) return sibling;
        }

        throw new NotSupportedException(
            $"'{Path.GetFileName(path)}' is a .wem, which cannot be measured. Put the .wav or .ogg it was made from beside it.");
    }

    /// <summary>The volume of interleaved <paramref name="samples" />.</summary>
    public static double FromSamples(float[] samples, int channels, int sampleRate)
    {
        var loudness = MeasureLoudness(samples, channels, sampleRate);
        return double.IsInfinity(loudness) || double.IsNaN(loudness)
            ? 0.0
            : Math.Round(ReferenceLoudness - loudness, 1, MidpointRounding.AwayFromZero);
    }

    /// <summary>The integrated loudness (LUFS) of interleaved <paramref name="samples" />, fed 50,000 samples at a time.</summary>
    internal static double MeasureLoudness(float[] samples, int channels, int sampleRate)
    {
        var meter = new Bs1770Meter(sampleRate, channels);
        var perRead = ReadSamples / channels * channels;
        for (var offset = 0; offset < samples.Length; offset += perRead)
        {
            var read = Math.Min(perRead, samples.Length - offset);
            var perChannel = read / channels;
            var block = new double[channels][];
            for (var channel = 0; channel < channels; channel++)
            {
                block[channel] = new double[perChannel];
                for (var i = 0; i < perChannel; i++) block[channel][i] = samples[offset + i * channels + channel];
            }

            meter.ProcessBuffer(block);
        }

        return meter.GetIntegratedLoudness();
    }

    /// <summary>The engine's own decode (native rate and channels, 32-bit float), else the export's decoder.</summary>
    private static (float[] Samples, int Channels, int SampleRate) Decode(string path, IList<string> warnings)
    {
        try
        {
            var buf = JuceSharp.AudioIo.JuceAudioDecoder.Decode(path);
            if (buf.Channels > 0 && buf.SampleRate > 0 && buf.FrameCount > 0)
                return (buf.Interleaved, buf.Channels, buf.SampleRate);
        }
        catch (Exception)
        {
            // Not a format the engine reads: the export's decoders take it.
        }

        var pcm = PcmAudioLoader.Load(path, warnings);
        var floats = new float[pcm.Samples.Length];
        for (var i = 0; i < floats.Length; i++) floats[i] = pcm.Samples[i] / 32768f;
        return (floats, pcm.Channels, pcm.SampleRate);
    }
}

/// <summary>
///     Port of <c>LufsMeter</c> (Rocksmith2014.NET, itself from xuan525/R128Normalization):
///     400 ms blocks overlapping 75 %, K-weighted, gated at -70 LUFS and then 10 LU under the
///     result. Kept line for line, including its start (the first blocks are zero-padded and
///     count) and its end (a step that ends exactly at a buffer's end is not counted).
/// </summary>
internal sealed class Bs1770Meter
{
    private readonly List<(double MeanSquare, double Loudness)> _preceding = [];
    private readonly int _channels;
    private readonly int _stepSampleCount;
    private readonly int _blockStepCount;
    private readonly double[][][] _blockBuffer;
    private readonly IirFilter _preFilter;
    private readonly IirFilter _highPass;
    private double[][] _stepBuffer;
    private int _stepBufferPosition;

    public Bs1770Meter(double sampleRate, int channels, double blockDuration = 0.4, double overlap = 0.75)
    {
        _channels = channels;
        var blockSampleCount = (int)Math.Round(blockDuration * sampleRate);
        _stepSampleCount = (int)Math.Round(blockSampleCount * (1.0 - overlap));
        _blockStepCount = blockSampleCount / _stepSampleCount;
        _stepBuffer = NewStep();
        _blockBuffer = new double[_blockStepCount][][];
        for (var i = 0; i < _blockStepCount; i++) _blockBuffer[i] = NewStep();
        _preFilter = new IirFilter(1.53512485958697, -2.69169618940638, 1.19839281085285, -1.69065929318241,
            0.73248077421585, sampleRate, channels);
        _highPass = new IirFilter(1.0, -2.0, 1.0, -1.99004745483398, 0.99007225036621, sampleRate, channels);
    }

    private double[][] NewStep()
    {
        var step = new double[_channels][];
        for (var c = 0; c < _channels; c++) step[c] = new double[_stepSampleCount];
        return step;
    }

    private static double ChannelWeight(int channel) => channel is 3 or 4 ? 1.41 : 1.0;

    private static double Loudness(double mean) => -0.691 + 10.0 * Math.Log10(mean);

    /// <summary>Feeds one buffer (channel-major); the filters run in place on it.</summary>
    public void ProcessBuffer(double[][] buffer)
    {
        _preFilter.Process(buffer);
        _highPass.Process(buffer);

        var bufferPosition = 0;
        var bufferSampleCount = buffer[0].Length;
        while (bufferPosition + (_stepSampleCount - _stepBufferPosition) < bufferSampleCount)
        {
            var take = _stepSampleCount - _stepBufferPosition;
            for (var c = 0; c < _channels; c++)
                Array.Copy(buffer[c], bufferPosition, _stepBuffer[c], _stepBufferPosition, take);
            bufferPosition += take;

            var oldest = _blockBuffer[0];
            for (var i = 1; i < _blockBuffer.Length; i++) _blockBuffer[i - 1] = _blockBuffer[i];
            _blockBuffer[^1] = _stepBuffer;
            _stepBuffer = oldest;
            _stepBufferPosition = 0;

            var momentaryMeanSquare = 0.0;
            for (var c = 0; c < _channels; c++)
            {
                var squaredSum = 0.0;
                foreach (var step in _blockBuffer)
                {
                    var samples = step[c];
                    var sum = 0.0;
                    for (var i = 0; i < samples.Length; i++) sum += samples[i] * samples[i];
                    squaredSum += sum;
                }

                momentaryMeanSquare += ChannelWeight(c) * (squaredSum / (_blockStepCount * _stepSampleCount));
            }

            _preceding.Add((momentaryMeanSquare, Loudness(momentaryMeanSquare)));
        }

        var remaining = bufferSampleCount - bufferPosition;
        for (var c = 0; c < _channels; c++)
            Array.Copy(buffer[c], bufferPosition, _stepBuffer[c], _stepBufferPosition, remaining);
        _stepBufferPosition = remaining;
    }

    /// <summary>Integrated loudness in LUFS; negative infinity when nothing passes the gates.</summary>
    public double GetIntegratedLoudness()
    {
        var absolute = _preceding.Where(x => x.Loudness > -70.0).ToArray();
        var relativeGate = Loudness(MeanSquareAverage(absolute)) - 10.0;
        var relative = absolute.Where(x => x.Loudness > relativeGate).ToArray();
        return Loudness(MeanSquareAverage(relative));
    }

    private static double MeanSquareAverage((double MeanSquare, double Loudness)[] blocks) =>
        blocks.Length == 0 ? 0.0 : blocks.Average(x => x.MeanSquare);

    /// <summary>Port of <c>SecondOrderIIRFilter</c>: coefficients given at 48 kHz, re-derived for other rates.</summary>
    private sealed class IirFilter
    {
        private readonly double _b0, _b1, _b2, _a1, _a2;
        private readonly double[] _z1, _z2;

        public IirFilter(double b0At48k, double b1At48k, double b2At48k, double a1At48k, double a2At48k,
            double sampleRate, int channels)
        {
            _z1 = new double[channels];
            _z2 = new double[channels];
            (_b0, _b1, _b2, _a1, _a2) = (b0At48k, b1At48k, b2At48k, a1At48k, a2At48k);
            if (sampleRate == 48_000.0) return;

            var kOverQ = (2.0 - 2.0 * a2At48k) / (a2At48k - a1At48k + 1.0);
            var k0 = Math.Sqrt((a1At48k + a2At48k + 1.0) / (a2At48k - a1At48k + 1.0));
            var q = k0 / kOverQ;
            var arctanK = Math.Atan(k0);
            var vb = (b0At48k - b2At48k) / (1.0 - a2At48k);
            var vh = (b0At48k - b1At48k + b2At48k) / (a2At48k - a1At48k + 1.0);
            var vl = (b0At48k + b1At48k + b2At48k) / (a1At48k + a2At48k + 1.0);

            var k = Math.Tan(arctanK * 48_000.0 / sampleRate);
            var common = 1.0 / (1.0 + k / q + k * k);
            _b0 = (vh + vb * k / q + vl * k * k) * common;
            _b1 = 2.0 * (vl * k * k - vh) * common;
            _b2 = (vh - vb * k / q + vl * k * k) * common;
            _a1 = 2.0 * (k * k - 1.0) * common;
            _a2 = (1.0 - k / q + k * k) * common;
        }

        public void Process(double[][] buffer)
        {
            for (var channel = 0; channel < _z1.Length; channel++)
            {
                var samples = buffer[channel];
                for (var i = 0; i < samples.Length; i++)
                {
                    var factorForB0 = samples[i] - _a1 * _z1[channel] - _a2 * _z2[channel];
                    var output = _b0 * factorForB0 + _b1 * _z1[channel] + _b2 * _z2[channel];
                    _z2[channel] = _z1[channel];
                    _z1[channel] = factorForB0;
                    samples[i] = output;
                }
            }
        }
    }
}
