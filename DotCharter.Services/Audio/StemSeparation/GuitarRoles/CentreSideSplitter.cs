using JuceSharp.Dsp;

namespace DotCharter.Services.Audio.StemSeparation.GuitarRoles;

/// <summary>
///     Splits a stereo guitar stem into what sits in the CENTRE (Lead) and what
///     is panned to the SIDES (Rhythm), offline, by a per-bin soft mask in the
///     STFT domain. Streaming, so a whole song never has to be in memory.
///     <para>
///         <b>The model.</b> In each time-frequency bin a centred source is the
///         part of the two channels that is the SAME — in phase and correlated
///         over time — while double-tracked rhythm guitars hard left and right
///         are two different performances, uncorrelated with each other and
///         with the lead. Per bin, with the auto- and cross-spectra smoothed over
///         ±<see cref="DefaultSmoothFrames" /> frames and ±<see cref="DefaultSmoothBins" /> bins
///         (one frame says nothing about correlation), the in-phase cross power
///         <c>Re(Φ_LR)</c> estimates the centre component's power. The lead mask of
///         each channel is that power over the channel's own power — the
///         channel's Wiener gain for "the centre" — and the rhythm mask is its
///         complement. The two per-channel ratios ARE the bin's pan estimate: a
///         coherent source panned partly left keeps its centred part in Lead and
///         sends the left-side excess to Rhythm.
///     </para>
///     <para>
///         <b>The bias floor.</b> A finite average of two UNcorrelated channels
///         still shows some cross power, which would leak rhythm into lead as
///         musical noise. <see cref="DefaultCoherenceFloor" /> is subtracted from the
///         normalised cross power and the rest rescaled, so a perfectly centred
///         bin still gets a mask of 1 and a bin whose coherence is within the
///         estimator's noise gets 0.
///     </para>
///     <para>
///         <b>Perfect reconstruction.</b> Both outputs are synthesised from the
///         same frames with complementary masks (<c>m</c> and <c>1 - m</c>) and a
///         Hann analysis + synthesis window at 75 % overlap, normalised per
///         hop phase, so <c>Lead + Rhythm</c> reproduces the input to float
///         precision. Nothing is lost or invented by the split.
///     </para>
///     <para>
///         Offline DSP on a worker thread. It is never on the audio callback: the
///         engine plays the WAVs this produces like any other stem.
///     </para>
/// </summary>
public sealed class CentreSideSplitter
{
    public const int FrameSize = 4096;
    public const int Hop = FrameSize / 4;
    public const int DefaultSmoothFrames = 3;
    public const int DefaultSmoothBins = 1;
    public const float DefaultCoherenceFloor = 0.3f;

    private const int Bins = FrameSize / 2 + 1;
    private const float Silence = 1e-12f;

    private readonly int _smoothFrames;
    private readonly int _smoothBins;
    private readonly float _coherenceFloor;
    private readonly int _ringSize;

    private readonly JuceFft _fft = new(FrameSize);
    private readonly float[] _window = new float[FrameSize];
    private readonly float[] _olaNorm = new float[Hop];

    // Analysis input: positions [s_t, s_t + N) of the frame being filled.
    private readonly float[] _inL = new float[FrameSize];
    private readonly float[] _inR = new float[FrameSize];
    private int _fill;

    private readonly FrameSlot[] _ring;
    private long _analysed;

    // Smoothed statistics for the frame being masked.
    private readonly float[] _sLl = new float[Bins];
    private readonly float[] _sRr = new float[Bins];
    private readonly float[] _sLr = new float[Bins];
    private readonly float[] _tmp = new float[Bins];

    // FFT scratch.
    private readonly float[] _re = new float[FrameSize];
    private readonly float[] _im = new float[FrameSize];
    private readonly float[] _re2 = new float[FrameSize];
    private readonly float[] _im2 = new float[FrameSize];

    // Overlap-add accumulators, positions [s_t, s_t + N) of the frame being synthesised.
    private readonly float[] _olaLeadL = new float[FrameSize];
    private readonly float[] _olaLeadR = new float[FrameSize];
    private readonly float[] _olaRhyL = new float[FrameSize];
    private readonly float[] _olaRhyR = new float[FrameSize];

    private readonly SampleQueue _outLeadL = new();
    private readonly SampleQueue _outLeadR = new();
    private readonly SampleQueue _outRhyL = new();
    private readonly SampleQueue _outRhyR = new();

    private long _pushed;
    private long _emitted;
    private long _skip = FrameSize - Hop;
    private bool _completed;

    private double _leadEnergy;
    private double _totalEnergy;

    /// <param name="smoothFrames">Statistics are averaged over ±this many frames.</param>
    /// <param name="smoothBins">…and over ±this many bins.</param>
    /// <param name="coherenceFloor">Normalised cross power treated as estimator noise.</param>
    public CentreSideSplitter(int smoothFrames = DefaultSmoothFrames, int smoothBins = DefaultSmoothBins,
        float coherenceFloor = DefaultCoherenceFloor)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(smoothFrames);
        ArgumentOutOfRangeException.ThrowIfNegative(smoothBins);
        if (coherenceFloor is < 0f or >= 1f) throw new ArgumentOutOfRangeException(nameof(coherenceFloor));
        _smoothFrames = smoothFrames;
        _smoothBins = smoothBins;
        _coherenceFloor = coherenceFloor;
        _ringSize = 2 * smoothFrames + 1;
        _ring = new FrameSlot[_ringSize];
        for (var n = 0; n < FrameSize; n++)
            _window[n] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * n / FrameSize));
        for (var j = 0; j < Hop; j++)
        {
            double sum = 0;
            for (var m = j; m < FrameSize; m += Hop) sum += _window[m] * _window[m];
            _olaNorm[j] = (float)sum;
        }

        for (var i = 0; i < _ringSize; i++) _ring[i] = new FrameSlot();
    }

    /// <summary>Share of the input's energy the centre mask assigned to Lead (0..1).</summary>
    public double LeadShare => _totalEnergy > 0 ? _leadEnergy / _totalEnergy : 0;

    /// <summary>Samples ready to <see cref="Read" />.</summary>
    public int Available => _outLeadL.Count;

    /// <summary>Feeds the next block of the input. Both channels the same length.</summary>
    public void Push(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        if (_completed) throw new InvalidOperationException("The splitter has already been completed.");
        if (left.Length != right.Length)
            throw new ArgumentException("Both channels must hold the same number of frames.", nameof(right));

        _pushed += left.Length;
        var offset = 0;
        while (offset < left.Length)
        {
            var take = Math.Min(Hop - _fill, left.Length - offset);
            left.Slice(offset, take).CopyTo(_inL.AsSpan(FrameSize - Hop + _fill));
            right.Slice(offset, take).CopyTo(_inR.AsSpan(FrameSize - Hop + _fill));
            _fill += take;
            offset += take;
            if (_fill == Hop) AnalyseFrame();
        }
    }

    /// <summary>
    ///     Ends the input: pads with silence until every pushed sample has been
    ///     synthesised. After this, <see cref="Available" /> counts the rest of the
    ///     output, which is exactly as long as the input.
    /// </summary>
    public void Complete()
    {
        if (_completed) return;
        _completed = true;
        while (_emitted < _pushed)
        {
            _inL.AsSpan(FrameSize - Hop + _fill).Clear();
            _inR.AsSpan(FrameSize - Hop + _fill).Clear();
            _fill = Hop;
            AnalyseFrame();
        }
    }

    /// <summary>Drains up to the destination length of finished output; returns the count.</summary>
    public int Read(Span<float> leadL, Span<float> leadR, Span<float> rhythmL, Span<float> rhythmR)
    {
        var n = Math.Min(Available, Math.Min(Math.Min(leadL.Length, leadR.Length), Math.Min(rhythmL.Length, rhythmR.Length)));
        _outLeadL.Dequeue(leadL[..n]);
        _outLeadR.Dequeue(leadR[..n]);
        _outRhyL.Dequeue(rhythmL[..n]);
        _outRhyR.Dequeue(rhythmR[..n]);
        return n;
    }

    /// <summary>Whole-buffer convenience over the streaming API.</summary>
    public static (float[] LeadL, float[] LeadR, float[] RhythmL, float[] RhythmR, double LeadShare) SplitAll(
        float[] left, float[] right, CentreSideSplitter? splitter = null)
    {
        splitter ??= new CentreSideSplitter();
        splitter.Push(left, right);
        splitter.Complete();
        var leadL = new float[left.Length];
        var leadR = new float[left.Length];
        var rhyL = new float[left.Length];
        var rhyR = new float[left.Length];
        var read = splitter.Read(leadL, leadR, rhyL, rhyR);
        if (read != left.Length)
            throw new InvalidOperationException($"Splitter produced {read} samples for {left.Length}.");
        return (leadL, leadR, rhyL, rhyR, splitter.LeadShare);
    }

    private void AnalyseFrame()
    {
        var slot = _ring[(int)(_analysed % _ringSize)];

        // Two real channels through ONE complex transform: z = l + i r.
        for (var n = 0; n < FrameSize; n++)
        {
            _re[n] = _inL[n] * _window[n];
            _im[n] = _inR[n] * _window[n];
        }

        _fft.Forward(_re, _im);
        for (var k = 0; k < Bins; k++)
        {
            var nk = (FrameSize - k) & (FrameSize - 1);
            float a = _re[k], b = _im[k], c = _re[nk], d = _im[nk];
            var lRe = 0.5f * (a + c);
            var lIm = 0.5f * (b - d);
            var rRe = 0.5f * (b + d);
            var rIm = 0.5f * (c - a);
            slot.LRe[k] = lRe;
            slot.LIm[k] = lIm;
            slot.RRe[k] = rRe;
            slot.RIm[k] = rIm;
            slot.Pll[k] = lRe * lRe + lIm * lIm;
            slot.Prr[k] = rRe * rRe + rIm * rIm;
            slot.Clr[k] = lRe * rRe + lIm * rIm; // Re(L · conj(R))
        }

        _analysed++;

        // Shift the analysis window by one hop.
        Array.Copy(_inL, Hop, _inL, 0, FrameSize - Hop);
        Array.Copy(_inR, Hop, _inR, 0, FrameSize - Hop);
        _fill = 0;

        // Frame t is masked once frame t + K is in the ring.
        var t = _analysed - 1 - _smoothFrames;
        // The first K frames wait for their look-ahead; the overlap-add buffer still
        // starts at frame 0's positions, so the delay never shifts the output.
        if (t >= 0) SynthesiseFrame(t);
    }

    private void SynthesiseFrame(long t)
    {
        var slot = _ring[(int)(t % _ringSize)];
        SmoothStatistics(t);

        double frameLead = 0, frameTotal = 0;
        for (var k = 0; k < Bins; k++)
        {
            float pll = _sLl[k], prr = _sRr[k];
            var g = MathF.Sqrt(pll * prr);
            var centre = (_sLr[k] - _coherenceFloor * g) / (1f - _coherenceFloor);
            float mL = 0f, mR = 0f;
            if (centre > 0f)
            {
                if (pll > Silence) mL = Math.Min(1f, centre / pll);
                if (prr > Silence) mR = Math.Min(1f, centre / prr);
            }

            float lRe = slot.LRe[k], lIm = slot.LIm[k], rRe = slot.RRe[k], rIm = slot.RIm[k];

            // Lead into (_re, _im) packed as A + iB, rhythm into (_re2, _im2).
            float aRe = mL * lRe, aIm = mL * lIm, bRe = mR * rRe, bIm = mR * rIm;
            float cRe = lRe - aRe, cIm = lIm - aIm, dRe = rRe - bRe, dIm = rIm - bIm;
            _re[k] = aRe - bIm;
            _im[k] = aIm + bRe;
            _re2[k] = cRe - dIm;
            _im2[k] = cIm + dRe;
            if (k is > 0 and < Bins - 1)
            {
                var nk = FrameSize - k;
                _re[nk] = aRe + bIm;
                _im[nk] = bRe - aIm;
                _re2[nk] = cRe + dIm;
                _im2[nk] = dRe - cIm;
            }

            var weight = k is 0 or Bins - 1 ? 1.0 : 2.0;
            frameLead += weight * (aRe * aRe + aIm * aIm + bRe * bRe + bIm * bIm);
            frameTotal += weight * (slot.Pll[k] + slot.Prr[k]);
        }

        _leadEnergy += frameLead;
        _totalEnergy += frameTotal;

        _fft.Inverse(_re, _im);
        _fft.Inverse(_re2, _im2);
        for (var n = 0; n < FrameSize; n++)
        {
            var w = _window[n];
            _olaLeadL[n] += _re[n] * w;
            _olaLeadR[n] += _im[n] * w;
            _olaRhyL[n] += _re2[n] * w;
            _olaRhyR[n] += _im2[n] * w;
        }

        // Positions [s_t, s_t + H) are now final: no later frame starts before s_t + H.
        EmitHop();
    }

    private void EmitHop()
    {
        var start = 0;
        if (_skip > 0)
        {
            start = (int)Math.Min(_skip, Hop);
            _skip -= start;
        }

        var remaining = _pushed - _emitted;
        var count = (int)Math.Max(0, Math.Min(Hop - start, remaining));
        for (var j = start; j < start + count; j++)
        {
            var norm = _olaNorm[j];
            _outLeadL.Enqueue(_olaLeadL[j] / norm);
            _outLeadR.Enqueue(_olaLeadR[j] / norm);
            _outRhyL.Enqueue(_olaRhyL[j] / norm);
            _outRhyR.Enqueue(_olaRhyR[j] / norm);
        }

        _emitted += count;

        ShiftOla(_olaLeadL);
        ShiftOla(_olaLeadR);
        ShiftOla(_olaRhyL);
        ShiftOla(_olaRhyR);
    }

    private static void ShiftOla(float[] ola)
    {
        Array.Copy(ola, Hop, ola, 0, FrameSize - Hop);
        Array.Clear(ola, FrameSize - Hop, Hop);
    }

    private void SmoothStatistics(long t)
    {
        Array.Clear(_sLl);
        Array.Clear(_sRr);
        Array.Clear(_sLr);
        for (var j = t - _smoothFrames; j <= t + _smoothFrames; j++)
        {
            if (j < 0 || j >= _analysed) continue;
            var s = _ring[(int)(j % _ringSize)];
            for (var k = 0; k < Bins; k++)
            {
                _sLl[k] += s.Pll[k];
                _sRr[k] += s.Prr[k];
                _sLr[k] += s.Clr[k];
            }
        }

        SmoothAcrossBins(_sLl);
        SmoothAcrossBins(_sRr);
        SmoothAcrossBins(_sLr);
    }

    private void SmoothAcrossBins(float[] values)
    {
        Array.Copy(values, _tmp, Bins);
        for (var k = 0; k < Bins; k++)
        {
            float sum = 0;
            for (var d = -_smoothBins; d <= _smoothBins; d++)
            {
                var i = k + d;
                if (i is >= 0 and < Bins) sum += _tmp[i];
            }

            values[k] = sum;
        }
    }

    private sealed class FrameSlot
    {
        public readonly float[] LRe = new float[Bins];
        public readonly float[] LIm = new float[Bins];
        public readonly float[] RRe = new float[Bins];
        public readonly float[] RIm = new float[Bins];
        public readonly float[] Pll = new float[Bins];
        public readonly float[] Prr = new float[Bins];
        public readonly float[] Clr = new float[Bins];
    }

    /// <summary>A growable FIFO of samples; the splitter emits a hop at a time and the
    /// caller drains between pushes, so it stays a few hops long.</summary>
    private sealed class SampleQueue
    {
        private float[] _buffer = new float[4 * Hop];
        private int _head;
        private int _count;

        public int Count => _count;

        public void Enqueue(float value)
        {
            if (_head + _count == _buffer.Length)
            {
                if (_head > 0)
                {
                    Array.Copy(_buffer, _head, _buffer, 0, _count);
                    _head = 0;
                }

                if (_count == _buffer.Length) Array.Resize(ref _buffer, _buffer.Length * 2);
            }

            _buffer[_head + _count] = value;
            _count++;
        }

        public void Dequeue(Span<float> destination)
        {
            _buffer.AsSpan(_head, destination.Length).CopyTo(destination);
            _head += destination.Length;
            _count -= destination.Length;
            if (_count == 0) _head = 0;
        }
    }
}
