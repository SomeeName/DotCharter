using System.Collections.ObjectModel;
using DotCharter.Core.Domain.Primitives;

namespace DotCharter.Core.Domain.Guitar;

public interface IGuitarSound
{
    bool Accent { get; set; }
    int StringIndex { get; set; }
    int Fret { get; set; }
    FractionalPosition Sustain { get; set; }
    MuteType Mute { get; set; }
    HopoType Hopo { get; set; }
    HarmonicType Harmonic { get; set; }
    BassPickingType BassPicking { get; set; }
    bool Vibrato { get; set; }
    int VibratoStrength { get; set; }
    bool Tremolo { get; set; }
    bool LinkNext { get; set; }
    int? SlideTo { get; set; }
    bool UnpitchedSlide { get; set; }
    ObservableCollection<BendValue> BendValues { get; set; }
}
