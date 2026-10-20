using System.Xml.Linq;
using DotCharter.Services.Parsing.GuitarPro;

namespace DotCharter.Tests.Services.Parsing.GuitarPro;

/// <summary>
/// The gpif <c>&lt;Note&gt;</c> element parser. Regression focus: the
/// <c>&lt;Accent&gt;</c> child used to be applied only when the target was a
/// stand-alone <see cref="GuitarNote"/> — accents on chord beats vanished.
/// </summary>
public sealed class GpNoteParserTests
{
    private static XElement Note(int stringIndex, int fret, bool accent)
    {
        var note = new XElement("Note",
            new XElement("Properties",
                new XElement("Property", new XAttribute("name", "String"),
                    new XElement("String", stringIndex)),
                new XElement("Property", new XAttribute("name", "Fret"),
                    new XElement("Fret", fret))));
        if (accent) note.AddFirst(new XElement("Accent", 4));
        return note;
    }

    [Fact]
    public void ChordNote_WithAccentElement_CarriesAccent()
    {
        var parser = new GpNoteParser();
        var cn = parser.ParseChordNote(Note(2, 5, accent: true), FractionalPosition.Zero,
            new FractionalPosition(1), stringCount: 6, hasTremolo: false);

        Assert.True(cn.Accent);
        Assert.Equal(2, cn.StringIndex);
        Assert.Equal(5, cn.Fret);
    }

    [Fact]
    public void ChordNote_WithoutAccentElement_HasNoAccent()
    {
        var parser = new GpNoteParser();
        var cn = parser.ParseChordNote(Note(2, 5, accent: false), FractionalPosition.Zero,
            new FractionalPosition(1), stringCount: 6, hasTremolo: false);

        Assert.False(cn.Accent);
    }

    [Fact]
    public void SingleNote_WithAccentElement_CarriesAccent()
    {
        var parser = new GpNoteParser();
        var note = parser.ParseNote(Note(0, 3, accent: true), FractionalPosition.Zero,
            new FractionalPosition(1), stringCount: 6, hasTremolo: false);

        Assert.True(note.Accent);
    }
}
