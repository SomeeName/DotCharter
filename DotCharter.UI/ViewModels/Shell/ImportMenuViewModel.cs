// File ▸ New project, the imports and Audio from YouTube: each verb hands its
// source (a picker, a dropped file, a link) to its Presentation launcher, and a
// launcher that is not registered is logged rather than silently doing nothing.
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Presentation.Screens.Editor;
using DotCharter.Tone.UI.ViewModels.Shell;

namespace DotCharter.UI.ViewModels.Shell;

public sealed partial class ImportMenuViewModel : ViewModelBase
{
    private readonly IAppLogger _logger;
    private readonly NewProjectLauncher? _newProject;
    private readonly EofImportLauncher? _eofImport;
    private readonly RsXmlImportLauncher? _rsXmlImport;
    private readonly MidiImportLauncher? _midiImport;
    private readonly GpImportLauncher? _gpImport;
    private readonly YoutubeAudioLauncher? _youtubeAudio;
    private readonly PackageConversionLauncher? _packageConversion;
    private readonly AlignToRecordingOffer _alignOffer;
    private readonly ShapeCleanupOffer _cleanupOffer;
    private readonly GpLinkSuggestionOffer? _linkOffer;

    public ImportMenuViewModel(
        IAppLogger logger,
        AlignToRecordingOffer alignOffer,
        ShapeCleanupOffer cleanupOffer,
        NewProjectLauncher? newProject = null,
        EofImportLauncher? eofImport = null,
        RsXmlImportLauncher? rsXmlImport = null,
        MidiImportLauncher? midiImport = null,
        GpImportLauncher? gpImport = null,
        YoutubeAudioLauncher? youtubeAudio = null,
        GpLinkSuggestionOffer? linkOffer = null,
        PackageConversionLauncher? packageConversion = null)
    {
        _logger = logger;
        _alignOffer = alignOffer;
        _cleanupOffer = cleanupOffer;
        _newProject = newProject;
        _eofImport = eofImport;
        _rsXmlImport = rsXmlImport;
        _midiImport = midiImport;
        _gpImport = gpImport;
        _youtubeAudio = youtubeAudio;
        _linkOffer = linkOffer;
        _packageConversion = packageConversion;
    }

    /// <summary>File ▸ New project, from the menu, the launch card, the palette and its keybind.</summary>
    [RelayCommand]
    private void OpenNewProject() => LaunchImport(_newProject, l => l.OpenAsync(), "new project");

    /// <summary>File ▸ Import ▸ Editor on Fire: the picker, then the dialog.</summary>
    [RelayCommand]
    private void ImportEof() =>
        LaunchImport(_eofImport, async l => await OfferCleanupAsync(await l.PickAsync(), "Editor on Fire import"), "Editor on Fire import");

    /// <summary>File ▸ Import ▸ Rocksmith XML: the picker, then the progress card.</summary>
    [RelayCommand]
    private void ImportRsXml() =>
        LaunchImport(_rsXmlImport, async l => await OfferCleanupAsync(await l.PickAsync(), "Rocksmith XML import"), "Rocksmith XML import");

    /// <summary>File ▸ Import ▸ MIDI: the picker, then the dialog.</summary>
    [RelayCommand]
    private void ImportMidi() => LaunchImport(_midiImport, async l => OfferAlignment(await l.PickAsync(), "MIDI import"), "MIDI import");

    /// <summary>File ▸ Import ▸ Guitar Pro: the picker, the track list read off the UI thread, then the dialog.</summary>
    [RelayCommand]
    private void ImportGuitarPro() =>
        LaunchImport(_gpImport, async l => await AfterGpImportAsync(await l.PickAsync()), "Guitar Pro import");

    /// <summary>A dropped or recent .eof, straight to its track list.</summary>
    internal void OpenEofImport(string filePath) =>
        LaunchImport(_eofImport, async l => await OfferCleanupAsync(await l.OpenAsync(filePath), "Editor on Fire import"), "Editor on Fire import");

    /// <summary>A dropped or recent .mid; the launcher refuses when no project is open.</summary>
    internal void OpenMidiImport(string filePath) =>
        LaunchImport(_midiImport, async l => OfferAlignment(await l.OpenAsync(filePath), "MIDI import"), "MIDI import");

    /// <summary>A dropped or recent RS XML, merged into the open project.</summary>
    internal void OpenRsXmlImport(string filePath) =>
        LaunchImport(_rsXmlImport, async l => await OfferCleanupAsync(await l.OpenAsync(filePath), "Rocksmith XML import"), "Rocksmith XML import");

    /// <summary>A dropped Guitar Pro score, straight to its track list — no picker.</summary>
    internal void OpenGpImport(string filePath) =>
        LaunchImport(_gpImport, async l => await AfterGpImportAsync(await l.OpenAsync(filePath)), "Guitar Pro import");

    /// <summary>A Guitar Pro import: first what can be linked, then the shapes to tidy, then Align to recording on the status rail.</summary>
    private async Task AfterGpImportAsync(bool imported)
    {
        if (imported && _linkOffer != null) await _linkOffer.OfferAsync();
        await OfferCleanupAsync(imported, "Guitar Pro import");
        OfferAlignment(imported, "Guitar Pro import");
    }

    /// <summary>An import that brought in shapes: Clean up shapes opens when it finds anything, and stays away from a tidy chart.</summary>
    private async Task OfferCleanupAsync(bool imported, string what)
    {
        if (imported) await _cleanupOffer.OfferAsync(what);
    }

    /// <summary>A tab import lands on the WRITTEN tempo: offer Align to recording next.</summary>
    private void OfferAlignment(bool imported, string what)
    {
        if (imported) _alignOffer.Offer(what);
    }

    private void LaunchImport<T>(T? launcher, Func<T, Task> run, string what) where T : class
    {
        if (launcher == null)
        {
            _logger.Error(AppLogCategory.Import, $"The {what} cannot open: no launcher is registered.");
            return;
        }

        DialogLaunch.Run(_logger, () => run(launcher), what);
    }

    /// <summary>File ▸ Import ▸ Convert PSARC to DotCharter song: the picker, then every package becomes a .dcsong. Present only when a plugin reads packages.</summary>
    [RelayCommand]
    private void ConvertPackages() => LaunchImport(_packageConversion, l => l.PickAsync(), "package conversion");

    /// <summary>File ▸ Audio from YouTube: the link dialog, then the attach. Absent from a build with the contested features off.</summary>
    [RelayCommand]
    private void AttachYoutubeAudio() => LaunchImport(_youtubeAudio, l => l.OpenAsync(), "Audio from YouTube");
}
