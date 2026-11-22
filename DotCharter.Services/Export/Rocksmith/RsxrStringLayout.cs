namespace DotCharter.Services.Export.Rocksmith;

/// <summary>The positional string order and tuning bounds of RS Extended Range.</summary>
internal static class RsxrStringLayout
{
    public const int FormatVersion = 1;
    public const int MinTuningOffset = -24;
    public const int MaxTuningOffset = 24;

    public static bool IsExtended(Arrangement arrangement) => Rs7StringLayout.IsExtended(arrangement);

    public static int[] StandardMidi(ArrangementType type, int stringCount)
    {
        if (type == ArrangementType.Vocal || (type == ArrangementType.Bass ? stringCount is < 4 or > 6 : stringCount is < 6 or > 8))
            throw new ArgumentOutOfRangeException(nameof(stringCount), stringCount, "Unsupported instrument string count.");
        var standard = new Arrangement { ArrangementType = type, StringCount = stringCount };
        foreach (var step in ExtendedRangeTunings.StandardOffsets(stringCount, type == ArrangementType.Bass))
            standard.TuningPitches.Add(new TuningOffset(step));
        return [.. Enumerable.Range(0, stringCount).Select(i => SoundingPitch.OpenStringMidi(standard, i)!.Value)];
    }

    public static int SlotOf(int stringIndex, int stringCount, ArrangementType type) =>
        Rs7StringLayout.ToSng(stringIndex, stringCount, type);

    public static int StringOf(int slot, int stringCount, ArrangementType type) =>
        Rs7StringLayout.ToDomain(slot, stringCount, type);

    /// <summary>Offsets against the positional standard notes, preserving each sounding pitch.</summary>
    public static int[] TuningOffsets(Arrangement arrangement)
    {
        var standard = StandardMidi(arrangement.ArrangementType, arrangement.StringCount);
        return [.. Enumerable.Range(0, arrangement.StringCount).Select(i =>
            SoundingPitch.OpenStringMidi(arrangement, i)!.Value - standard[i])];
    }

    public static string? ExportRefusal(Arrangement arrangement)
    {
        if (Rs7StringLayout.ExportRefusal(arrangement) is { } refusal) return refusal;
        if (!IsExtended(arrangement)) return null;
        var offsets = TuningOffsets(arrangement);
        for (var i = 0; i < offsets.Length; i++)
        {
            if (offsets[i] is < MinTuningOffset or > MaxTuningOffset)
                return $"'{arrangement.Name}' has tuning offset {offsets[i]} on string {i}; RS Extended Range requires offsets from -24 to 24.";
        }

        return null;
    }

    public static string? XmlExportRefusal(Arrangement arrangement, bool sixStringProjection) =>
        sixStringProjection && Rs7StringLayout.IsExtendedGuitar(arrangement) ? null : ExportRefusal(arrangement);

}
