namespace DotCharter.Tone.Audio.Equalizer;

/// <summary>
///     The whole inspector EQ, as one mutable object: six bands, the master
///     power, the solo, the route and the output trim.
///     <para>
///         <b>This is the single source of truth.</b> The engine is pushed from
///         it, the drawn curve is computed from it, the "n/6" badge counts it and
///         the band chips render it — so none of those four can drift from the
///         others. The fault this exists to prevent has shipped in this codebase
///         before: a display model that agrees with nothing the DSP is doing.
///     </para>
///     <para>
///         Plain and observable-free on purpose. It is a MODEL, not a view
///         model; <c>EqInspectorViewModel</c> wraps it and raises the change
///         notifications. That keeps every rule below testable with no UI thread.
///     </para>
/// </summary>
public sealed class EqSection
{
    private readonly EqBand[] _bands;

    /// <summary>A section at the "Full range" preset: six bands parked at sane
    /// frequencies with every one of them OFF, which is exactly what the engine
    /// starts at, so a freshly built section and a freshly started engine agree
    /// before anything has been pushed.</summary>
    public EqSection() => _bands = [.. EqPresets.FullRange];

    /// <summary>The six bands, in plot order (low to high by default, though
    /// nothing stops a user dragging band 5 below band 1).</summary>
    public IReadOnlyList<EqBand> Bands => _bands;

    /// <summary>Master power. False = the section is bypassed and the curve is
    /// flat, whatever the per-band enables say.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>The soloed band's index, or −1 for "no solo". While a band is
    /// soloed it is the ONLY one applied.</summary>
    public int SoloedBand { get; private set; } = -1;

    /// <summary>Which bus the bands run on.</summary>
    public EqRoute Route { get; set; } = EqRoute.Song;

    /// <summary>Output trim in dB, applied after the bands. Clamped on write.</summary>
    public double TrimDb
    {
        get;
        set => field = Math.Clamp(value, -EqLimits.MaxTrimDb, EqLimits.MaxTrimDb);
    }

    /// <summary>The name of the preset currently loaded, or null once the user
    /// has changed anything — which is what makes the header read "Custom".</summary>
    public string? PresetKey { get; private set; } = EqPresets.FullRangeKey;

    /// <summary>
    ///     The bands the ENGINE will actually run, in the same order it runs
    ///     them: nothing while the section is off, only the soloed band while one
    ///     is soloed, else every enabled band.
    ///     <para>
    ///         The curve is drawn from exactly this list, so "what I see" and
    ///         "what I hear" are the same predicate evaluated once.
    ///     </para>
    /// </summary>
    public IReadOnlyList<EqBand> AudibleBands
    {
        get
        {
            var audible = new List<EqBand>(_bands.Length);
            CopyAudibleBandsTo(audible);
            return audible;
        }
    }

    /// <summary>
    ///     The SAME predicate as <see cref="AudibleBands" />, written into a
    ///     caller-owned list (cleared first) so a per-frame reader — the plot,
    ///     repainting at ~15 Hz — does not allocate a list and a LINQ chain per
    ///     frame. <see cref="AudibleBands" /> is defined in terms of this method,
    ///     so the two cannot disagree.
    /// </summary>
    public void CopyAudibleBandsTo(List<EqBand> into)
    {
        into.Clear();
        if (!IsEnabled) return;
        if (SoloedBand >= 0 && SoloedBand < _bands.Length)
        {
            into.Add(_bands[SoloedBand]);
            return;
        }

        foreach (var band in _bands)
        {
            if (band.Enabled)
                into.Add(band);
        }
    }

    /// <summary>How many bands are audible — the number behind the "n/6" badge.
    /// Derived from <see cref="AudibleBands" /> rather than counted separately,
    /// which is the whole point.</summary>
    public int ActiveCount => AudibleBands.Count;

    /// <summary>Replace one band. Every value is clamped to
    /// <see cref="EqLimits" /> first, and the section stops calling itself a
    /// preset.</summary>
    public void SetBand(int index, EqBand band)
    {
        if (index < 0 || index >= _bands.Length) return;
        _bands[index] = band.Clamped();
        PresetKey = null;
    }

    /// <summary>Switch one band on or off. NOT a preset change in itself —
    /// muting a band to hear what it does should not relabel the preset — but it
    /// is a section change, so it does dirty the curve.</summary>
    public void SetBandEnabled(int index, bool enabled)
    {
        if (index < 0 || index >= _bands.Length) return;
        _bands[index] = _bands[index] with { Enabled = enabled };
    }

    /// <summary>Solo a band, or pass the same index again (or −1) to clear it.
    /// Exactly one band can be soloed: soloing two is a mixer feature this
    /// section deliberately does not have, because the engine's own solo is a
    /// single index.</summary>
    public void ToggleSolo(int index)
    {
        if (index < 0 || index >= _bands.Length) { SoloedBand = -1; return; }
        SoloedBand = SoloedBand == index ? -1 : index;
    }

    /// <summary>Put one band back to a flat, neutral setting — the double-click
    /// gesture. Keeps its type, its frequency and its enabled state; only the
    /// two things a drag moves are reset.</summary>
    public void ResetBand(int index)
    {
        if (index < 0 || index >= _bands.Length) return;
        _bands[index] = _bands[index] with { GainDb = 0.0, Q = EqLimits.DefaultQ };
        PresetKey = null;
    }

    /// <summary>Load a named preset. Unknown keys are ignored rather than
    /// throwing — a preset list that grew in one release and shrank in the next
    /// must not take the inspector down.</summary>
    public void LoadPreset(string key)
    {
        var preset = EqPresets.ByKey(key);
        if (preset is null) return;

        for (var i = 0; i < _bands.Length && i < preset.Count; i++) _bands[i] = preset[i];
        SoloedBand = -1;
        PresetKey = key;
    }
}
