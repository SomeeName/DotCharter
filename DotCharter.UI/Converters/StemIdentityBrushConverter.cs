using System.Globalization;
using Avalonia.Data.Converters;
using DotCharter.Tone.UI.Converters;

namespace DotCharter.UI.Converters;

/// <summary>
///     A separated stem's NAME → the Fire token key that names its identity
///     colour. Pure and UI-free so the mapping is pinned in the default gate
///     (<c>UI/StemIdentityTests</c>).
///     <para>
///         The names are the separator's own output — htdemucs writes
///         <c>drums</c>/<c>bass</c>/<c>other</c>/<c>vocals</c> and, on the
///         6-source model, <c>guitar</c>/<c>piano</c> (see
///         <c>JuceStemSeparationService</c>) — plus whatever a user dropped into
///         a project's <c>stems/</c> folder, which the mixer adopts verbatim. So
///         an UNKNOWN name is an ordinary state, not an error: it takes the
///         neutral slate identity rather than borrowing another instrument's.
///     </para>
///     <para>
///         The master-mix strip is identified by its FLAG, never by its display
///         name — <c>StemMixerViewModel</c> builds it with the literal
///         "Original", and keying on that string would silently repaint the row
///         the day it is localized.
///     </para>
/// </summary>
public static class StemIdentity
{
    /// <summary>The whole song, not one instrument — the layer's accent.</summary>
    public const string MixToken = "FireStemMix";

    /// <summary>Anything the separator did not name for us.</summary>
    public const string OtherToken = "FireStemOther";

    /// <summary>
    ///     The Fire token key for a channel strip. Never returns null or empty —
    ///     a row always has an identity, even if it is the neutral one.
    /// </summary>
    public static string TokenFor(string? name, bool isMasterMix)
    {
        if (isMasterMix) return MixToken;

        return (name ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "guitar" or "guitars" or "guitar-lead" or "guitar-rhythm" => "FireStemGuitar",
            "bass" => "FireStemBass",
            "drums" or "drum" => "FireStemDrums",
            "vocals" or "vocal" => "FireStemVocals",
            "piano" or "keys" => "FireStemPiano",
            _ => OtherToken
        };
    }
}

/// <summary>
///     Binds a channel strip's (<c>Name</c>, <c>IsMasterMix</c>) pair to the
///     brush its identity token names. Two hops on purpose: the view model says
///     WHICH stem, <see cref="StemIdentity" /> says which identity, and the one
///     token dictionary still says what that identity looks like — so no colour
///     value ever lives in a view model or in markup.
/// </summary>
public sealed class StemIdentityBrushConverter : IMultiValueConverter
{
    /// <summary>The shared instance markup binds to.</summary>
    public static readonly StemIdentityBrushConverter Instance = new();

    /// <inheritdoc />
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        var name = values.Count > 0 ? values[0] as string : null;
        var isMaster = values.Count > 1 && values[1] is true;
        return FireTokenBrushConverter.Instance.Convert(
            StemIdentity.TokenFor(name, isMaster), targetType, parameter, culture);
    }
}
