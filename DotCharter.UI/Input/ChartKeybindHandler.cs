using System.Diagnostics;
using System.Linq.Expressions;
using Avalonia.Input;
using DotCharter.Services.Diagnostics;

namespace DotCharter.UI.Input;

public class ChartKeybindHandler(
    Func<bool> isInputBlocked,
    IAppLogger logger,
    IUsageTracker? usage = null) : IKeybindRegistry, DotCharter.Services.Infrastructure.Input.IKeybindDispatch
{
    private readonly Dictionary<string, Bind> _activeBinds = [];
    private readonly Dictionary<string, Action> _verbActions = new(StringComparer.Ordinal);

    /// <summary>
    ///     Every registration is a SET producer, so the fixed single-chord
    ///     registrations and the runtime-authored pattern binds go through one
    ///     path and one collision check.
    /// </summary>
    private readonly List<Func<IEnumerable<KeybindRegistration>>> _registrations = [];

    public void Register(Expression<Func<KeyChord>> chordLookup, Action action, string? label = null)
    {
        ArgumentNullException.ThrowIfNull(chordLookup);
        // Interpreted, not JIT-compiled: ~140 of these run at startup, and each
        // is read only on a RefreshBinds.
        var lookup = chordLookup.Compile(preferInterpretation: true);
        var name = LabelOf(chordLookup, action, label, () => lookup().ToString());
        _registrations.Add(() => [new KeybindRegistration(lookup(), action, name)]);
    }

    public void Register(KeyChord chord, Action action, string? label = null)
    {
        var name = LabelOf(null, action, label, chord.ToString);
        _registrations.Add(() => [new KeybindRegistration(chord, action, name)]);
    }

    public void Register(Expression<Func<Key>> keyLookup, Action action, string? label = null)
    {
        ArgumentNullException.ThrowIfNull(keyLookup);
        var lookup = keyLookup.Compile(preferInterpretation: true);
        var name = LabelOf(keyLookup, action, label, () => new KeyChord(lookup()).ToString());
        _registrations.Add(() => [new KeybindRegistration(new KeyChord(lookup()), action, name)]);
    }

    public void RegisterDynamic(Func<IEnumerable<KeybindRegistration>> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _registrations.Add(source);
    }

    public void RefreshBinds()
    {
        _activeBinds.Clear();
        _verbActions.Clear();
        var collisions = 0;

        foreach (var produce in _registrations)
        {
            foreach (var registration in produce())
            {
                // Every verb by its label, bound to a key or not: a MIDI bind
                // runs a verb whose chord the user cleared. The FIRST
                // registration of a label is the rebindable one (Key.Add
                // doubles SpeedUp under the same label, later).
                _verbActions.TryAdd(registration.Label, registration.Action);
                if (!registration.Chord.IsBound) continue;

                var hash = GetKeyHash(registration.Chord);
                if (_activeBinds.TryGetValue(hash, out var existing))
                {
                    collisions++;
                    var message = $"Keybind collision on {registration.Chord}: " +
                                  $"'{registration.Label}' overrides '{existing.Label}'.";
                    Debug.WriteLine(message);
                    logger.Warning(AppLogCategory.Settings, message);
                }

                _activeBinds[hash] = new Bind(registration.Action, registration.Label);
            }
        }

        logger.Info(AppLogCategory.Settings,
            $"Keybinds refreshed. Active binds={_activeBinds.Count}, collisions={collisions}.");
    }

    public string? DescribeBinding(KeyChord chord)
    {
        if (!chord.IsBound) return null;
        return _activeBinds.TryGetValue(GetKeyHash(chord), out var bind) ? bind.Label : null;
    }

    public bool HandleKeyPress(Key key, bool ctrl, bool shift, bool alt)
    {
        // Guard: ignore standalone modifier presses so Ctrl alone doesn't fire
        // a "Ctrl+None" lookup.
        if (key is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
        {
            return false;
        }

        if (isInputBlocked())
        {
            logger.Trace(AppLogCategory.Input,
                $"Key press blocked by open dialog/input. Key={key}, ctrl={ctrl}, shift={shift}, alt={alt}.");
            return false;
        }

        var hash = GetKeyHash(key, ctrl, shift, alt);
        if (_activeBinds.TryGetValue(hash, out var bind))
        {
            // Counted before the action runs, so a verb that opens a dialog or
            // throws still registers as used. This is the ONE dispatch point for
            // every editor shortcut, which is what makes the count complete.
            usage?.Record(UsageKind.Keybind, bind.Label);
            bind.Action.Invoke();
            return true;
        }

        return false;
    }

    /// <summary>
    ///     Runs the verb registered under <paramref name="label" /> — the
    ///     <c>KeybindConfig</c> property name — the way its chord would: blocked
    ///     by an open dialog, counted under the same id. The door a MIDI
    ///     controller's binds come through (<see cref="MidiBindingDispatch" />), so
    ///     a pad and a key run ONE implementation.
    /// </summary>
    public bool HandleVerb(string label)
    {
        if (!_verbActions.TryGetValue(label, out var action)) return false;
        if (isInputBlocked())
        {
            logger.Trace(AppLogCategory.Input, $"Verb '{label}' blocked by open dialog/input.");
            return false;
        }

        usage?.Record(UsageKind.Keybind, label);
        action.Invoke();
        return true;
    }

    /// <summary>
    ///     Every shortcut currently bound. The denominator for "which of my
    ///     keybinds do I never press" — a count alone cannot name the unused ones.
    /// </summary>
    public IReadOnlyList<string> KnownKeybindLabels =>
        _activeBinds.Values.Select(b => b.Label).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>
    ///     The id a registration is counted and reported under, in order of
    ///     preference: the explicit label; the config property the lookup reads
    ///     (<c>() => k.PlayPause</c> → <c>PlayPause</c>); a real method name for
    ///     a method-group action; and last the chord itself (<c>key:F12</c>).
    ///     A compiler-generated lambda name is never an answer — it names
    ///     nothing and renumbers whenever the registrar is edited.
    /// </summary>
    internal static string LabelOf(LambdaExpression? lookup, Action action, string? explicitLabel,
        Func<string> chordText)
    {
        if (!string.IsNullOrWhiteSpace(explicitLabel)) return explicitLabel.Trim();

        if (lookup?.Body is MemberExpression member) return member.Member.Name;

        var methodName = action.Method.Name;
        if (!string.IsNullOrEmpty(methodName) && !methodName.StartsWith('<')) return methodName;

        return "key:" + chordText();
    }

    private static string GetKeyHash(KeyChord chord) => GetKeyHash(
        chord.Key,
        chord.Modifiers.HasFlag(KeyModifiers.Control),
        chord.Modifiers.HasFlag(KeyModifiers.Shift),
        chord.Modifiers.HasFlag(KeyModifiers.Alt));

    private static string GetKeyHash(Key key, bool ctrl, bool shift, bool alt) =>
        $"{key}_{(ctrl ? "1" : "0")}_{(shift ? "1" : "0")}_{(alt ? "1" : "0")}";

    private readonly record struct Bind(Action Action, string Label);
}
