using System.Globalization;
using Avalonia.Data.Converters;

namespace DotCharter.Tone.UI.Converters;

/// <summary>
///     Text transforms for the Fire layer's MONO steps.
///     <para>
///         The layer's typography rule for IBM Plex Mono is "always uppercase,
///         always letter-spaced", and for authored copy that is a content rule —
///         the resx value is already written in caps. It cannot be for BOUND
///         data: a project name, a song title or a tempo read-out arrives in
///         whatever case the user or the package wrote it, and Avalonia has no
///         `text-transform`. Without this the editor's top-bar context reads
///         "Sakasama · 83 BPM" in a face and a tracking that only works in caps.
///     </para>
/// </summary>
public static class TextConverters
{
    /// <summary>Uppercases a bound string for a mono caps read-out. Culture-aware
    /// on purpose (`ToUpper`, not `ToUpperInvariant`): the shell is localized and
    /// a Turkish dotted i is not the same letter as an invariant one.</summary>
    public static readonly IValueConverter ToUpper = new FuncValueConverter<string?, string?>(
        s => s?.ToUpper(CultureInfo.CurrentCulture));
}
