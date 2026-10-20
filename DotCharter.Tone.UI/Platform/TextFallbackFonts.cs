using Avalonia.Media;

namespace DotCharter.Tone.UI.Platform;

/// <summary>
///     The per-glyph FALLBACK chain the app hands Avalonia's font manager: the
///     families it may reach for when the requested face has no glyph for a
///     character. Every face in the theme is Latin-only — <c>FireFontDisplay</c>
///     is Chakra Petch, <c>FireFontUi</c> is Space Grotesk, <c>FireFontMono</c>
///     is IBM Plex Mono (<c>Styles/FireTokens/Palette.axaml</c>) — so every
///     non-Latin character in a song title, artist or album goes through here.
///     <para>
///     Order is priority, and the matcher takes the FIRST family in the chain
///     that HAS the glyph. So broad Latin/Cyrillic faces go first: they carry no
///     CJK and therefore cannot steal a CJK character from the faces below.
///     Putting CJK first is the trap — those faces DO carry accented Latin and
///     Cyrillic, so they capture them.
///     </para>
///     <para>
///     <b>Measured on Windows 11 / Avalonia 12.1.1 — read this
///     before rewriting the chain, and do NOT claim it fixes tofu.</b> Avalonia's
///     Skia font manager already matches CJK with no chain configured at all:
///     <c>八</c> and <c>ヨ</c> resolve to Yu Gothic UI, <c>한</c> to Malgun
///     Gothic. The chain was added while investigating an owner report of
///     "mangled" Japanese metadata and that report is NOT explained
///     by it — the tags themselves were read correctly (<c>artist='ヨルシカ'</c>
///     in the log) and CJK resolves either way. What the chain actually fixes is
///     the arbitrary pick for everything else: with no chain, <c>é</c> and
///     <c>Ж</c> resolved to <b>Malgun Gothic</b>, a Korean face, and with it they
///     resolve to Segoe UI. It also makes the choice deterministic rather than
///     dependent on the matcher's cache order.
///     </para>
/// </summary>
internal static class TextFallbackFonts
{
    /// <summary>Family names in fallback order. Public for the lint that asserts
    /// the chain still covers the scripts the app is expected to display.</summary>
    internal static readonly string[] FamilyNames =
    [
        // Broad-coverage UI faces FIRST. The matcher walks the chain and takes
        // the first family that HAS the glyph, so a face with no CJK cannot
        // steal a CJK character from the ones below — while a CJK face put
        // first WOULD steal every accented Latin and Cyrillic character,
        // because it has those glyphs too.
        "Segoe UI",              // Windows: Latin, Cyrillic, Greek, Arabic, Hebrew
        "Helvetica Neue",        // macOS
        "Noto Sans",             // Linux
        // Symbols the song data carries that no text face above has, and that
        // a CJK face would otherwise draw at full width: the flat and sharp in
        // a tuning ("E♭ Standard").
        "Segoe UI Symbol",       // Windows: ♭ ♯ ♮
        // CJK.
        "Yu Gothic UI",          // Windows, JP
        "Meiryo UI",             // Windows, JP (older installs)
        "Microsoft YaHei UI",    // Windows, Simplified Chinese
        "Microsoft JhengHei UI", // Windows, Traditional Chinese
        "Malgun Gothic",         // Windows, Korean
        "Hiragino Sans",         // macOS, JP
        "PingFang SC",           // macOS, Simplified Chinese
        "Apple SD Gothic Neo",   // macOS, Korean
        "Noto Sans CJK JP",      // Linux
        "Noto Sans CJK SC",
        "Noto Sans CJK KR",
        // Everything else a song's metadata realistically carries.
        "Nirmala UI",            // Windows, Indic
        "Segoe UI Emoji",        // Windows, emoji
        "Noto Color Emoji",      // Linux, emoji
        "DejaVu Sans"            // Linux, last resort
    ];

    /// <summary>The chain as Avalonia's font manager wants it.</summary>
    internal static FontManagerOptions Options() => new()
    {
        FontFallbacks = FamilyNames
            .Select(name => new FontFallback { FontFamily = new FontFamily(name) })
            .ToArray()
    };
}
