namespace DotCharter.Services.Infrastructure.Configuration;

/// <summary>
///     The Rocksmith 2014 keys, as the <c>RocksmithKeys</c> object of
/// <c>appconfig.json</c>. DotCharter ships none of them; the user
///     pastes them here, and nothing in the UI names the section. Every value is
///     EMPTY by default and always written, so the file shows where each key goes.
///     <para>
///         A value is a 32-byte key as 64 hex digits (whitespace allowed). Empty
///         means "no key"; a malformed value is ignored and logged by its NAME
///         (<see cref="Problems" />) — never by its content. The values are
///         secrets: <c>SensitiveTextRedactor</c> blanks them in the diagnostics
///         bundle and the crash report. The product reads them through
///         <c>Infrastructure.Psarc.RocksmithKeys</c>; the rules are in
///         <c>docs/agents/services/parsing-psarc.md</c>.
///     </para>
/// </summary>
public sealed class RocksmithKeysConfig
{
    /// <summary>Every Rocksmith key is AES-256: 32 bytes, 64 hex digits.</summary>
    public const int KeyBytes = 32;

    /// <summary>The PSARC table-of-contents key.</summary>
    public string PsarcToc { get; set; } = string.Empty;

    /// <summary>The PC SNG key.</summary>
    public string SngPc { get; set; } = string.Empty;

    /// <summary>The Mac SNG key.</summary>
    public string SngMac { get; set; } = string.Empty;

    /// <summary>Members of the section this build does not declare, kept and written
    /// back unchanged, like <see cref="AppConfig.UnknownSettings" />.</summary>
    [System.Text.Json.Serialization.JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? UnknownSettings { get; set; }

    /// <summary>Whether any value is filled in (well-formed or not).</summary>
    public bool HasAnyValue() =>
        !string.IsNullOrWhiteSpace(PsarcToc) || !string.IsNullOrWhiteSpace(SngPc) || !string.IsNullOrWhiteSpace(SngMac);

    /// <summary>
    ///     The key a value holds, or null when it is empty or malformed;
    ///     <paramref name="problem" /> says why a non-empty value was refused,
    ///     without any of its characters.
    /// </summary>
    public static byte[]? TryParseKey(string? value, out string? problem)
    {
        problem = null;
        if (string.IsNullOrWhiteSpace(value)) return null;

        var hex = string.Concat(value.Where(c => !char.IsWhiteSpace(c)));
        if (!hex.All(char.IsAsciiHexDigit))
        {
            problem = "is not hex";
            return null;
        }

        if (hex.Length != KeyBytes * 2)
        {
            problem = $"holds {hex.Length} hex digits, not {KeyBytes * 2}";
            return null;
        }

        return Convert.FromHexString(hex);
    }

    /// <summary>One line per malformed value, naming the field and never its content.</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        Check(nameof(PsarcToc), PsarcToc);
        Check(nameof(SngPc), SngPc);
        Check(nameof(SngMac), SngMac);
        return problems;

        void Check(string name, string value)
        {
            if (TryParseKey(value, out var problem) == null && problem != null)
                problems.Add($"appconfig.json RocksmithKeys.{name} {problem}; that key is ignored.");
        }
    }

    /// <summary>An explicit <c>null</c> in the file reads as empty, so the next save
    /// writes the empty field back.</summary>
    internal void Normalize()
    {
        PsarcToc ??= string.Empty;
        SngPc ??= string.Empty;
        SngMac ??= string.Empty;
    }
}
