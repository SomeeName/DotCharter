using DotCharter.Services.Export.Sng.Model;

namespace DotCharter.Services.Export.Sng;

/// <summary>
/// The default lyric-font symbol definitions written into vocals SNG files: the
/// 192 glyph rectangles of the game's built-in lyrics texture. They are generated,
/// not shipped as a file. The glyphs sit on a regular grid, 21 per column, 96/1024
/// apart horizontally and 48/1024 apart vertically; the only free data is one
/// integer pixel height per glyph. <c>DefaultVocalSymbolsTests</c> pins the SHA-256
/// of the serialized table (8,452 bytes), so any drift in the string, a width or the
/// grid arithmetic fails there.
/// </summary>
internal static class DefaultVocalSymbols
{
    private const int Rows = 21;
    private const double Unit = 1024.0;

    private static readonly Lazy<IReadOnlyList<RsSymbolDefinition>> Cached = new(Build);

    /// <summary>The glyph of each cell, in cell order (column-major, 21 rows per column).</summary>
    private const string Glyphs =
        "\uFFFF\u0020\u0021\u0022\u0023\u0024\u0025\u0026\u0027\u0028\u0029\u002A\u002B\u002C\u002D\u002E" +
        "\u002F\u0030\u0031\u0032\u0033\u0034\u0035\u0036\u0037\u0038\u0039\u003A\u003B\u003C\u003D\u003E" +
        "\u003F\u0040\u0041\u0042\u0043\u0044\u0045\u0046\u0047\u0048\u0049\u004A\u004B\u004C\u004D\u004E" +
        "\u004F\u0050\u0051\u0052\u0053\u0054\u0055\u0056\u0057\u0058\u0059\u005A\u005B\u005C\u005D\u005E" +
        "\u005F\u0061\u0062\u0063\u0064\u0065\u0066\u0067\u0068\u0069\u006A\u006B\u006C\u006D\u006E\u006F" +
        "\u0070\u0071\u0072\u0073\u0074\u0075\u0076\u0077\u0078\u0079\u007A\u007B\u007C\u007D\u007E\u00A1" +
        "\u00A2\u00A5\u00A6\u00A7\u00A8\u00AA\u00AB\u00B0\u00B2\u00B3\u00B4\u00B6\u00B7\u00B8\u00B9\u00BA" +
        "\u00BB\u00BC\u00BD\u00BE\u00BF\u00C0\u00C1\u00C2\u00C4\u00C5\u00C6\u00C7\u00C8\u00C9\u00CA\u00CB" +
        "\u00CC\u00CE\u00CF\u00D1\u00D2\u00D3\u00D4\u00D6\u00D8\u00D9\u00DA\u00DB\u00DC\u00DE\u00DF\u00E0" +
        "\u00E1\u00E2\u00E4\u00E5\u00E6\u00E7\u00E8\u00E9\u00EA\u00EB\u00EC\u00ED\u00EE\u00EF\u00F1\u00F2" +
        "\u00F3\u00F4\u00F6\u00F8\u00F9\u00FA\u00FB\u00FC\u0130\u013A\u0152\u0153\u0160\u0161\u017E\u1E90" +
        "\u2012\u2018\u2019\u201E\u201F\u2026\u2035\u203E\u20A4\u20AC\u2122\u2400\u24B8\u24C7\u2605\u27A8";

    /// <summary>Pixel width of each glyph, in cell order.</summary>
    private static readonly int[] Widths =
    [
        17,9,14,19,26,24,32,29,13,15,15,19,24,17,16,15,23,26,17,25,24,
        26,24,25,25,24,26,15,17,24,24,24,23,32,31,24,26,27,21,21,31,27,
        14,20,27,20,34,29,32,23,33,25,24,22,26,30,40,29,28,26,15,23,15,
        24,24,24,24,20,24,23,18,24,23,14,14,24,13,31,23,25,24,24,19,20,
        17,23,25,34,27,26,23,16,13,16,24,14,20,28,13,24,20,19,23,18,19,
        19,16,26,15,14,14,19,23,33,33,33,22,31,31,31,31,31,38,26,21,21,
        21,21,16,20,20,29,32,32,32,32,33,26,26,26,26,23,24,24,24,24,24,
        24,33,20,23,23,23,23,16,16,20,20,23,25,25,25,25,27,23,23,23,23,
        9,9,39,35,24,20,23,17,17,17,17,23,17,39,17,17,17,27,39,20,17,
        17,45,45
    ];

    public static IReadOnlyList<RsSymbolDefinition> Load() => Cached.Value;

    private static IReadOnlyList<RsSymbolDefinition> Build()
    {
        var result = new List<RsSymbolDefinition>(Glyphs.Length);
        for (var i = 0; i < Glyphs.Length; i++)
        {
            var left = i / Rows * 96 / Unit;
            var top = i % Rows * 48 / Unit;
            var width = Widths[i];

            float[] outer = [(float)left, (float)top, (float)(left + 104 / Unit), (float)(top + width / Unit)];
            float[] inner = [(float)(left + 12 / Unit), (float)(top + 4 / Unit), (float)(left + 92 / Unit), (float)(top + (width - 4) / Unit)];
            result.Add(new RsSymbolDefinition(Glyphs[i].ToString(), outer, inner));
        }

        return result;
    }
}
