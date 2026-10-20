using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Tone.Infrastructure.Configuration;
using DotCharter.Tone.Infrastructure.Shell;
using DotCharter.Tone.Logging;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>A plain-text document shown in the app. The caller reads or builds the text; this shows it.</summary>
public sealed class TextDocumentViewModel : DialogViewModel<bool>, ITextDocumentScreen, IDialogSetup<TextDocumentRequest>
{
    private readonly ISystemClipboard _clipboard;
    private readonly IShellLauncher _shell;
    private readonly IAppLogger _logger;
    private TextDocumentRequest _request = new("", "", "", "");

    public TextDocumentViewModel(ISystemClipboard clipboard, IShellLauncher shell, IAppLogger logger)
    {
        _clipboard = clipboard;
        _shell = shell;
        _logger = logger;
        CopyCommand = new AsyncRelayCommand(CopyAsync);
        ShowFileCommand = new RelayCommand(ShowFile);
        CloseCommand = new RelayCommand(() => Close(true));
    }

    public string Title => _request.Title;

    public string Subtitle => _request.Subtitle;

    public string Text => _request.Text;

    public bool HasText => Text.Length > 0;

    public string EmptyText => _request.EmptyText;

    public ICommand CopyCommand { get; }

    public bool CanShowFile => _request.FilePath.Length > 0;

    public ICommand ShowFileCommand { get; }

    public ICommand CloseCommand { get; }

    public void Configure(TextDocumentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _request = request with
        {
            Title = request.Title ?? "",
            Subtitle = request.Subtitle ?? "",
            Text = request.Text ?? "",
            FilePath = request.FilePath ?? "",
            EmptyText = request.EmptyText ?? ""
        };
        OnPropertyChanged(string.Empty);
    }

    private async Task CopyAsync()
    {
        try
        {
            await _clipboard.SetTextAsync(Text).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.Warning(AppLogCategory.Application, $"Copying '{Title}' failed.", ex);
        }
    }

    private void ShowFile()
    {
        if (!CanShowFile || !_shell.RevealInFileManager(_request.FilePath))
            _logger.Warning(AppLogCategory.Application, $"The file behind '{Title}' could not be shown: '{_request.FilePath}'.");
    }
}
