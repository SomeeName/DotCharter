namespace DotCharter.Core.Domain.Project;

public class Stem
{
    public bool Local { get; set; } = true;
    public string Name { get; set; } = "song";
    public double Offset { get; set; }
    public string Path { get; set; } = "song.wav";
    public double Volume { get; set; } = 1.0;
    public bool Muted { get; set; }
    public bool Solo { get; set; }
}
