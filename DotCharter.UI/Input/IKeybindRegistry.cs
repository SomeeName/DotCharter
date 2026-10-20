using System.Linq.Expressions;
using Avalonia.Input;

namespace DotCharter.UI.Input;

/// <summary>
///     One chord-to-action binding produced by a registration. The
///     <see cref="Label" /> exists so a collision can name BOTH sides: the
///     registry resolves a clash last-registration-wins with only a log line, so
///     "something silently overrode something else" is otherwise invisible.
///     <para>
///         It is also the id the usage statistics count the shortcut under, so
///         it must be STABLE and READABLE: the <c>KeybindConfig</c> property name
///         for a rebindable chord (<c>PlayPause</c>), the verb for a fixed one.
///         Never a delegate's method name — a lambda's is
///         <c>&lt;RegisterAll&gt;b__53</c>, which names nothing and changes
///         whenever the registrar is edited.
///     </para>
/// </summary>
/// <param name="Chord">The chord to claim; an unbound chord is skipped.</param>
/// <param name="Action">What to run.</param>
/// <param name="Label">Human/diagnostic name of the owner.</param>
public readonly record struct KeybindRegistration(KeyChord Chord, Action Action, string Label);

public interface IKeybindRegistry
{
    /// <summary>
    ///     Registers an action against a user-rebindable <see cref="KeyChord" />
    ///     lookup. The lookup is evaluated at <see cref="RefreshBinds" /> time so
    ///     re-binding a chord in the settings dialog instantly takes effect.
    ///     <para>
    ///         An EXPRESSION rather than a delegate so the registration can be
    ///         labelled after the property it reads: <c>() => k.PlayPause</c>
    ///         registers as <c>PlayPause</c>. Pass <paramref name="label" /> only
    ///         when the lookup is not a plain property read.
    ///     </para>
    /// </summary>
    void Register(Expression<Func<KeyChord>> chordLookup, Action action, string? label = null);

    /// <summary>
    ///     Convenience overload for fixed (non-rebindable) chords — used by a few
    ///     hard-wired internal binds like Escape and the bookmark digit keys.
    ///     Name the verb in <paramref name="label" />; without one, a lambda
    ///     action is labelled after the chord (<c>key:F12</c>).
    /// </summary>
    void Register(KeyChord chord, Action action, string? label = null);

    /// <summary>
    ///     Convenience overload that takes a bare <see cref="Key" /> with no
    ///     modifier — kept for the few non-rebindable single-key registrations.
    ///     Name the verb in <paramref name="label" />: a fixed key that doubles a
    ///     rebindable verb (<c>Key.Add</c> for <c>SpeedUp</c>) must count under
    ///     the same id, or the verb reads as half as used.
    /// </summary>
    void Register(Expression<Func<Key>> keyLookup, Action action, string? label = null);

    /// <summary>
    ///     Registers a VARIABLE-SIZED set of binds, re-enumerated on every
    ///     <see cref="RefreshBinds" />. Everything else here reserves exactly one
    ///     chord decided at startup, which cannot express bindings the user
    ///     CREATES at runtime — the match patterns, where both the number of
    ///     actions and their chords are data. Registering N fixed slots instead
    ///     would cap how many patterns can exist and reserve dead entries for the
    ///     ones that do not.
    /// </summary>
    void RegisterDynamic(Func<IEnumerable<KeybindRegistration>> source);

    /// <summary>
    ///     Processes a keypress and executes the registered action if one exists.
    /// </summary>
    bool HandleKeyPress(Key key, bool ctrl, bool shift, bool alt);

    /// <summary>
    ///     Rebuilds the internal dispatch table. Called when keybind settings change.
    /// </summary>
    void RefreshBinds();

    /// <summary>
    ///     The label of whatever currently owns <paramref name="chord" />, or null
    ///     when it is free. The pattern builder asks this before letting a chord be
    ///     assigned: silently overriding a shipped editing key is exactly what this
    ///     registry's last-registration-wins rule does otherwise.
    /// </summary>
    string? DescribeBinding(KeyChord chord);
}
