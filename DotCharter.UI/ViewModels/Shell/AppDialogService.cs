using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DotCharter.Presentation.Dialogs;
using DotCharter.Services.Diagnostics;
using DotCharter.UI.ViewModels.Dialogs;
using DotCharter.UI.ViewModels.Onboarding;
using DotCharter.UI.Views.Shell;

namespace DotCharter.UI.ViewModels.Shell;

public interface IAppDialogService : INotifyPropertyChanged
{
    /// <summary>Every dialog id the app offers — the denominator for usage stats.</summary>
    IReadOnlyList<string> KnownDialogIds { get; }

    OnboardingWizardViewModel Onboarding { get; }
    MessageDialogViewModel Message { get; }
    IReadOnlyList<IDialogViewModel> AllDialogs { get; }
    IDialogViewModel? FirstOpenDialog { get; }

    /// <summary>The dialogs on the rewrite's <c>DialogHost</c> (target-architecture.md § 3.2).</summary>
    IDialogs Hosted { get; }

    /// <summary>A legacy dialog OR a hosted one is open — the one flag the shell's key gates and the 3D pane read.</summary>
    bool HasOpenDialog { get; }

    /// <summary>Closes the topmost dialog the way Esc does; the dialog's name, or null when none was open.</summary>
    string? CloseTopDialog();
}

public sealed class AppDialogService : ObservableObject, IAppDialogService
{
    private readonly IUsageTracker? _usage;

    public AppDialogService(
        OnboardingWizardViewModel onboarding,
        MessageDialogViewModel message,
        IUsageTracker? usage = null,
        IDialogs? hosted = null,
        ScreenCatalog? catalog = null)
    {
        _usage = usage;
        Hosted = hosted ?? new DialogsSlot();
        _hostedIds = catalog?.Entries.Where(e => e.Kind == ScreenKind.Dialog).Select(e => e.UsageId).ToList() ?? [];
        Hosted.Changed += OnHostedDialogChanged;
        Onboarding = onboarding;
        Message = message;

        AllDialogs =
        [
            // The first-run wizard opens the latency wizard ON TOP of itself (its
            // Latency page) and can itself sit over the Settings tab ("Run setup
            // again"); the settings page is a screen, not a dialog, and is not listed.
            Onboarding,
            Message
        ];

        foreach (var dialog in AllDialogs)
        {
            if (dialog is INotifyPropertyChanged inpc)
                inpc.PropertyChanged += OnDialogPropertyChanged;
        }
    }

    private void OnDialogPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(IDialogViewModel.IsOpen)) return;

        OnPropertyChanged(nameof(FirstOpenDialog));
        OnPropertyChanged(nameof(HasOpenDialog));

        // Every dialog in the app opens through this one property, which is what
        // makes "which dialogs actually get used" one hook rather than thirty.
        if (sender is not IDialogViewModel dialog) return;

        RecordUse(DialogId(dialog), dialog.IsOpen);
    }

    private void RecordUse(string id, bool opened)
    {
        if (opened)
        {
            // The OPEN edge is the use; IsOpen fires on close too, and counting
            // both would double every dialog.
            _usage?.Record(UsageKind.Dialog, id);
            _openedAtUtcTicks[id] = DateTime.UtcNow.Ticks;
            return;
        }

        // The CLOSE edge contributes DWELL TIME, not a use. It separates a dialog
        // opened often and dismissed instantly — the wrong route to something, or
        // a misclick — from one that is genuinely worked in.
        if (!_openedAtUtcTicks.Remove(id, out var openedAt)) return;
        _usage?.RecordDuration(UsageKind.Dialog, id,
            (long)TimeSpan.FromTicks(DateTime.UtcNow.Ticks - openedAt).TotalMilliseconds);
    }

    private readonly Dictionary<string, long> _openedAtUtcTicks = new(StringComparer.Ordinal);

    /// <summary>The report needs what EXISTS to name what was never opened.</summary>
    public IReadOnlyList<string> KnownDialogIds => [.. AllDialogs.Select(DialogId), .. _hostedIds];

    private static string DialogId(IDialogViewModel dialog)
    {
        var name = dialog.GetType().Name;
        const string suffix = "ViewModel";
        return name.EndsWith(suffix, StringComparison.Ordinal) ? name[..^suffix.Length] : name;
    }

    public OnboardingWizardViewModel Onboarding { get; }
    public MessageDialogViewModel Message { get; }
    public IReadOnlyList<IDialogViewModel> AllDialogs { get; }
    public IDialogViewModel? FirstOpenDialog => AllDialogs.FirstOrDefault(dialog => dialog.IsOpen);
    public IDialogs Hosted { get; }
    public bool HasOpenDialog => FirstOpenDialog != null || Hosted.HasOpenDialog;

    /// <summary>
    ///     Top first, in drawing order: the generic message dialog sits above
    ///     the dialog host, and the host above every other legacy dialog
    ///     (<c>DialogContainerView</c>).
    /// </summary>
    public string? CloseTopDialog()
    {
        if (Message.IsOpen)
        {
            Message.RequestClose();
            return DialogId(Message);
        }

        if (Hosted.HasOpenDialog && Hosted.CloseTop()) return "hosted dialog";
        if (FirstOpenDialog is not { } legacy) return null;
        legacy.RequestClose();
        return DialogId(legacy);
    }

    private readonly IReadOnlyList<string> _hostedIds;

    /// <summary>The hosted dialogs' opens and closes, counted like the legacy ones' IsOpen edges.</summary>
    private void OnHostedDialogChanged(object? sender, DialogChange change)
    {
        OnPropertyChanged(nameof(HasOpenDialog));
        RecordUse(change.Id, change.IsOpen);
    }
}
