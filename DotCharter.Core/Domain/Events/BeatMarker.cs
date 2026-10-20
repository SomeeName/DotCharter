namespace DotCharter.Core.Domain.Events;

public class BeatMarker
{
    public int BeatsInMeasure { get; set; } = 4;
    public double Bpm { get; set; } = 120.0;
    public bool IsAnchor { get; set; }
    public bool IsMeasure { get; set; }
    public int NoteDenominator { get; set; } = 4;
    public double Time { get; set; }

    public BeatMarker Clone()
    {
        var clone = new BeatMarker();
        clone.CopyFrom(this);
        return clone;
    }

    public void CopyFrom(BeatMarker other)
    {
        BeatsInMeasure = other.BeatsInMeasure;
        Bpm = other.Bpm;
        IsAnchor = other.IsAnchor;
        IsMeasure = other.IsMeasure;
        NoteDenominator = other.NoteDenominator;
        Time = other.Time;
    }
}
