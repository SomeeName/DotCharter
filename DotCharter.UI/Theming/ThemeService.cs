using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;

namespace DotCharter.UI.Theming;

public sealed class ThemeService : IThemeService
{
    private readonly Dictionary<string, Color> _current = new(StringComparer.Ordinal);

    public ThemeService()
    {
        foreach (var kv in CurrentPreset.Colors) _current[kv.Key] = kv.Value;
    }

    public ThemePreset CurrentPreset { get; private set; } = ThemePresets.Default;

    public IReadOnlyDictionary<string, Color> CurrentColors => _current;

    public event Action? ThemeChanged;

    public void ApplyPreset(ThemePreset preset, IReadOnlyDictionary<string, string>? overridesHex = null)
    {
        CurrentPreset = preset;
        _current.Clear();
        foreach (var kv in preset.Colors) _current[kv.Key] = kv.Value;

        // Fall back to the default preset for any registered token that this
        // preset omitted. Keeps the theme system stable even if a future
        // preset forgets a key — every DynamicResource consumer still
        // resolves to *something* sensible instead of disappearing.
        foreach (var key in ThemeTokens.All)
        {
            if (!_current.ContainsKey(key) && ThemePresets.Default.Colors.TryGetValue(key, out var fallback))
                _current[key] = fallback;
        }

        if (overridesHex != null)
        {
            foreach (var kv in overridesHex)
            {
                if (TryParseColor(kv.Value, out var c))
                    _current[kv.Key] = c;
            }
        }

        PushAll();
        ThemeChanged?.Invoke();
    }

    public void SetColor(string tokenKey, Color color)
    {
        _current[tokenKey] = color;
        Push(tokenKey, color);
        ThemeChanged?.Invoke();
    }

    public Color GetColor(string tokenKey)
    {
        if (_current.TryGetValue(tokenKey, out var c)) return c;
        if (CurrentPreset.Colors.TryGetValue(tokenKey, out c)) return c;
        return Colors.Magenta;
    }

    private void PushAll()
    {
        Run(() =>
        {
            var app = Application.Current;
            if (app == null) return;
            foreach (var kv in _current)
                Write(app, kv.Key, kv.Value);
        });
    }

    // The accent also drives Fluent's own SystemAccentColor family, so stock
    // controls follow the theme instead of the Windows accent.
    private static void Write(Application app, string key, Color color)
    {
        app.Resources[key] = color;
        if (key != ThemeTokens.Accent) return;
        foreach (var kv in FluentAccent.Derive(color))
            app.Resources[kv.Key] = kv.Value;
    }

    private static void Push(string key, Color color)
    {
        Run(() =>
        {
            var app = Application.Current;
            if (app == null) return;
            Write(app, key, color);
        });
    }

    private static void Run(Action a)
    {
        if (Dispatcher.UIThread.CheckAccess()) a();
        else Dispatcher.UIThread.Post(a);
    }

    private static bool TryParseColor(string? s, out Color c)
    {
        c = default;
        if (string.IsNullOrWhiteSpace(s)) return false;
        try
        {
            c = Color.Parse(s);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
