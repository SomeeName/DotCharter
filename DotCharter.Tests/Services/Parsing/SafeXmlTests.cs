using System.Text;
using System.Xml;
using DotCharter.Services.Parsing;

namespace DotCharter.Tests.Services.Parsing;

/// <summary>
/// <see cref="SafeXml"/> is the hardened XML gateway for content out of
/// untrusted containers (PSARC entries, Guitar Pro archives, .rscp projects).
/// Its whole reason to exist is defensive, yet it had no tests — this pins the
/// hardening its own doc-comment promises so a TFM bump or a "simplify to
/// XDocument.Load" refactor cannot silently reopen XXE / an entity bomb.
/// </summary>
public sealed class SafeXmlTests
{
    private static Stream Utf8(string xml) => new MemoryStream(Encoding.UTF8.GetBytes(xml));

    [Fact]
    public void LoadFromStream_WellFormedXml_ParsesNormally()
    {
        using var stream = Utf8("<project><chart songName=\"Test\"/></project>");
        var doc = SafeXml.LoadFromStream(stream);
        Assert.Equal("project", doc.Root!.Name.LocalName);
        Assert.Equal("Test", doc.Root!.Element("chart")!.Attribute("songName")!.Value);
    }

    [Fact]
    public void LoadFromStream_ExternalEntityDoctype_IsRejected_NoFileDisclosure()
    {
        // The classic XXE: a DOCTYPE declaring an external SYSTEM entity. With
        // DtdProcessing.Prohibit the reader throws on the DOCTYPE itself — the
        // entity is never resolved, so no local file is ever read.
        const string xxe =
            "<?xml version=\"1.0\"?>" +
            "<!DOCTYPE root [<!ENTITY secret SYSTEM \"file:///etc/passwd\">]>" +
            "<root>&secret;</root>";
        using var stream = Utf8(xxe);
        Assert.ThrowsAny<XmlException>(() => SafeXml.LoadFromStream(stream));
    }

    [Fact]
    public void LoadFromStream_InternalDtd_IsRejected()
    {
        // Even a purely-internal DTD (the vehicle for a billion-laughs entity
        // bomb) is prohibited outright, so the expansion can never begin.
        const string dtd =
            "<?xml version=\"1.0\"?>" +
            "<!DOCTYPE lolz [<!ENTITY lol \"lol\"><!ENTITY lol2 \"&lol;&lol;&lol;\">]>" +
            "<lolz>&lol2;</lolz>";
        using var stream = Utf8(dtd);
        Assert.ThrowsAny<XmlException>(() => SafeXml.LoadFromStream(stream));
    }

    [Fact]
    public void LoadFromStream_MalformedXml_Throws()
    {
        using var stream = Utf8("<project><chart></project>"); // unterminated <chart>
        Assert.ThrowsAny<XmlException>(() => SafeXml.LoadFromStream(stream));
    }

    [Fact]
    public void LoadFromStream_EmptyStream_Throws()
    {
        using var stream = new MemoryStream([]);
        Assert.ThrowsAny<XmlException>(() => SafeXml.LoadFromStream(stream));
    }

    [Fact]
    public void LoadFromFile_MissingFile_Throws()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcharter-safexml-missing-{Guid.NewGuid():N}.xml");
        Assert.ThrowsAny<FileNotFoundException>(() => SafeXml.LoadFromFile(path));
    }

    [Fact]
    public void LoadFromFile_WellFormed_RoundTripsThroughDisk()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcharter-safexml-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(path, "<root><child value=\"42\"/></root>");
            var doc = SafeXml.LoadFromFile(path);
            Assert.Equal("42", doc.Root!.Element("child")!.Attribute("value")!.Value);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
