namespace DotCharter.Tone.Audio.Dsp.Tones;

/// <summary>
///     The tone half of <see cref="ArrangementInstrument.IsBassPart(Arrangement, Func{bool})" />:
///     whether an arrangement's tones are BASS tones, for a caller that has
///     the project. It lives here and not in Core because it parses the tone bodies.
///     <para>
///         A tone is a bass tone when its amp is bass gear (the <c>Bass_</c> key
///         prefix the game's own gear list uses) AND it carries the
///         <c>$[35715]BASS</c> descriptor. Either alone is not enough in the owner's
///         CDLC library: guitar parts with one bass-amp tone among guitar ones
///         (a synth-bass effect, a fuzz bass solo), a guitar solo built on a bass
///         head with CLEAN / ECHO descriptors, and B-standard guitar parts tagged
///         BASS on a Marshall all exist. EVERY tone the arrangement declares and the
///         project owns a body for must qualify, and there must be at least one.
///     </para>
/// </summary>
public static class ArrangementBassTones
{
    /// <summary>The descriptor a bass tone carries (the manifest's <c>UIName</c>).</summary>
    public const string BassDescriptor = "$[35715]BASS";

    /// <summary>The key prefix of the game's bass amps and cabinets.</summary>
    public const string BassGearPrefix = "Bass_";

    /// <summary>True when <paramref name="tone" /> is a bass tone (bass amp + BASS descriptor).</summary>
    /// <param name="tone">A parsed tone.</param>
    /// <returns>True for a bass tone.</returns>
    public static bool IsBassTone(RsTone tone)
    {
        ArgumentNullException.ThrowIfNull(tone);
        return tone.Amp.GearKey.StartsWith(BassGearPrefix, StringComparison.Ordinal) &&
               tone.ToneDescriptors.Any(d => string.Equals(d, BassDescriptor, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     True when every tone <paramref name="arrangement" /> declares that
    ///     <paramref name="project" /> owns a parsable body for is a bass tone, and
    ///     there is at least one. Name matching is <see cref="ChartToneNames" />'s
    ///     (trimmed, case-insensitive, by key or display name).
    /// </summary>
    /// <param name="project">The project that owns the tone bodies.</param>
    /// <param name="arrangement">The arrangement whose tones are asked about.</param>
    /// <returns>True when its tones are bass gear.</returns>
    public static bool AreBassGear(ChartProject project, Arrangement arrangement)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(arrangement);

        var any = false;
        foreach (var name in ChartToneNames.Declared(arrangement))
        {
            var body = project.ToneDefinitions.FirstOrDefault(t => Matches(t.Key, name) || Matches(t.Name, name));
            if (body is null || !RsToneJsonParser.TryParse(body.Json, out var tone) || tone is null) continue;
            if (!IsBassTone(tone)) return false;
            any = true;
        }

        return any;
    }

    /// <summary>
    ///     <see cref="ArrangementInstrument.IsBassPart(Arrangement, Func{bool})" /> with
    ///     the tone signal filled in from <paramref name="project" /> (null = the
    ///     arrangement-only rule). The tones are parsed only when the path and the
    ///     tuning reference have not already answered.
    /// </summary>
    /// <param name="project">The project that owns the tone bodies, or null.</param>
    /// <param name="arrangement">The arrangement.</param>
    /// <returns>True for a bass part.</returns>
    public static bool IsBassPart(ChartProject? project, Arrangement arrangement) =>
        ArrangementInstrument.IsBassPart(arrangement,
            project is null ? null : () => AreBassGear(project, arrangement));

    private static bool Matches(string? candidate, string trimmedName) =>
        !string.IsNullOrWhiteSpace(candidate) &&
        string.Equals(candidate.Trim(), trimmedName, StringComparison.OrdinalIgnoreCase);
}
