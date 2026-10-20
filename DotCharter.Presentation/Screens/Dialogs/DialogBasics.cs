using DotCharter.Localization;
using DotCharter.Tone.Logging;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>What every dialog and launcher needs beside its own services: its words and its log.
/// One parameter, so the seven-parameter constructor rule leaves room for the rest.</summary>
public sealed class DialogBasics(ILocalizationService localization, IAppLogger logger)
{
    public ILocalizationService Localization { get; } = localization;

    public IAppLogger Logger { get; } = logger;
}
