namespace DotCharter.Tone.Infrastructure.Shell;

/// <summary>
///     The operating system's text clipboard. Implemented by
///     <c>DotCharter.Tone.UI/Platform/AvaloniaSystemClipboard</c> over the main
///     window's clipboard — ONE implementation for both hosts: DotCharter registers
///     it in its container (the GitHub device code, the text documents), RigLab
///     builds it in <c>RigLabHost</c>, and the AI tone designer's "copy the prompt
///     for ChatGPT" uses it in both. It lives in this assembly (not Services)
///     so the rig, which never references Services, can reach it. Not the
///     chart clipboard — that is <c>State/IClipboardService</c>, which copies notes,
///     not text.
/// </summary>
public interface ISystemClipboard
{
    /// <summary>Puts <paramref name="text" /> on the clipboard. A host with no
    /// window (headless, shutting down) does nothing.</summary>
    Task SetTextAsync(string text);

    /// <summary>The clipboard's text, or null when it holds none or there is no
    /// window.</summary>
    Task<string?> GetTextAsync();
}
