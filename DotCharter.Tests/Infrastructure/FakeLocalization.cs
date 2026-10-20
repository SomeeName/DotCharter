using System.ComponentModel;
using System.Globalization;
using System.Resources;
using DotCharter.Localization;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     A deterministic <see cref="ILocalizationService" /> for tests of classes that take
///     their localization through the constructor. It never reads or writes the
///     process-wide culture and holds no state shared with
///     <see cref="LocalizationService.Instance" />, so a test that uses it needs no
///     <c>[Collection("LocalizationSingleton")]</c>.
///     <para>
///         <see cref="English" /> (the default) answers every key with the invariant
///         (English) resx value, exactly as the real service does under en-US:
///         a missing key resolves to the key name and a blank key to the empty string.
///         <see cref="KeysOnly" /> answers with the KEY instead, so a test can assert
///         which resource was chosen without depending on its wording.
///     </para>
///     <para>
///         Formatting mirrors the real service — the string is looked up, then the
///         arguments are substituted with <c>string.Format</c>, <c>Plural</c> passing
///         the count as <c>{0}</c> and <c>PluralWith</c> letting it only choose — except
///         that the arguments are formatted with a FIXED culture (invariant unless one is
///         given) rather than <see cref="CultureInfo.CurrentCulture" />.
///     </para>
/// </summary>
internal sealed class FakeLocalization : ILocalizationService
{
    private static readonly CultureInfo EnUs = CultureInfo.GetCultureInfo("en-US");

    private static readonly ResourceManager Resources = new(
        "DotCharter.Localization.Resources",
        typeof(LocalizationService).Assembly);

    private readonly bool _keysOnly;
    private readonly CultureInfo _formatCulture;

    private FakeLocalization(bool keysOnly, CultureInfo? formatCulture)
    {
        _keysOnly = keysOnly;
        _formatCulture = formatCulture ?? CultureInfo.InvariantCulture;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<LanguageOption> LanguageOptions { get; } = [];

    public string ConfiguredCulture { get; private set; } = LocalizationService.SystemLanguage;

    public CultureInfo Culture => EnUs;

    public bool IsRightToLeft => false;

    public string this[string key] => Lookup(key);

    /// <summary>English resx values; arguments formatted with <paramref name="formatCulture" />
    /// (invariant when null).</summary>
    public static FakeLocalization English(CultureInfo? formatCulture = null) => new(false, formatCulture);

    /// <summary>
    ///     Answers with the key itself: <c>["Foo"]</c> is <c>Foo</c>, and a formatted
    ///     lookup appends its arguments, <c>Format("Foo_Fmt", 3, "x")</c> being
    ///     <c>Foo_Fmt(3, x)</c>.
    /// </summary>
    public static FakeLocalization KeysOnly(CultureInfo? formatCulture = null) => new(true, formatCulture);

    public string Format(string key, params object[] args) => Render(key, args);

    public string Plural(string oneKey, string manyKey, int count, params object[] extra)
    {
        var args = new object[1 + extra.Length];
        args[0] = count;
        Array.Copy(extra, 0, args, 1, extra.Length);
        return Render(count == 1 ? oneKey : manyKey, args);
    }

    public string PluralWith(string oneKey, string manyKey, int count, params object[] args) => Render(count == 1 ? oneKey : manyKey, args);

    /// <summary>Records the setting and raises <see cref="PropertyChanged" />; the strings
    /// and the process culture are left exactly as they were.</summary>
    public void SetConfiguredCulture(string? configValue)
    {
        ConfiguredCulture = string.IsNullOrWhiteSpace(configValue)
            ? LocalizationService.SystemLanguage
            : configValue.Trim();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ConfiguredCulture)));
    }

    /// <summary>A no-op: the fake serves the built-in English table only.</summary>
    public void LoadCommunityLanguages(string folder)
    {
    }

    private string Lookup(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return string.Empty;
        }

        if (_keysOnly)
        {
            return key;
        }

        return Resources.GetString(key, EnUs) ?? key;
    }

    private string Render(string key, object[] args)
    {
        if (!_keysOnly)
        {
            return string.Format(_formatCulture, Lookup(key), args);
        }

        if (args.Length == 0)
        {
            return Lookup(key);
        }

        var rendered = args.Select(a => string.Format(_formatCulture, "{0}", a));
        return $"{Lookup(key)}({string.Join(", ", rendered)})";
    }
}
