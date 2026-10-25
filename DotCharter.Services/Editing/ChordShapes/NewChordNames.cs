namespace DotCharter.Services.Editing.ChordShapes;

/// <summary>
///     The name a NEWLY MADE chord template gets: the suggester's first name while "Name new
///     chords automatically" is on (<c>AppConfig.AutoNameNewChords</c>), empty while it is off.
///     Every editor-side creation path asks it — the resolver's mint, accepted hand-shape
///     suggestions, a minted reshape, the chord panel's display seed and Generate fingerings —
///     and Guitar Pro import does for a chord its file leaves unnamed. A name the source carries
///     (a paste's source template, an imported name) is kept by the caller and never asks.
///     <para>
///         Process-wide because the paths are static services with no configuration at hand:
///         the app wires a read of its live config once at startup
///         (<see cref="Configure" />), so a settings change applies to the next chord made.
///         Unwired (every gate test) it is on, the default.
///     </para>
/// </summary>
public static class NewChordNames
{
    private static Func<bool> _automatic = static () => true;

    /// <summary>Wires the setting; the app passes a read of its cached config.</summary>
    public static void Configure(Func<bool> automatic) =>
        _automatic = automatic ?? throw new ArgumentNullException(nameof(automatic));

    /// <summary>Whether new chords are named automatically right now.</summary>
    public static bool Automatic => _automatic();

    /// <summary>The name for a new template on <paramref name="frets" />: suggested, or empty while naming is off.</summary>
    public static string For(Arrangement arrangement, IReadOnlyDictionary<int, int> frets, ChordNamingOptions? naming = null) =>
        Automatic ? ChordNameSuggester.SuggestFirstChordName(arrangement, frets, naming: naming) : string.Empty;

    /// <summary>
    ///     Whether an automatic flow may give <paramref name="template" /> a name: naming is on,
    ///     the template has no name yet, and the charter did not choose none.
    /// </summary>
    public static bool MayFill(ChordTemplate template) =>
        Automatic && string.IsNullOrEmpty(template.Name) && !template.Unnamed;
}
