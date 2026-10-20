using System.ComponentModel;
using System.Globalization;
using System.Resources;
using System.Text.Json;

namespace DotCharter.Localization;

public sealed class LanguageOption : INotifyPropertyChanged
{
    private readonly string? _literalName;

    public LanguageOption(string configValue, string displayNameKey, string? literalName = null)
    {
        ConfigValue = configValue;
        DisplayNameKey = displayNameKey;
        _literalName = literalName;
    }

    public string ConfigValue { get; }
    public string DisplayNameKey { get; }

    // Community languages carry a literal name (their own native name, or the
    // "Language_DisplayName" entry inside the file). Built-in options resolve
    // their display name through the resx so it follows the active UI language.
    public string DisplayName => _literalName ?? LocalizationService.Instance[DisplayNameKey];

    public event PropertyChangedEventHandler? PropertyChanged;

    internal void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayName)));
}

public interface ILocalizationService : INotifyPropertyChanged
{
    IReadOnlyList<LanguageOption> LanguageOptions { get; }
    string ConfiguredCulture { get; }
    CultureInfo Culture { get; }

    /// <summary>True when the active culture reads right-to-left (Arabic,
    /// Hebrew, …) — the shell binds <c>FlowDirection</c> to this.</summary>
    bool IsRightToLeft { get; }

    string this[string key] { get; }
    string Format(string key, params object[] args);

    /// <summary>Picks the singular or plural form by <paramref name="count" />
    /// and passes it as <c>{0}</c>. See the implementation for why this is a
    /// two-form rule and not CLDR categories.</summary>
    string Plural(string oneKey, string manyKey, int count, params object[] extra);

    /// <summary>As <see cref="Plural" />, but <paramref name="count" /> only
    /// CHOOSES — the arguments are passed through verbatim.</summary>
    string PluralWith(string oneKey, string manyKey, int count, params object[] args);

    void SetConfiguredCulture(string? configValue);

    /// <summary>Loads community-contributed language packs (flat
    /// <c>&lt;culture&gt;.json</c> key/value files) from <paramref name="folder" />,
    /// making them selectable. Never throws — a malformed or partial file is
    /// skipped and its missing keys fall back to English.</summary>
    void LoadCommunityLanguages(string folder);
}

public sealed class LocalizationService : ILocalizationService
{
    public const string SystemLanguage = "system";

    // Reserved key a community pack can set to name itself in the language
    // picker; falls back to the culture's native name when absent.
    private const string DisplayNameKey = "Language_DisplayName";

    private static readonly string[] SupportedCultures = ["en-US", "de-DE", "ja-JP"];
    private static readonly CultureInfo EnUs = CultureInfo.GetCultureInfo("en-US");

    private readonly ResourceManager _resources = new(
        "DotCharter.Localization.Resources",
        typeof(LocalizationService).Assembly);

    // Community packs keyed by culture code (case-insensitive).
    private readonly Dictionary<string, CommunityLanguage> _community =
        new(StringComparer.OrdinalIgnoreCase);

    private CommunityLanguage? _activeCommunity;

    private LocalizationService() => RebuildLanguageOptions();

    public static LocalizationService Instance { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<LanguageOption> LanguageOptions { get; private set; } = [];

    public string ConfiguredCulture { get; private set; } = SystemLanguage;
    public CultureInfo Culture { get; private set; } = EnUs;
    public bool IsRightToLeft => Culture.TextInfo.IsRightToLeft;

    public string this[string key] => GetString(key);

    /// <summary>
    ///     The STRING comes from <see cref="Culture" /> (the language the user
    ///     chose for the UI); the NUMBERS and DATES substituted into it come
    ///     from <see cref="CultureInfo.CurrentCulture" /> (the formatting the
    ///     user set in Windows). Those are two separate choices and this is the
    ///     one place both are made — see <c>SetConfiguredCulture</c>.
    /// </summary>
    public string Format(string key, params object[] args) => string.Format(CultureInfo.CurrentCulture, GetString(key), args);

    /// <summary>
    ///     Picks between a SINGULAR and a PLURAL resource by count, then formats.
    ///     <para>
    ///         It exists because the game surfaces print small counts constantly
    ///         and a plural-only string reads as broken the moment one of them is
    ///         1 — the launch screen said "1 Tage Serie", the calendar "1 aktive
    ///         Tage" and "1 Sessions". English tolerates it less obviously and
    ///         German not at all.
    ///     </para>
    ///     <para>
    ///         Deliberately the SIMPLE two-form rule, not CLDR plural categories:
    ///         both shipped languages are two-form (one / other), and a language
    ///         pack that needs more can supply a plural string that reads for any
    ///         count. <paramref name="count"/> is also passed as
    ///         <c>{0}</c> so a caller never repeats it. The STRING comes from
    ///         the resource culture and the COUNT is formatted with the ambient
    ///         culture — see <see cref="Format" />.
    ///     </para>
    /// </summary>
    public string Plural(string oneKey, string manyKey, int count, params object[] extra) => FormatPlural(GetString, CultureInfo.CurrentCulture, oneKey, manyKey, count, extra);

    /// <summary>
    ///     The two-form rule itself: <paramref name="oneKey" /> for exactly 1,
    ///     <paramref name="manyKey" /> for everything else (zero and negatives
    ///     included). <see cref="Plural" /> and <see cref="PluralWith" /> both
    ///     choose through it.
    /// </summary>
    public static string PluralKey(string oneKey, string manyKey, int count) => count == 1 ? oneKey : manyKey;

    /// <summary>
    ///     <see cref="Plural" /> with the resource lookup and the number format
    ///     passed in. It exists so the chooser can be tested against a lookup
    ///     whose two forms differ by construction: through the instance, a
    ///     language whose singular and plural are the same text (Japanese)
    ///     cannot tell which form was picked.
    /// </summary>
    public static string FormatPlural(Func<string, string> lookup, IFormatProvider formatProvider,
        string oneKey, string manyKey, int count, object[] extra)
    {
        var args = new object[1 + extra.Length];
        args[0] = count;
        for (var i = 0; i < extra.Length; i++) args[i + 1] = extra[i];
        return string.Format(formatProvider, lookup(PluralKey(oneKey, manyKey, count)), args);
    }

    /// <summary>
    ///     <para>
    ///         The same two-form choice as <see cref="Plural" />, except
    ///         <paramref name="count" /> only DECIDES — it is not substituted.
    ///         The arguments are passed to <c>string.Format</c> verbatim.
    ///     </para>
    ///     <para>
    ///         It exists for the strings whose <c>{0}</c> is a FORMATTED
    ///         quantity rather than the raw number. The inspector prints a
    ///         sustain as "1½ beats" (<c>FractionalLengthFormat</c>, vulgar
    ///         fractions and all) and the drop tuner prints a signed step as
    ///         "-2 semitones" — neither is the integer that decides the form,
    ///         and <see cref="Plural" /> would overwrite the display with it.
    ///         Pass the deciding magnitude here: <c>PluralWith(one, many,
    ///         Math.Abs(step), step.ToString("+0;-0"))</c>.
    ///     </para>
    /// </summary>
    public string PluralWith(string oneKey, string manyKey, int count, params object[] args) => string.Format(CultureInfo.CurrentCulture, GetString(PluralKey(oneKey, manyKey, count)), args);

    public void LoadCommunityLanguages(string folder)
    {
        _community.Clear();
        try
        {
            if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
            {
                foreach (var file in Directory.EnumerateFiles(folder, "*.json"))
                {
                    var lang = TryLoadFile(file);
                    if (lang != null) _community[lang.Code] = lang;
                }
            }
        }
        catch
        {
            // A single unreadable folder/file must never take down startup.
        }

        // Re-resolve the ACTIVE pack against the set we just rebuilt. Startup
        // calls this before SetConfiguredCulture so it is normally a no-op, but
        // a second call (a rescan after the user drops a pack in) would
        // otherwise keep serving strings out of a CommunityLanguage that is no
        // longer in the folder — a deleted pack would go on being the UI
        // language until restart.
        _activeCommunity = _community.GetValueOrDefault(ConfiguredCulture);

        RebuildLanguageOptions();
    }

    public void SetConfiguredCulture(string? configValue)
    {
        ConfiguredCulture = NormalizeConfigValue(configValue);
        _activeCommunity = _community.GetValueOrDefault(ConfiguredCulture);
        Culture = ResolveCulture(ConfiguredCulture);

        // UI culture ONLY. The FORMATTING culture (CultureInfo.CurrentCulture —
        // number separators, date and time patterns, the first day of the week)
        // is a Windows setting the user already made, and this method has no
        // business overwriting it with the language they picked for the menus.
        //
        // Overwriting it is wrong in both directions:
        //   * an unsupported UI language resolves to en-US (ResolveCulture's
        //     fallback), so a French, Spanish or Polish user — anyone outside
        //     the three built-ins and the installed packs — would get English strings
        //     AND en-US dates and decimal points, on their own machine;
        //   * a supported one is NORMALISED to the base culture, so a de-AT
        //     user would be moved to de-DE and their calendar would say
        //     "Januar" instead of "Jänner".
        //
        // Safe to leave alone only because nothing that reaches a FILE or a WIRE
        // reads the ambient culture any more — see DotCharter.Localization's
        // AGENTS.md § "A FILE or a WIRE is never localized" and the two lints it
        // names.
        CultureInfo.DefaultThreadCurrentUICulture = Culture;
        Thread.CurrentThread.CurrentUICulture = Culture;

        foreach (var option in LanguageOptions)
            option.Refresh();

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LanguageOptions)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRightToLeft)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    private string GetString(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return string.Empty;

        // Community pack wins for keys it provides; everything it omits falls
        // through to the resx (which itself resolves invariant/English last).
        if (_activeCommunity != null &&
            _activeCommunity.Strings.TryGetValue(key, out var community) &&
            !string.IsNullOrEmpty(community))
        {
            return community;
        }

        return _resources.GetString(key, Culture) ??
               _resources.GetString(key, EnUs) ?? key;
    }

    private void RebuildLanguageOptions()
    {
        var options = new List<LanguageOption>
        {
            new(SystemLanguage, "Language_System"),
            new("en-US", "Language_English"),
            new("de-DE", "Language_German"),
            new("ja-JP", "Language_Japanese")
        };

        foreach (var lang in _community.Values.OrderBy(l => l.DisplayName, StringComparer.CurrentCultureIgnoreCase))
            options.Add(new LanguageOption(lang.Code, string.Empty, lang.DisplayName));

        LanguageOptions = options;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LanguageOptions)));
    }

    private static CommunityLanguage? TryLoadFile(string file)
    {
        try
        {
            var code = Path.GetFileNameWithoutExtension(file);
            if (string.IsNullOrWhiteSpace(code)) return null;

            // Community files must never shadow a built-in, fully-translated
            // culture; those live in the resx.
            if (SupportedCultures.Contains(code, StringComparer.OrdinalIgnoreCase)) return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;

            var strings = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String)
                    strings[prop.Name] = prop.Value.GetString() ?? string.Empty;
            }

            if (strings.Count == 0) return null;

            var culture = EnUs;
            var validCulture = false;
            try
            {
                culture = CultureInfo.GetCultureInfo(code);
                validCulture = true;
            }
            catch (CultureNotFoundException)
            {
                // Keep the pack usable for lookups; formatting uses en-US.
            }

            strings.TryGetValue(DisplayNameKey, out var displayName);
            if (string.IsNullOrWhiteSpace(displayName))
                displayName = validCulture ? culture.NativeName : code;

            return new CommunityLanguage(code, displayName!, culture, strings);
        }
        catch
        {
            // Malformed JSON / IO error: skip this file entirely.
            return null;
        }
    }

    private string NormalizeConfigValue(string? configValue)
    {
        if (string.IsNullOrWhiteSpace(configValue)) return SystemLanguage;
        if (string.Equals(configValue, SystemLanguage, StringComparison.OrdinalIgnoreCase)) return SystemLanguage;
        if (_community.TryGetValue(configValue, out var comm)) return comm.Code;

        return TryMapSupportedCulture(configValue, out var supported) ? supported : SystemLanguage;
    }

    private CultureInfo ResolveCulture(string configValue)
    {
        if (string.Equals(configValue, SystemLanguage, StringComparison.OrdinalIgnoreCase))
        {
            return TryMapSupportedCulture(CultureInfo.CurrentUICulture.Name, out var supported)
                ? CultureInfo.GetCultureInfo(supported)
                : EnUs;
        }

        if (_community.TryGetValue(configValue, out var comm)) return comm.Culture;

        try
        {
            return CultureInfo.GetCultureInfo(configValue);
        }
        catch (CultureNotFoundException)
        {
            return EnUs;
        }
    }

    private static bool TryMapSupportedCulture(string cultureName, out string supportedCulture)
    {
        supportedCulture = string.Empty;
        if (string.IsNullOrWhiteSpace(cultureName)) return false;

        var exact = SupportedCultures.FirstOrDefault(culture =>
            string.Equals(culture, cultureName, StringComparison.OrdinalIgnoreCase));
        if (exact != null)
        {
            supportedCulture = exact;
            return true;
        }

        var neutralName = cultureName.Split('-')[0];
        var neutral = SupportedCultures.FirstOrDefault(culture =>
            string.Equals(culture.Split('-')[0], neutralName, StringComparison.OrdinalIgnoreCase));
        if (neutral == null) return false;

        supportedCulture = neutral;
        return true;
    }

    private sealed class CommunityLanguage
    {
        public CommunityLanguage(string code, string displayName, CultureInfo culture,
            IReadOnlyDictionary<string, string> strings)
        {
            Code = code;
            DisplayName = displayName;
            Culture = culture;
            Strings = strings;
        }

        public string Code { get; }
        public string DisplayName { get; }
        public CultureInfo Culture { get; }
        public IReadOnlyDictionary<string, string> Strings { get; }
    }
}
