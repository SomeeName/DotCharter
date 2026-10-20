using System.Globalization;
using System.Xml.Linq;
using BeatMarker = DotCharter.Core.Domain.Events.BeatMarker;
using ChartProject = DotCharter.Core.Domain.Project.ChartProject;

namespace DotCharter.Services.Parsing.Rscp;

/// <summary>
///     Parses beat/tempo information from RSCP chart files
/// </summary>
public class RscpBeatsParser
{
    public void ParseBeats(XDocument doc, ChartProject project)
    {
        var beatNodes = doc.Descendants("beat2").ToList();
        var beatsTimes = new List<double>();

        foreach (var b in beatNodes)
        {
            if (double.TryParse(b.Attribute("t")?.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var t))
            {
                beatsTimes.Add(t);
                var sig = b.Attribute("sig")?.Value ?? "4/4";
                var sigParts = sig.Split('/');
                var num = 4;
                var den = 4;
                if (sigParts.Length == 2)
                {
                    int.TryParse(sigParts[0], CultureInfo.InvariantCulture, out num);
                    int.TryParse(sigParts[1], CultureInfo.InvariantCulture, out den);
                    // A corrupt "4/0" (or negative) signature otherwise
                    // divides to an Infinity BPM that poisons the whole
                    // tempo map without ever throwing.
                    if (num <= 0) num = 4;
                    if (den <= 0) den = 4;
                }

                var isMeasure = b.Attribute("bar")?.Value == "T";
                var isAnchor = b.Attribute("anchor")?.Value == "T";

                project.Beats.Add(new BeatMarker
                {
                    Time = t,
                    BeatsInMeasure = num,
                    NoteDenominator = den,
                    IsMeasure = isMeasure,
                    IsAnchor = isAnchor
                });
            }
        }

        for (var i = 0; i < project.Beats.Count - 1; i++)
        {
            var msPerBeat = project.Beats[i + 1].Time - project.Beats[i].Time;
            if (msPerBeat > 0)
                project.Beats[i].Bpm = Math.Round(60000.0 / msPerBeat * 4.0 / project.Beats[i].NoteDenominator, 3);
        }

        if (project.Beats.Count > 1) project.Beats[^1].Bpm = project.Beats[^2].Bpm;

        if (beatsTimes.Count >= 2)
        {
            var msPerBeat = beatsTimes[1] - beatsTimes[0];
            if (msPerBeat > 0)
                project.Bpm = Math.Round(60000.0 / msPerBeat * 4.0 / project.Beats[0].NoteDenominator, 2);
        }
    }
}
