using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Services.Infrastructure.Shell;
using DotCharter.Tone.Infrastructure.Configuration;
using DotCharter.Tone.Logging;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>Help ▸ About. The version is read once, when it opens; the licence opens over it, in the app.</summary>
public sealed class AboutViewModel : DialogViewModel<bool>, IAboutScreen
{
    private readonly IBundledDocuments _documents;
    private readonly IDialogs _dialogs;
    private readonly IAppLogger _logger;
    private readonly ILocalizationService _localization;

    public AboutViewModel(IBundledDocuments documents, IDialogs dialogs, IAppLogger logger, ILocalizationService localization)
    {
        _documents = documents;
        _localization = localization;
        _dialogs = dialogs;
        _logger = logger;
        Version = "v" + DotCharter.Services.Infrastructure.AppVersionInfo.Current;
        OpenLicenseCommand = new AsyncRelayCommand(OpenLicenseAsync);
        CloseCommand = new RelayCommand(() => Close(true));
    }

    public string Version { get; }

    public ICommand OpenLicenseCommand { get; }

    public ICommand CloseCommand { get; }

    /// <summary>Not a resx string: a version number reads the same in every language.</summary>
    public static string FormatVersion(Version? version) => $"v{version?.ToString() ?? "dev"}";

    private async Task OpenLicenseAsync()
    {
        try
        {
            var request = await Task.Run(() => LicenseDocument.Request(_documents, _localization)).ConfigureAwait(true);
            _dialogs.ShowAsync(request).Observe(_logger, "license");
        }
        catch (Exception ex)
        {
            _logger.Error(AppLogCategory.Application, "Showing the licence from About failed.", ex);
        }
    }
}
