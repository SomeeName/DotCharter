namespace DotCharter.Services.Editing;

/// <summary>
///     The persistent "which strings am I editing" selector — DotCharter's
///     counterpart to EOF's <c>eof_pro_guitar_fret_bitmask</c>
///     (<c>EOF_SRC/main.c:274</c>, toggled with <c>SHIFT+Numpad 1..6</c>) and
///     rscharter's <c>CurrentSelectionEditor.getEditedStrings()</c>
///     (<c>Ctrl+1..9</c>).
///     <para>
///         <b>An EMPTY mask means EVERY string.</b> That is the fallback both
///         reference tools use, it is what the chord inspector's per-string
///         checkboxes already did
///         (<c>InspectorViewModel.ApplyChordNoteMutation</c> falls back to all
///         strings when none are picked), and it is what makes the mask a strict
///         no-op until the user actually sets one — every keyboard verb behaves
///         exactly as it did before the first <c>Ctrl+n</c>.
///     </para>
///     <para>
///         An immutable value type on purpose: it is stored in
///         <c>EditorUiState</c> / <c>ProjectState.StringEditMask</c> and read on
///         the render path, so there must be no way for a consumer to mutate the
///         shared instance. Toggling produces a new value.
///     </para>
/// </summary>
public readonly struct StringEditMask : IEquatable<StringEditMask>
{
    /// <summary>Highest string index the mask can address. Nine because the
    /// keyboard binding is <c>Ctrl+1..9</c>; the model itself allows 0..7
    /// (<c>Arrangement.StringCount</c> tops out at 8).</summary>
    public const int MaxStrings = 9;

    private StringEditMask(int bits) => Bits = bits;

    /// <summary>Raw bit payload — bit <c>i</c> set = string <c>i</c> targeted.
    /// <c>0</c> is the empty mask (= all strings).</summary>
    public int Bits { get; }

    /// <summary>The default: no string explicitly targeted, so every string is.</summary>
    public static StringEditMask Empty => default;

    /// <summary>True when no string is explicitly targeted, i.e. every string is.</summary>
    public bool IsEmpty => Bits == 0;

    /// <summary>How many strings are explicitly targeted (0 while empty).</summary>
    public int Count => System.Numerics.BitOperations.PopCount((uint)Bits);

    public static StringEditMask FromBits(int bits) => new(bits & ((1 << MaxStrings) - 1));

    public static StringEditMask FromStrings(params int[] stringIndices)
    {
        var bits = 0;
        foreach (var index in stringIndices)
        {
            if (IsAddressable(index))
                bits |= 1 << index;
        }

        return new StringEditMask(bits);
    }

    /// <summary>
    ///     THE question every per-string verb asks: may I edit this string?
    ///     An empty mask says yes to everything.
    /// </summary>
    public bool Includes(int stringIndex) =>
        IsEmpty || (IsAddressable(stringIndex) && (Bits & (1 << stringIndex)) != 0);

    /// <summary>
    ///     Whether the string is EXPLICITLY in the mask — what the lane headers
    ///     draw. Deliberately distinct from <see cref="Includes" />: an empty
    ///     mask edits every string but must not render as "all nine lit", or the
    ///     user cannot see that no mask is set.
    /// </summary>
    public bool IsExplicit(int stringIndex) =>
        IsAddressable(stringIndex) && (Bits & (1 << stringIndex)) != 0;

    /// <summary>Flip one string in or out of the mask. Removing the last one
    /// yields <see cref="Empty" />, i.e. back to "all strings".</summary>
    public StringEditMask Toggle(int stringIndex)
    {
        if (!IsAddressable(stringIndex)) return this;
        return new StringEditMask(Bits ^ (1 << stringIndex));
    }

    /// <summary>
    ///     Drop every bit the arrangement has no LANE for.
    ///     <para>
    ///         <see cref="MaxStrings" /> is a property of the KEYBOARD (nine digit
    ///         keys); the lanes on screen are the active arrangement's
    ///         <c>Tuning.Length</c>. The digit families refuse a string the
    ///         arrangement has not got, but a mask set on a 6-string lead SURVIVED
    ///         the switch to a 4-string bass: bit 5 stayed lit with no lane header
    ///         to draw it on, and an invisible mask makes every per-string verb a
    ///         silent no-op — exactly the invisible modal state the mask contract
    ///         forbids. Narrowing on the switch is the only answer that keeps what
    ///         the mask SAYS and what the highway SHOWS the same thing.
    ///     </para>
    ///     <para>
    ///         Narrowing the last bit away yields <see cref="Empty" />, i.e. back
    ///         to "all strings" — the same landing the last <c>Ctrl+n</c> toggle
    ///         has.
    ///     </para>
    /// </summary>
    public StringEditMask NarrowTo(int stringCount)
    {
        if (stringCount >= MaxStrings) return this;
        var keep = stringCount <= 0 ? 0 : (1 << stringCount) - 1;
        return new StringEditMask(Bits & keep);
    }

    /// <summary>Replace the mask with exactly this one string.</summary>
    public StringEditMask Only(int stringIndex) =>
        IsAddressable(stringIndex) ? new StringEditMask(1 << stringIndex) : this;

    private static bool IsAddressable(int stringIndex) => stringIndex is >= 0 and < MaxStrings;

    public bool Equals(StringEditMask other) => Bits == other.Bits;
    public override bool Equals(object? obj) => obj is StringEditMask other && Equals(other);
    public override int GetHashCode() => Bits;
    public static bool operator ==(StringEditMask a, StringEditMask b)
    {
        return a.Equals(b);
    }

    public static bool operator !=(StringEditMask a, StringEditMask b)
    {
        return !a.Equals(b);
    }

    /// <summary>Status-line form, EOF's <c>song.c:9366-9376</c> idea: "1-3-5" or
    /// "all".</summary>
    public override string ToString()
    {
        if (IsEmpty) return "all";
        var parts = new List<string>();
        for (var i = 0; i < MaxStrings; i++)
        {
            if (IsExplicit(i))
                parts.Add((i + 1).ToString());
        }

        return string.Join("-", parts);
    }
}
