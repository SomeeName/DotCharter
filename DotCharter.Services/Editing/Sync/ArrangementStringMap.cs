namespace DotCharter.Services.Editing.Sync;

/// <summary>
///     Which TARGET string plays what a SOURCE string plays: the one with the same
///     open pitch. A 7-string in drop A and a 6-string in drop D share their five
///     upper strings at different indices (A2 is string 2 on the seven, string 1 on
///     the six); a 5-string bass B E A D G and a 4-string E A D G share four.
///     <para>
///         The open pitch is the SOUNDING one (<see cref="SoundingPitch.OpenSoundingMidi" />
///         plus the residual cents of the tuning reference), before any capo: the capo
///         stops both strings at the same absolute fret, so it is checked per note
///         (<see cref="SoundsAlike" />), never folded into the string identity.
///     </para>
/// </summary>
internal sealed class ArrangementStringMap
{
    private readonly Arrangement _source;
    private readonly Arrangement _target;
    private readonly int[] _map;

    private ArrangementStringMap(Arrangement source, Arrangement target, int[] map, bool isIdentity)
    {
        _source = source;
        _target = target;
        _map = map;
        IsIdentity = isIdentity;
    }

    /// <summary>
    ///     True when the two arrangements have the same strings tuned the same way, so a
    ///     string index means the same thing in both. That is the index sync, kept exactly as it was.
    /// </summary>
    public bool IsIdentity { get; }

    public static ArrangementStringMap Between(Arrangement source, Arrangement target)
    {
        var sourceCount = Math.Max(0, source.StringCount);
        var targetCount = Math.Max(0, target.StringCount);
        var targetOpen = new long?[targetCount];
        for (var t = 0; t < targetCount; t++) targetOpen[t] = OpenCents(target, t);

        var map = new int[sourceCount];
        var identity = sourceCount == targetCount;
        for (var s = 0; s < sourceCount; s++)
        {
            map[s] = -1;
            if (OpenCents(source, s) is not { } open) continue;

            // Top-aligned first: the strings an extended-range neck ADDS are the low ones.
            var aligned = s + targetCount - sourceCount;
            if (aligned >= 0 && aligned < targetCount && targetOpen[aligned] == open)
            {
                map[s] = aligned;
                continue;
            }

            map[s] = Array.IndexOf(targetOpen, open);
        }

        for (var s = 0; s < sourceCount && identity; s++) identity = map[s] == s;
        return new ArrangementStringMap(source, target, map, identity);
    }

    /// <summary>The target string with <paramref name="sourceString" />'s open pitch, or false when none has it.</summary>
    public bool TryMap(int sourceString, out int targetString)
    {
        targetString = sourceString >= 0 && sourceString < _map.Length ? _map[sourceString] : -1;
        return targetString >= 0;
    }

    /// <summary>
    ///     Whether the source's (string, fret) and the target's (string, SAME fret) sound the
    ///     same pitch — false when a capo differs under an open string, or either is unpriced.
    /// </summary>
    public bool SoundsAlike(int sourceString, int targetString, int fret) =>
        SoundingPitch.SoundingMidiFor(_source, sourceString, fret) is { } a &&
        SoundingPitch.SoundingMidiFor(_target, targetString, fret) is { } b &&
        a == b;

    /// <summary>
    ///     A copy of <paramref name="template" /> on the target's strings (frets, fingers and
    ///     ghost bits moved with their string), with no id; <c>null</c> when any fretted string
    ///     has no target twin. Never touches <paramref name="template" /> — it is shared.
    /// </summary>
    public ChordTemplate? Remap(ChordTemplate template)
    {
        var copy = template.Clone();
        copy.Id = -1;
        copy.Frets = [];
        copy.Fingers = [];
        copy.GhostMask = 0;
        foreach (var (stringIndex, fret) in template.Frets)
        {
            if (!TryMap(stringIndex, out var t)) return null;
            copy.Frets[t] = fret;
            if (template.IsGhost(stringIndex)) copy.GhostMask |= ChordTemplate.GhostBit(t);
        }

        foreach (var (stringIndex, finger) in template.Fingers)
        {
            if (TryMap(stringIndex, out var t) && copy.Frets.ContainsKey(t)) copy.Fingers[t] = finger;
        }

        return copy;
    }

    private static long? OpenCents(Arrangement arrangement, int stringIndex) =>
        SoundingPitch.OpenSoundingMidi(arrangement, stringIndex) is { } midi
            ? midi * 100L + (long)Math.Round(ArrangementInstrument.ResidualCents(arrangement.CentOffset))
            : null;
}
