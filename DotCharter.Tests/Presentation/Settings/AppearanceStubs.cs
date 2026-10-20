using DotCharter.Services.Infrastructure.Appearance;
using DotCharter.UI.Theming;

namespace DotCharter.Tests.Presentation.Settings;

/// <summary>Recording fakes of the two Appearance seams: a theme of hex strings and a scale with no shell behind it.</summary>
internal static class AppearanceStubs
{
    public const string Base = "#101010";

    /// <summary>
    ///     The real token catalogue (so names localize), every colour starting at
    ///     <see cref="Base" />. The string palettes colour the three <c>String*</c>
    ///     tokens it knows; the default palette colours none.
    /// </summary>
    internal sealed class FakeTheme : IAppearanceTheme
    {
        private static readonly Dictionary<string, string> Okabe = new()
        {
            [ThemeTokens.String1] = "#D55E00", [ThemeTokens.String2] = "#F0E442", [ThemeTokens.String3] = "#56B4E9"
        };

        private readonly Dictionary<string, string> _colors = new(StringComparer.Ordinal);

        public FakeTheme()
        {
            foreach (var token in Tokens) _colors[token.Key] = Base;
        }

        public IReadOnlyList<ThemeTokenEntry> Tokens { get; } =
        [
            .. ThemePresets.CustomizableTokens
                .Select(t => new ThemeTokenEntry(t.Key, t.DisplayName, t.Category))
        ];

        public IReadOnlyList<StringPaletteEntry> StringPalettes { get; } =
            [new("default", "Default (theme)"), new("okabe-ito", "Okabe-Ito"), new("ibm", "IBM")];

        public string DefaultStringPaletteId => "default";

        public int PaletteCalls { get; private set; }

        public int ResetCalls { get; private set; }

        public int PersistedCalls { get; private set; }

        public (IReadOnlyDictionary<string, string> FreeForm, string PaletteId)? LastPersisted { get; private set; }

        public string Color(string key) => _colors[key];

        public string GetHex(string tokenKey) => _colors[tokenKey];

        public void SetHex(string tokenKey, string hex)
        {
            if (hex.Length is 7 or 9 && hex[0] == '#') _colors[tokenKey] = hex.ToUpperInvariant();
        }

        public void ApplyStringPalette(string paletteId)
        {
            PaletteCalls++;
            LayPalette(paletteId);
        }

        public void ResetColors(string paletteId)
        {
            ResetCalls++;
            foreach (var key in _colors.Keys.ToList()) _colors[key] = Base;
            LayPalette(paletteId);
        }

        public void ApplyPersisted(IReadOnlyDictionary<string, string> freeFormOverrides, string paletteId)
        {
            PersistedCalls++;
            LastPersisted = (new Dictionary<string, string>(freeFormOverrides), paletteId);
            foreach (var key in _colors.Keys.ToList()) _colors[key] = Base;
            foreach (var kv in freeFormOverrides) _colors[kv.Key] = kv.Value;
            LayPalette(paletteId);
        }

        public IReadOnlyDictionary<string, string> FreeFormOverrides(bool excludeStringTokens) =>
            _colors.Where(kv => kv.Value != Base && !(excludeStringTokens && StringColorPresets.IsStringToken(kv.Key)))
                .ToDictionary(kv => kv.Key, kv => kv.Value);

        private void LayPalette(string paletteId)
        {
            foreach (var key in Okabe.Keys) _colors[key] = Base;
            if (paletteId == "okabe-ito")
                foreach (var kv in Okabe) _colors[kv.Key] = kv.Value;
        }
    }

    /// <summary>Auto first, then 100–200 %. Under Auto the "shell" derives 125 %.</summary>
    internal sealed class FakeScale : IInterfaceScale
    {
        public const double AutoDerived = 1.25;

        private static readonly double[] Factors = [1.00, 1.25, 1.50, 1.75, 2.00];

        public bool IsAutoMode { get; private set; }

        public double AppliedFactor { get; private set; } = 1.0;

        public int ApplyCalls { get; private set; }

        public int RestoreCalls { get; private set; }

        public IReadOnlyList<string> ChoiceLabels() => ["Auto", "100 %", "125 %", "150 %", "175 %", "200 %"];

        public int IndexFor(bool auto, double factor)
        {
            if (auto) return 0;
            var best = 0;
            for (var i = 1; i < Factors.Length; i++)
                if (Math.Abs(Factors[i] - factor) < Math.Abs(Factors[best] - factor)) best = i;
            return best + 1;
        }

        public double? FactorFor(int index) => IsAuto(index) ? null : Factors[index - 1];

        public bool IsAuto(int index) => index <= 0 || index > Factors.Length;

        public double Clamp(double factor) => Math.Clamp(factor, 0.75, 2.0);

        public void Apply(int index)
        {
            ApplyCalls++;
            IsAutoMode = FactorFor(index) is null;
            AppliedFactor = FactorFor(index) ?? AutoDerived;
        }

        public void Restore(bool auto, double factor)
        {
            RestoreCalls++;
            IsAutoMode = auto;
            AppliedFactor = auto ? AutoDerived : Clamp(factor);
        }
    }
}
