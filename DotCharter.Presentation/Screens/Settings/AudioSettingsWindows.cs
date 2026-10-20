using DotCharter.Tone.Audio.Abstractions;
using DotCharter.Services.Infrastructure.Shell;

namespace DotCharter.Presentation.Screens.Settings;

/// <summary>The windows the Audio tab opens: the latency wizard, and the ASIO panel over the main window.</summary>
public sealed record AudioSettingsWindows(ILatencyCalibrationPrompt LatencyWizard, IMainWindowHandle? MainWindow = null);
