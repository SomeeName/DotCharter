using System.Windows.Input;
using DotCharter.Presentation.Contracts.Dialogs;

namespace DotCharter.Presentation.Fakes.Dialogs;

/// <summary>A services-free <see cref="IUpdateAvailableScreen" />; Later answers false.</summary>
public sealed class FakeUpdateAvailableScreen : FakeDialog<bool>, IUpdateAvailableScreen
{
    private const string Version = "v0.63.0 — you have v0.62.1";

    private const string Notes =
        "## What's new\n\n- Dialogs open on the new host\n- **Set BPM** remembers the grid's tempo\n- Faster first open";

    private FakeUpdateAvailableScreen(string version, string notes, string status, bool downloading, double progress,
        bool canInstall)
    {
        VersionText = version;
        ReleaseNotes = notes;
        StatusText = status;
        IsDownloading = downloading;
        DownloadProgress = progress;
        CanInstall = canInstall;
        LaterCommand = Answer(false);
        DownloadAndInstallCommand = canInstall && !downloading ? Answer(true) : Disabled();
    }

    public static IReadOnlyList<FakeVariant<IUpdateAvailableScreen>> Variants { get; } =
    [
        new(FakeVariantNames.Default, Default),
        new(FakeVariantNames.Empty, Empty),
        new(FakeVariantNames.LongText, LongText),
        new(FakeVariantNames.Busy, Busy),
        new("Blocked", Blocked)
    ];

    public static FakeUpdateAvailableScreen Default() => new(Version, Notes, "", false, 0, true);

    /// <summary>A release with no notes: the section is hidden.</summary>
    public static FakeUpdateAvailableScreen Empty() => new(Version, "", "", false, 0, true);

    public static FakeUpdateAvailableScreen LongText() =>
        new("v0.63.0-rc.12+build.20260922 — Sie verwenden v0.62.1 (インストール済み)",
            "## Neuigkeiten\n\n- Donaudampfschifffahrtsgesellschaftskapitänsmütze: sehr lange Zeile ohne Umbruch " +
            "im Versionshinweis, die trotzdem umbrechen muss\n- 非常に長いリリースノートの行です。ダイアログの幅を超えても折り返されます。",
            "Der Installationsordner ist schreibgeschützt; starten Sie DotCharter mit Schreibrechten neu.", false, 0,
            false);

    /// <summary>Downloading: the determinate bar and the percentage line.</summary>
    public static FakeUpdateAvailableScreen Busy() => new(Version, Notes, "Downloading… 42 %", true, 42, true);

    /// <summary>This install cannot update itself; the primary is disabled and the reason printed.</summary>
    public static FakeUpdateAvailableScreen Blocked() =>
        new(Version, Notes, "This copy was not installed by the release installer, so it cannot update itself.", false,
            0, false);

    public string VersionText { get; }

    public string ReleaseNotes { get; }

    public bool HasReleaseNotes => ReleaseNotes.Length > 0;

    public string StatusText { get; }

    public bool IsDownloading { get; }

    public double DownloadProgress { get; }

    public bool CanInstall { get; }

    public ICommand LaterCommand { get; }

    public ICommand DownloadAndInstallCommand { get; }
}
