using System.Xml.Linq;

namespace DotCharter.Services.Parsing.Rscp;

/// <summary>
///     The <c>.dcproj</c> spelling of <see cref="Arrangement.AudioCheckSpots" />: a trailing
///     <c>&lt;dcAudioCheck&gt;</c> per arrangement with one <c>&lt;spot p=".." reason=".."/&gt;</c> each, absent when
///     there is none, so every other chart stays byte-identical. A spot whose reason this build does not know is
///     dropped on read rather than guessed.
/// </summary>
internal static class RscpAudioCheck
{
    public const string Element = "dcAudioCheck";

    public static void Write(XElement arrNode, Arrangement arrangement)
    {
        if (arrangement.AudioCheckSpots.Count == 0) return;
        arrNode.Add(new XElement(Element, arrangement.AudioCheckSpots.Select(s =>
            new XElement("spot", new XAttribute("p", s.Position.ToString()), new XAttribute("reason", s.Reason)))));
    }

    public static void Read(XElement arrNode, Arrangement arrangement)
    {
        if (arrNode.Element(Element) is not { } node) return;
        foreach (var spot in node.Elements("spot"))
        {
            if (!RscpParsingUtilities.TryParseFraction(spot.Attribute("p")?.Value, out var position)) continue;
            if (!Enum.TryParse<AudioCheckReason>(spot.Attribute("reason")?.Value, out var reason) || !Enum.IsDefined(reason)) continue;
            arrangement.AudioCheckSpots.Add(new AudioCheckSpot(position, reason));
        }
    }
}
