namespace DotCharter.Services.Session;

/// <summary>
///     Who asked for a project save. Required on every save entry, so a new caller must choose:
///     only a save the USER starts applies a draft an editor still holds (a text box keeps its
///     focus through Ctrl+S); an unattended save writes the stored model and leaves the draft
///     where the user is still typing it.
/// </summary>
public enum SaveInitiator
{
    /// <summary>Save, Save As: the pending draft is committed first.</summary>
    User,

    /// <summary>The close or replace prompt's Save: the pending draft is committed and every staged draft only its own
    /// Apply writes (the inspector's staged pages) is applied first, since the prompt counted it as unsaved work.</summary>
    Prompt,

    /// <summary>A save nobody asked for at that moment (after stem separation, …): the draft stays pending.</summary>
    Unattended
}
