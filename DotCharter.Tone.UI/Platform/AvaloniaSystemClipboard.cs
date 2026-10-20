using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using DotCharter.Tone.Infrastructure.Shell;

namespace DotCharter.Tone.UI.Platform;

/// <summary><see cref="ISystemClipboard" /> over the main window's clipboard — the
/// one implementation both hosts use (DotCharter's container and RigLab's
/// <c>RigLabHost</c>; the desktop lifetime's main window is each app's own).</summary>
public sealed class AvaloniaSystemClipboard : ISystemClipboard
{
    public async Task SetTextAsync(string text)
    {
        if (Clipboard() is { } clipboard) await clipboard.SetTextAsync(text);
    }

    public async Task<string?> GetTextAsync() =>
        Clipboard() is { } clipboard ? await clipboard.TryGetTextAsync() : null;

    private static IClipboard? Clipboard() =>
        Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow?.Clipboard
            : null;
}
