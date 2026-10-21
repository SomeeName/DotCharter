using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Core.Domain.Project;
using DotCharter.Services.Diagnostics;
using DotCharter.Services.Export;
using DotCharter.Services.Export.DlcBuilder;
using DotCharter.Services.State;
using DotCharter.Tone.Infrastructure.Configuration;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>What the export dialog holds that a DLC Builder project is written from, read at the moment of the click.</summary>
internal sealed record DlcBuilderProjectInput(ChartProject Snapshot, string AudioPath, string AlbumArtPath, string OutputPath);

/// <summary>
///     The export dialog's "Export DLC Builder project": asks for a folder, writes the chart there as a DLC
///     Builder project, and says where it went. A standard chart needs an encrypted package, which
///     DLC Builder builds from that project.
/// </summary>
internal sealed partial class RocksmithExportDlcBuilder : ObservableObject
{
    private readonly ChartEditDialogServices _edit;
    private readonly RocksmithExportServices _io;
    private readonly IDlcBuilderProjectWriter _writer;
    private readonly RocksmithExportPickers _pickers;
    private readonly Func<DlcBuilderProjectInput?> _input;

    [ObservableProperty][NotifyPropertyChangedFor(nameof(HasMessage))] private string _message = "";

    public RocksmithExportDlcBuilder(ChartEditDialogServices edit, RocksmithExportServices io, IDlcBuilderProjectWriter writer,
        RocksmithExportPickers pickers, Func<DlcBuilderProjectInput?> input)
    {
        _edit = edit;
        _io = io;
        _writer = writer;
        _pickers = pickers;
        _input = input;
        Command = new AsyncRelayCommand(ExportAsync);
    }

    public ICommand Command { get; }

    public bool HasMessage => Message.Length > 0;

    private async Task ExportAsync()
    {
        if (_input() is not { } input) return;
        var loc = _edit.Localization;
        var start = Path.GetDirectoryName(input.OutputPath);
        if (await _pickers.FolderAsync(start ?? "", "Picker_SelectDlcBuilderFolder") is not { Length: > 0 } folder) return;
        Message = "";
        try
        {
            var sources = new DlcBuilderProjectSources(input.AudioPath.Trim(), input.AlbumArtPath.Trim(), _edit.State.TotalAudioLengthMs);
            var path = await Task.Run(() => _writer.Write(folder, input.Snapshot, sources));
            Message = loc.Format("Dialog_Export_DlcBuilderProjectWritten", path);
            _edit.State.SetMessage(Message, StatusSeverity.Success);
        }
        catch (PlayOnlyContentException ex)
        {
            _edit.Logger.Info(AppLogCategory.Project, ex.Message);
            await _io.Prompt.ShowAsync(loc["Dialog_Export_Title"], loc[PlayOnlyExportGuard.MessageKey], MessageDialogKind.Error, ex.Message);
        }
        catch (Exception ex)
        {
            _edit.Logger.Error(AppLogCategory.Project, $"The DLC Builder project could not be written to '{folder}'.", ex);
            await _io.Prompt.ShowAsync(loc["Dialog_Export_Title"], loc.Format("Dialog_Export_DlcBuilderProjectFailed", ex.Message),
                MessageDialogKind.Error, ex.ToString());
        }
    }
}
