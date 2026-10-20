namespace DotCharter.Tone.Audio.Dsp;

/// <summary>Durable module-local control mapping. MIDI channels are zero based;
/// -1 disables a macro or the paired MIDI channel/controller. Ranges may reverse.</summary>
public sealed record RigControlAssignment(string ParameterId, int MacroIndex, float Min, float Max,
    int MidiChannel = -1, int MidiController = -1);

public static class RigControlAssignments
{
    public const int MaximumCount = 64;
    public static IReadOnlyList<RigControlAssignment> Copy(IReadOnlyList<RigControlAssignment>? source)
    {
        if (source is null || source.Count == 0) return Array.Empty<RigControlAssignment>();
        if (source.Count > MaximumCount) throw new ArgumentException("Too many control assignments.", nameof(source));
        var result = source.ToArray();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in result)
        {
            if (a is null || string.IsNullOrWhiteSpace(a.ParameterId) || a.ParameterId.Length > 128 ||
                !ids.Add(a.ParameterId) || !float.IsFinite(a.Min) || !float.IsFinite(a.Max) ||
                a.MacroIndex is < -1 or > 3 ||
                !((a.MidiChannel == -1 && a.MidiController == -1) ||
                  (a.MidiChannel is >= 0 and <= 15 && a.MidiController is >= 0 and <= 127)))
            {
                throw new ArgumentException("Invalid control assignment.", nameof(source));
            }
        }

        Array.Sort(result, (a, b) => StringComparer.Ordinal.Compare(a.ParameterId, b.ParameterId));
        return Array.AsReadOnly(result);
    }

    public static bool TryValidate(IReadOnlyList<RigControlAssignment>? source, DspModuleType? module,
        out IReadOnlyList<RigControlAssignment> result)
    {
        result = Array.Empty<RigControlAssignment>();
        if (module is null) return false;
        try { result = Copy(source); }
        catch (ArgumentException) { return false; }
        foreach (var a in result)
        {
            var p = module.Params.FirstOrDefault(p => p.Id == a.ParameterId);
            if (p is null || a.Min < p.Min || a.Min > p.Max || a.Max < p.Min || a.Max > p.Max)
                return false;
        }
        return true;
    }
}
