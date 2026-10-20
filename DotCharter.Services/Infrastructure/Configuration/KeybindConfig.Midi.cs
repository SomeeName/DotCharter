// Partial: MIDI controller bindings — verb → trigger, persisted in keybinds.json beside the chords.
using System.Reflection;

namespace DotCharter.Services.Infrastructure.Configuration;

public partial class KeybindConfig
{
    private static readonly HashSet<string> ChordVerbs = typeof(KeybindConfig)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.PropertyType == typeof(KeyChord) && p.CanRead && p.CanWrite)
        .Select(p => p.Name)
        .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    ///     The rows whose chord is a MODIFIER for a digit family (Shift+0..9 =
    ///     bookmark n, and so on): one row, ten verbs, so one pad cannot stand for
    ///     it. Every other <see cref="KeyChord" /> row is a single verb a MIDI
    ///     press can run.
    /// </summary>
    public static IReadOnlySet<string> MidiUnbindableVerbs { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(BookmarkBase), nameof(SetFretBase), nameof(StringMaskBase), nameof(PenToggleStringBase)
    };

    /// <summary>
    ///     The controller binds, keyed by the verb — the <see cref="KeyChord" />
    ///     property name, which is also the label the keybind dispatch registers
    ///     the verb's action under. A trigger belongs to at most one verb.
    ///     <para>
    ///         Settable for the JSON reader only; edit through
    ///         <see cref="SetMidiTrigger" /> and <see cref="ClearMidiBindings" />,
    ///         which announce the change the Keybinds tab and its unsaved counter follow.
    ///     </para>
    /// </summary>
    public Dictionary<string, MidiTrigger> MidiBindings { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Whether <paramref name="verb" /> is a row a MIDI trigger can be bound to.</summary>
    public static bool IsMidiBindable(string verb) => ChordVerbs.Contains(verb) && !MidiUnbindableVerbs.Contains(verb);

    public MidiTrigger GetMidiTrigger(string verb) =>
        MidiBindings.TryGetValue(verb, out var trigger) ? trigger : MidiTrigger.None;

    /// <summary>The verb <paramref name="trigger" /> runs, or null when nothing is bound to it.</summary>
    public string? FindMidiVerb(MidiTrigger trigger)
    {
        if (!trigger.IsBound) return null;
        foreach (var (verb, bound) in MidiBindings)
            if (bound == trigger) return verb;

        return null;
    }

    /// <summary>
    ///     Binds <paramref name="verb" /> to <paramref name="trigger" />, or unbinds it
    ///     for <see cref="MidiTrigger.None" />. A trigger runs ONE verb, so a verb that
    ///     already held it lets go — its name is returned, so the caller can say so.
    /// </summary>
    public string? SetMidiTrigger(string verb, MidiTrigger trigger)
    {
        if (!IsMidiBindable(verb))
            throw new ArgumentException($"'{verb}' is not a verb a MIDI trigger can run.", nameof(verb));

        string? displaced = null;
        if (trigger.IsBound)
        {
            displaced = FindMidiVerb(trigger);
            if (displaced == verb) return null;
            if (displaced is not null) MidiBindings.Remove(displaced);
            MidiBindings[verb] = trigger;
        }
        else if (!MidiBindings.Remove(verb))
        {
            return null;
        }

        OnPropertyChanged(nameof(MidiBindings));
        return displaced;
    }

    public void ClearMidiBindings()
    {
        if (MidiBindings.Count == 0) return;
        MidiBindings.Clear();
        OnPropertyChanged(nameof(MidiBindings));
    }

    /// <summary>
    ///     Drops what no row can show and no press can run: an unknown verb (a
    ///     renamed or removed row), a digit-family row, an unreadable trigger, and a
    ///     second verb on a trigger already taken (the first in file order keeps it).
    ///     Returns how many entries went.
    /// </summary>
    public int SanitizeMidiBindings()
    {
        var kept = new Dictionary<string, MidiTrigger>(StringComparer.Ordinal);
        var taken = new HashSet<MidiTrigger>();
        foreach (var (verb, trigger) in MidiBindings ?? [])
        {
            if (verb is null || !IsMidiBindable(verb) || !trigger.IsBound || !taken.Add(trigger)) continue;
            kept[verb] = trigger;
        }

        var dropped = (MidiBindings?.Count ?? 0) - kept.Count;
        MidiBindings = kept;
        return dropped;
    }
}
