namespace DotCharter.Tone.Infrastructure.Shell;

/// <summary>
///     Hands a URL, file or folder to the operating system shell — the browser,
///     the default app for a file type, the file manager. Implemented by
///     <c>DotCharter.UI/Platform/DesktopShellLauncher</c>. Every member throws
///     what the underlying process launch throws (no handler registered, access
///     denied); callers keep their own best-effort catch.
/// </summary>
public interface IShellLauncher
{
    /// <summary>Opens <paramref name="url" /> in the default browser.</summary>
    void OpenUrl(string url);

    // No "open a file with its default app": Windows raises its "choose an app" box
    // for an extension with none (.md, and .txt on some machines). Show a document in
    // the app (ITextDocumentScreen) and reveal its file with RevealInFileManager.

    /// <summary>
    ///     Opens a folder in the file manager. When <paramref name="create" /> is
    ///     true the folder is created first; otherwise a missing folder opens
    ///     nothing and the call returns false.
    /// </summary>
    bool OpenFolder(string path, bool create = false);

    /// <summary>
    ///     Opens the file manager with <paramref name="path" /> selected. A file
    ///     that does not exist opens nothing and the call returns false.
    /// </summary>
    bool RevealInFileManager(string path);
}
