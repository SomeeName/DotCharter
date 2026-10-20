using System.Diagnostics;
using DotCharter.Tone.Infrastructure.Shell;

namespace DotCharter.Tone.UI.Platform;

/// <summary>
///     <see cref="IShellLauncher" /> over <see cref="Process.Start(ProcessStartInfo)" />
///     with shell execution: a URL goes to the browser, a file to its default
///     app, a folder to Explorer, and a reveal is <c>explorer.exe /select,</c>.
///     The start infos are built by pure functions so the arguments are pinned
///     without launching anything.
/// </summary>
public sealed class DesktopShellLauncher : IShellLauncher
{
    public void OpenUrl(string url) => Process.Start(ForShellOpen(url));

    public bool OpenFolder(string path, bool create = false)
    {
        if (create) Directory.CreateDirectory(path);
        else if (!Directory.Exists(path)) return false;
        Process.Start(ForShellOpen(path));
        return true;
    }

    public bool RevealInFileManager(string path)
    {
        if (!File.Exists(path)) return false;
        Process.Start(ForReveal(path));
        return true;
    }

    /// <summary>Shell-executes <paramref name="target" /> — the default handler for a
    /// URL, a file or a folder.</summary>
    internal static ProcessStartInfo ForShellOpen(string target) => new(target) { UseShellExecute = true };

    /// <summary>Explorer with <paramref name="path" /> (made absolute) selected.</summary>
    internal static ProcessStartInfo ForReveal(string path) =>
        new("explorer.exe", $"/select,\"{Path.GetFullPath(path)}\"") { UseShellExecute = true };
}
