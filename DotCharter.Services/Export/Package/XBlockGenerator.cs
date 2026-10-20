using System.Text;
using System.Xml;

namespace DotCharter.Services.Export.Package;

/// <summary>
/// Generates the game xblock ("gamexblocks/nsongs/{key}.xblock") describing
/// the entity set for each arrangement.
/// Ported from Rocksmith2014.NET XBlock.fs (PC branch).
/// </summary>
internal static class XBlockGenerator
{
    public static byte[] Create(RsPackageMetadata meta, IReadOnlyList<RsPackageArrangement> arrangements)
    {
        var dlcName = meta.DlcKey.ToLowerInvariant();

        using var memory = new MemoryStream();
        var settings = new XmlWriterSettings
        {
            Indent = true,
            Encoding = new UTF8Encoding(true),
            CloseOutput = false
        };

        using (var writer = XmlWriter.Create(memory, settings))
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("game");
            writer.WriteStartElement("entitySet");

            foreach (var arrangement in arrangements)
            {
                writer.WriteStartElement("entity");
                writer.WriteAttributeString("id", arrangement.PersistentId.ToString("N"));
                writer.WriteAttributeString("modelName", "RSEnumerable_Song");
                writer.WriteAttributeString("name", $"{meta.DlcKey}_{arrangement.ArrangementName}");
                writer.WriteAttributeString("iterations", "0");

                writer.WriteStartElement("properties");
                WriteProperty(writer, "Header", $"urn:database:hsan-db:songs_dlc_{dlcName}");
                WriteProperty(writer, "Manifest", $"urn:database:json-db:{dlcName}_{arrangement.PartitionName}");
                WriteProperty(writer, "SngAsset",
                    $"urn:application:musicgame-song:{dlcName}_{arrangement.PartitionName}");
                WriteProperty(writer, "AlbumArtSmall", $"urn:image:dds:album_{dlcName}_64");
                WriteProperty(writer, "AlbumArtMedium", $"urn:image:dds:album_{dlcName}_128");
                WriteProperty(writer, "AlbumArtLarge", $"urn:image:dds:album_{dlcName}_256");
                WriteProperty(writer, "LyricArt", "");
                WriteProperty(writer, "ShowLightsXMLAsset", $"urn:application:xml:{dlcName}_showlights");
                WriteProperty(writer, "SoundBank", $"urn:audio:wwise-sound-bank:song_{dlcName}");
                WriteProperty(writer, "PreviewSoundBank", $"urn:audio:wwise-sound-bank:song_{dlcName}_preview");
                writer.WriteEndElement(); // properties

                writer.WriteEndElement(); // entity
            }

            writer.WriteEndElement(); // entitySet
            writer.WriteEndElement(); // game
            writer.WriteEndDocument();
        }

        return memory.ToArray();
    }

    private static void WriteProperty(XmlWriter writer, string name, string value)
    {
        writer.WriteStartElement("property");
        writer.WriteAttributeString("name", name);
        writer.WriteStartElement("set");
        writer.WriteAttributeString("value", value);
        writer.WriteEndElement();
        writer.WriteEndElement();
    }
}
