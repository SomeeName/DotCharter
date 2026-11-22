namespace DotCharter.Services.Export.Rocksmith;

/// <summary>
///     The low bass tuning fix. Rocksmith 2014 does not register a bass note below C1 (MIDI 24),
///     so a stock four-string bass in B standard or lower, or a drop tuning whose low string falls under
///     C1, is unplayable as charted. The community fix (Customs Forge's "low bass tuning fix", RS Toolkit's
///     "Fix Low Bass Tuning") writes the tuning an octave up (+12 on every string) with the tuning pitch at
///     A220, cent offset −1200, so the game reads the low strings an octave higher and the song still sounds
///     where it was charted. The SNG's tuning, and with it the manifest and <c>.hsan</c> <c>Tuning</c>, take
///     the raised offsets; the manifest's <c>CentOffset</c> takes the lowered reference. It is the same
///     encoding <see cref="ArrangementInstrument" /> reads back as an octave-down reference (a Bass path at
///     A220 tuned <c>7 7 7 7</c> is B standard).
///     <para>
///         It applies to a COPY on the export side only, never to the project. Only the Bass path qualifies:
///         a guitar never gets it, and a five-/six-string bass is RS Extended Range, whose format spells its
///         low B0 string itself, so its SNXR record stays unchanged. A chart already at an octave-down
///         reference is left alone (the fix is in it already).
///     </para>
/// </summary>
public static class LowBassTuningFix
{
    /// <summary>C1, the lowest pitch Rocksmith 2014 registers on a bass.</summary>
    public const int LowestRegisteredMidi = 24;

    /// <summary>The tuning offset added to every string.</summary>
    public const int OctaveSemitones = 12;

    /// <summary>The cent offset subtracted from the tuning reference: A440 becomes A220.</summary>
    public const double OctaveCents = 1200.0;

    /// <summary>The lowest open string's sounding pitch, or null when no string is priced.
    ///     Every played note is at or above its open string, so this is also the lowest played pitch.</summary>
    public static int? LowestPitch(Arrangement arrangement)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        var count = arrangement.StringCount > 0 ? arrangement.StringCount : 4;
        int? lowest = null;
        for (var i = 0; i < count; i++)
        {
            if (SoundingPitch.OpenSoundingMidi(arrangement, i) is { } midi && (lowest is null || midi < lowest))
                lowest = midi;
        }
        return lowest;
    }

    /// <summary>Whether this arrangement's stock package copy needs the fix.</summary>
    public static bool Needs(Arrangement arrangement) =>
        arrangement.ArrangementType == ArrangementType.Bass
        && !Rs7StringLayout.IsExtendedBass(arrangement)
        && !ArrangementInstrument.IsOctaveDownReference(arrangement.CentOffset)
        && LowestPitch(arrangement) is < LowestRegisteredMidi;

    /// <summary>A copy with every tuning offset an octave up and the reference an octave down; the source is untouched.</summary>
    public static Arrangement Apply(Arrangement arrangement)
    {
        var offsets = TuningOffsetResolution.Resolve(arrangement);
        var copy = ChartProjectCloner.CloneArrangement(arrangement, retainIdentities: true);
        copy.TuningPitches = [.. offsets.Select(o => new TuningOffset(o + OctaveSemitones))];
        copy.CentOffset = arrangement.CentOffset - OctaveCents;
        return copy;
    }

    /// <summary>The copy the package writes: fixed when <paramref name="enabled" /> and needed, else the arrangement itself.</summary>
    public static Arrangement ForPackage(Arrangement arrangement, bool enabled) =>
        enabled && Needs(arrangement) ? Apply(arrangement) : arrangement;

    /// <summary>The names of the arrangements <paramref name="selected" /> keeps that need the fix, in project order.</summary>
    public static IReadOnlyList<string> NeedingFix(ChartProject project, Func<Arrangement, bool> selected) =>
        [.. project.Arrangements.Where(a => selected(a) && Needs(a)).Select(a => a.Name)];
}
