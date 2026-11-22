using System.Globalization;
using System.Xml.Linq;

namespace DotCharter.Services.Parsing.Rscp;

/// <summary>Optional native-project metadata; interchange lyric writers keep their existing wire format.</summary>
internal static class LyricReviewSerialization
{
    internal static void Write(XElement node, LyricEvent lyric)
    {
        if (lyric.AlignmentConfidence is >= 0 and <= 1)
            node.Add(new XAttribute("dcAlignConfidence", lyric.AlignmentConfidence.Value.ToString("R", CultureInfo.InvariantCulture)));
        if (lyric.ReviewReasons != LyricReviewReason.None)
            node.Add(new XAttribute("dcAlignReview", lyric.ReviewReasons));
        if (lyric.IsSustainShortened)
            node.Add(new XAttribute("dcShortSustain", true));
    }

    internal static LyricEvent Read(XElement node, LyricEvent lyric)
    {
        if (double.TryParse(node.Attribute("dcAlignConfidence")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var score)
            && double.IsFinite(score) && score is >= 0 and <= 1)
        {
            lyric.AlignmentConfidence = score;
        }
        if (Enum.TryParse<LyricReviewReason>(node.Attribute("dcAlignReview")?.Value, out var reasons))
            lyric.ReviewReasons = reasons;
        lyric.IsSustainShortened = bool.TryParse(node.Attribute("dcShortSustain")?.Value, out var shortened) && shortened;
        return lyric;
    }
}
