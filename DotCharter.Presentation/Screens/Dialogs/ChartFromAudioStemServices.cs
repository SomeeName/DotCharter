using DotCharter.Services.Audio.StemSeparation;
using DotCharter.Services.Import.Audio;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>Chart from audio's stem strip: the stem locator and the app's own separation, as one part.</summary>
public sealed record ChartFromAudioStemServices(IChartFromAudioStemLocator Locator, ISongStemSeparation Separation);
