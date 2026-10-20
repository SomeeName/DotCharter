// The Help-menu commands: About, Session Health diagnostics, the bundled
// LICENSE + THIRD-PARTY-NOTICES.md, the update check and the local usage report.
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Services.Diagnostics;
using DotCharter.Services.Infrastructure.Shell;
using DotCharter.Services.Update;
using DotCharter.Tone.UI.ViewModels.Shell;
using DotCharter.UI.Input;
using DotCharter.UI.Services;

namespace DotCharter.UI.ViewModels.Shell;

public sealed partial class HelpMenuViewModel : ViewModelBase
{
    private readonly IAppDialogService _dialogs;
    private readonly IDialogs _hostedDialogs;
    private readonly ChartKeybindHandler _keybinds;
    private readonly IAppLogger _logger;
    private readonly ILocalizationService _localization;
    private readonly IDiagnosticsDialogService? _diagnosticsDialog;
    private readonly IUsageTracker? _usage;
    private readonly IBundledDocuments? _bundledDocs;
    private readonly UpdateCheckFlow? _updateChecks;

    public HelpMenuViewModel(
        IAppDialogService dialogs,
        ChartKeybindHandler keybinds,
        IAppLogger logger,
        ILocalizationService localization,
        IDialogs? hostedDialogs = null,
        IDiagnosticsDialogService? diagnosticsDialog = null,
        IUsageTracker? usage = null,
        IBundledDocuments? bundledDocs = null,
        UpdateCheckFlow? updateChecks = null)
    {
        _dialogs = dialogs;
        _hostedDialogs = hostedDialogs ?? dialogs.Hosted;
        _keybinds = keybinds;
        _logger = logger;
        _localization = localization;
        _diagnosticsDialog = diagnosticsDialog;
        _usage = usage;
        _bundledDocs = bundledDocs;
        _updateChecks = updateChecks;
    }

    /// <summary>The ids the palette counts commands under — the usage report's
    /// command denominator. The catalogue is built from the shell, which wires
    /// this.</summary>
    internal Func<IEnumerable<string>>? CommandUsageIds { get; set; }

    [RelayCommand]
    private void ShowAbout() => Launch(() => _hostedDialogs.ShowAsync<IAboutScreen, bool>(), "about");

    [RelayCommand]
    private void ShowDiagnostics() => _diagnosticsDialog?.ShowDiagnostics();

    /// <summary>Help ▸ License &amp; notices: THIRD-PARTY-NOTICES.md and LICENSE read in the app — the system has no
    /// viewer for .md on many machines and raised its "choose an app" box.</summary>
    [RelayCommand]
    private void OpenLicenseFile()
    {
        if (_bundledDocs is null) return;
        var docs = _bundledDocs;
        Launch(async () => await _hostedDialogs.ShowAsync(
            await Task.Run(() => LicenseDocument.Request(docs, _localization)).ConfigureAwait(true)).ConfigureAwait(true), "license");
    }

    /// <summary>
    ///     The ONE door for "is there an update" outside Settings: Help ▸ Check
    ///     for updates… and the palette row. Re-opens what the startup check
    ///     found, else checks now (<c>UpdateCheckFlow.CheckOrShowAsync</c>).
    /// </summary>
    [RelayCommand]
    private Task CheckOrShowUpdate() => _updateChecks?.CheckOrShowAsync() ?? Task.CompletedTask;

    /// <summary>
    ///     Writes the local usage report and opens it.
    ///     <para>
    ///         The counts exist to answer "what is worth improving and what should
    ///         move into a submenu", and that question needs the DENOMINATOR — the
    ///         list of everything on offer — not just a ranking of what was
    ///         clicked. So the report is built against the live dialog list and the
    ///         live keybind registry, which is what lets it name the things that
    ///         have never been used at all.
    ///     </para>
    /// </summary>
    [RelayCommand]
    private void ShowUsageStatistics()
    {
        if (_usage == null) return;

        try
        {
            // The command denominator is the palette catalogue, under the ids
            // its entries are COUNTED as (a RelayCommand's method name, shared
            // with the menu click hook). Without a command denominator the report
            // would list every command ever clicked
            // as dropped from the app and none as never used.
            var known = new Dictionary<string, IReadOnlyList<string>>
            {
                [UsageKind.Command] = (CommandUsageIds?.Invoke() ?? [])
                    .Distinct(StringComparer.Ordinal).ToList(),
                [UsageKind.Dialog] = _dialogs.KnownDialogIds,
                [UsageKind.Keybind] = _keybinds.KnownKeybindLabels,
            };

            var report = _usage.Snapshot.BuildReport(known, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            _usage.Flush();

            // Shown in the app, with the written copy one click away: handing the .txt
            // to its default app raised Windows' "choose an app" box where there is none.
            var path = Path.Combine(WritablePaths.ConfigRoot, "usage-report.txt");
            try
            {
                AtomicFile.WriteAllText(path, report);
            }
            catch (Exception ex)
            {
                _logger.Warning(AppLogCategory.Application, "Could not write the usage report file.", ex);
                path = "";
            }

            Launch(() => _hostedDialogs.ShowAsync(new TextDocumentRequest(_localization["UsageReport_Title"],
                _localization["UsageReport_Subtitle"], report, path, _localization["UsageReport_Empty"])), "usage statistics");
        }
        catch (Exception ex)
        {
            _logger.Warning(AppLogCategory.Application, "Could not build the usage report.", ex);
        }
    }

    private void Launch(Func<Task> show, string what) => DialogLaunch.Run(_logger, show, what);
}
