using System.Globalization;
using System.Xml.Linq;

namespace DotCharter.Services.Parsing.Rscp;

internal static class RscpGpSource
{
    public const string RootElement = "dcGpSource";
    private const string Version = "1";

    public static bool Recognizes(XElement node) => (string?)node.Attribute("v") == Version;

    public static void Read(XDocument doc, ChartProject project)
    {
        var node = doc.Root?.Elements(RootElement).FirstOrDefault(Recognizes);
        if (node == null) return;
        var bars = new List<GpSourceBar>();
        foreach (var bar in node.Elements("bar"))
        {
            var entry = ReadBar(bar);
            if (entry == null) return;
            bars.Add(entry);
        }

        project.GpSourceBars = [.. bars];
    }

    private static GpSourceBar? ReadBar(XElement bar)
    {
        int Number(string name, int fallback = -1) =>
            int.TryParse((string?)bar.Attribute(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value : fallback;
        var start = Number("start");
        var count = Number("count");
        var beats = Number("beats");
        var denominator = Number("unit");
        var written = Number("written", -2);
        var pass = Number("pass", 1);
        var endings = Number("endings", 0);
        if (start < 0 || count < 1 || beats < 1 || denominator < 1 || written < -1 || pass < 1 || endings < 0) return null;

        return new GpSourceBar
        {
            StartBeat = start, BeatCount = count, BeatsInMeasure = beats, NoteDenominator = denominator,
            WrittenBar = written, Pass = pass, EndingMask = endings, Marker = (string?)bar.Attribute("marker") ?? "",
            StartsBlock = (string?)bar.Attribute("opens") == "1",
            EndsBlock = (string?)bar.Attribute("closes") == "1"
        };
    }

    public static void Write(XElement root, ChartProject project)
    {
        if (project.GpSourceBars.Count == 0) return;
        root.Add(new XElement(RootElement, new XAttribute("v", Version), project.GpSourceBars.Select(WriteBar)));
    }

    private static XElement WriteBar(GpSourceBar bar) =>
        new("bar", new XAttribute("start", bar.StartBeat), new XAttribute("count", bar.BeatCount),
            new XAttribute("beats", bar.BeatsInMeasure), new XAttribute("unit", bar.NoteDenominator),
            new XAttribute("written", bar.WrittenBar), new XAttribute("pass", bar.Pass),
            new XAttribute("endings", bar.EndingMask),
            bar.StartsBlock ? new XAttribute("opens", "1") : null,
            bar.EndsBlock ? new XAttribute("closes", "1") : null,
            bar.Marker.Length > 0 ? new XAttribute("marker", bar.Marker) : null);
}
