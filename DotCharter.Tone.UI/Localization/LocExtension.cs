using Avalonia.Data;
using Avalonia.Markup.Xaml;
using DotCharter.Tone.UI.Converters;

namespace DotCharter.Tone.UI.Localization;

public sealed class LocExtension : MarkupExtension
{
    public LocExtension()
    {
    }

    public LocExtension(string key) => Key = key;

    public string Key { get; set; } = string.Empty;

    /// <summary>
    ///     Uppercases the localized value for a Fire MONO step
    ///     (<c>{localization:Loc Some_Key, Upper=True}</c>).
    ///     <para>
    ///         The layer's rule for IBM Plex Mono is "always uppercase, always
    ///         letter-spaced", and Avalonia has no <c>text-transform</c>. For most
    ///         strings that is a CONTENT rule — the resx value is written in caps.
    ///         It cannot be when the SAME key also labels a title-case surface:
    ///         the inspector's eight tab captions are both the mono strip AND the
    ///         Chakra header of the tab each one opens, so uppercasing the resx
    ///         value would fix the strip by shouting in the header. Half that
    ///         strip shipped in caps and half in sentence case for exactly that
    ///         reason.
    ///     </para>
    ///     <para>
    ///         Culture-aware, through the same <see cref="TextConverters.ToUpper" />
    ///         the editor's status rail uses — one transform, so the shell cannot
    ///         end up with two answers for a Turkish dotted i.
    ///     </para>
    /// </summary>
    public bool Upper { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        return new Binding($"[{Key}]")
        {
            Source = LocalizationService.Instance,
            Mode = BindingMode.OneWay,
            Converter = Upper ? TextConverters.ToUpper : null
        };
    }
}
