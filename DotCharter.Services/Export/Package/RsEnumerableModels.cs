using System.Text;

namespace DotCharter.Services.Export.Package;

/// <summary>
/// The two <c>flatmodels/rs/rsenumerable_*.flat</c> entries every song psarc carries:
/// small XML model declarations (a name, a numeric model id, and a list of
/// <c>AssetID</c> properties with their numeric ids). They are generated here rather
/// than shipped as files; <c>RsEnumerableModelsTests</c> pins the SHA-256 of the exact
/// bytes (UTF-8 BOM, LF, no trailing newline) the game has always been given.
/// </summary>
internal static class RsEnumerableModels
{
    private const string RootName = "RSEnumerable_Root";
    private const uint RootId = 1917312803;

    private const string SongName = "RSEnumerable_Song";
    private const uint SongId = 3871465268;

    private static readonly (string Name, uint Id)[] RootProperties =
    [
        ("Manifest", 3402662178),
        ("Header", 3529813182)
    ];

    private static readonly (string Name, uint Id)[] SongProperties =
    [
        ("XmlAsset", 194332538),
        ("SngAsset", 2132105560),
        ("AlbumArtSmall", 2295103720),
        ("AlbumArtMedium", 3067535336),
        ("AlbumArtLarge", 223887511),
        ("LyricArt", 3842719743),
        ("ShowLightsXMLAsset", 2024527203),
        ("SoundBank", 4177482269),
        ("PreviewSoundBank", 1820138184),
        ("Manifest", 3402662178),
        ("Header", 3529813182)
    ];

    /// <summary>Bytes of <c>rsenumerable_root.flat</c>.</summary>
    public static byte[] Root() => Build(RootName, RootId, mixin: false, RootProperties);

    /// <summary>Bytes of <c>rsenumerable_song.flat</c>.</summary>
    public static byte[] Song() => Build(SongName, SongId, mixin: true, SongProperties);

    private static byte[] Build(string name, uint id, bool mixin, (string Name, uint Id)[] properties)
    {
        var text = new StringBuilder();
        text.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n");
        text.Append("<model\n");
        text.Append("  name=\"").Append(name).Append("\"\n");
        text.Append("  id=\"").Append(id).Append("\">\n");
        text.Append("  <trait\n    value=\"Active\" />\n");
        if (mixin)
        {
            text.Append("  <mixin\n");
            text.Append("    name=\"").Append(RootName).Append("\"\n");
            text.Append("    id=\"").Append(RootId).Append("\"\n");
            text.Append("    type=\"Active\" />\n");
        }

        foreach (var (propertyName, propertyId) in properties)
        {
            text.Append("  <property\n    type=\"AssetID\"\n");
            text.Append("    name=\"").Append(propertyName).Append("\"\n");
            text.Append("    id=\"").Append(propertyId).Append("\">\n");
            text.Append("    <set\n      value=\"\" />\n  </property>\n");
        }

        text.Append("</model>");
        return [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(text.ToString())];
    }
}
