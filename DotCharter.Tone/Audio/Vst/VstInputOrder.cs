namespace DotCharter.Tone.Audio.Vst;

/// <summary>Native input attachment appends; detach the whole chain before rebuilding its order.</summary>
internal static class VstInputOrder
{
    public static void Apply<T>(IReadOnlyList<(T Plugin, int Player, bool BypassIntent)> slots,
        Action<T> detach, Action<T, int> attach)
    {
        foreach (var slot in slots) detach(slot.Plugin);
        foreach (var slot in slots)
        {
            if (!slot.BypassIntent) attach(slot.Plugin, slot.Player);
        }
    }
}
