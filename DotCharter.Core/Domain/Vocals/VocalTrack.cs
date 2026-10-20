using System.Collections.ObjectModel;

namespace DotCharter.Core.Domain.Vocals;

public class VocalTrack
{
    public byte ColorA { get; set; } = 255;
    public byte ColorB { get; set; } = 255;
    public byte ColorG { get; set; } = 255;
    public byte ColorR { get; set; } = 255;
    public string Name { get; set; } = "Vocals";

    public ObservableCollection<LyricPhrase> Phrases { get; set; } = [];
}
