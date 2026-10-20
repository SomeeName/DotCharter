using System.ComponentModel;
using System.Windows.Input;

namespace DotCharter.Presentation.Contracts.Settings;

/// <summary>
///     Settings ▸ AI: the one place a provider for "Design a tone" is chosen,
///     and the block that installs the local model "Match from audio" uses as a
///     prior. The <c>Is…Visible</c> flags are how the cross-tab search filters
///     this tab — each already folds in whether the chosen provider wants that
///     row at all, so a view binds one flag per row and asks nothing.
/// </summary>
public interface IAiSettingsScreen : INotifyPropertyChanged
{
    /// <summary>
    ///     False when the build's composition root registered no AI services.
    ///     The tab then prints its reason and every control is disabled, rather
    ///     than offering controls that cannot work.
    /// </summary>
    bool IsAvailable { get; }

    bool IsUnavailableNoteVisible { get; }

    bool IsProviderGroupVisible { get; }

    IReadOnlyList<IAiProviderOption> Providers { get; }

    IAiProviderOption SelectedProvider { get; set; }

    bool IsProviderRowVisible { get; }

    /// <summary>
    ///     The note above the local-CLI rows: what it runs, whose login it uses
    ///     and whose usage it spends. One line, no disclosure triangle — a cost
    ///     note behind one is a cost note nobody reads.
    /// </summary>
    bool IsClaudeCliNoteVisible { get; }

    /// <summary>The same note for the Codex CLI: it runs under the user's ChatGPT sign-in.</summary>
    bool IsCodexCliNoteVisible { get; }

    string ModelId { get; set; }

    bool IsModelRowVisible { get; }

    /// <summary>The model row's line: required for most providers, optional for Codex (empty means GPT-6 Luna).</summary>
    string ModelDesc { get; }

    /// <summary>The three model ids the local CLI is offered with, as fill-in buttons.</summary>
    bool AreClaudeModelPresetsVisible { get; }

    ICommand UseClaudeHaikuCommand { get; }

    ICommand UseClaudeOpusCommand { get; }

    ICommand UseClaudeSonnetCommand { get; }

    /// <summary>The two model ids the Codex CLI is offered with, as fill-in buttons.
    /// An empty model box means GPT-6 Luna.</summary>
    bool AreCodexModelPresetsVisible { get; }

    ICommand UseCodexSolCommand { get; }

    ICommand UseCodexLunaCommand { get; }

    /// <summary>The Reasoning row under the model: Default / Low / Medium / High, sent with every
    /// request, and disabled with its reason where this provider and model cannot take it.</summary>
    IAiReasoningPart Reasoning { get; }

    bool IsEffortRowVisible { get; }

    /// <summary>
    ///     The OpenAI-compatible server's address, and — for the local CLI,
    ///     which has no address — the optional explicit path to its executable.
    ///     One persisted value, two mutually exclusive rows.
    /// </summary>
    string BaseUrl { get; set; }

    bool IsBaseUrlRowVisible { get; }

    bool IsCliPathRowVisible { get; }

    /// <summary>"Claude Code path" or "Codex path", with its line.</summary>
    string CliPathLabel { get; }

    string CliPathDesc { get; }

    ICommand UseLmStudioCommand { get; }

    ICommand UseOllamaCommand { get; }

    /// <summary>
    ///     What the user just typed into the password box, never seeded from the
    ///     store. A box that repopulated itself would hand the secret to
    ///     anything that can screenshot the window, and would make an empty box
    ///     mean "erase my key" — <see cref="ClearKeyCommand" /> is the erase.
    /// </summary>
    string ApiKeyEntry { get; set; }

    bool IsApiKeyRowVisible { get; }

    /// <summary>
    ///     True where the store cannot SEAL the key, so it is kept for the
    ///     session and forgotten on exit. The row says so: a user who believes a
    ///     key was saved and finds it gone reads that as a broken feature.
    /// </summary>
    bool IsKeySessionOnlyNoteVisible { get; }

    /// <summary>"Key stored" / "No key stored" — the presence, never the value.</summary>
    string KeyStateText { get; }

    bool HasStoredKey { get; }

    ICommand ClearKeyCommand { get; }

    /// <summary>
    ///     May the designer read the Rocksmith tones in the player's own library
    ///     and hand the closest one to the model as an anchor. On by default: it
    ///     is the biggest quality lever the feature has.
    /// </summary>
    bool UseLibraryTones { get; set; }

    bool IsUseLibraryTonesRowVisible { get; }

    /// <summary>
    ///     May a provider that can search the web (the Claude Code and Codex CLIs)
    ///     look a named song's recorded rig up before designing. On by default;
    ///     the row shows only for those two providers.
    /// </summary>
    bool WebResearch { get; set; }

    bool IsWebResearchRowVisible { get; }

    bool IsTestRowVisible { get; }

    /// <summary>
    ///     Saves first, then asks the configured model to answer. A test against
    ///     the settings on screen rather than the ones in force would pass for a
    ///     configuration the app is not using — the exact failure a
    ///     test-connection button exists to rule out.
    /// </summary>
    ICommand TestConnectionCommand { get; }

    string StatusText { get; }

    bool HasStatus { get; }

    bool IsStatusError { get; }

    IMatchModelPart Match { get; }
}

/// <summary>
/// Settings ▸ AI ▸ Reasoning. <see cref="IsSupported" /> is false for a provider or a
///     model that cannot take a level (a Claude model older than the effort parameter): the
///     picker is then disabled and <see cref="Description" /> says why instead of what it does.
/// </summary>
public interface IAiReasoningPart : INotifyPropertyChanged
{
    IReadOnlyList<IAiEffortOption> Levels { get; }

    IAiEffortOption Selected { get; set; }

    bool IsSupported { get; }

    /// <summary>What the level does, or why this provider and model cannot take one.</summary>
    string Description { get; }
}

/// <summary>One level of the Reasoning picker.</summary>
public interface IAiEffortOption
{
    string Label { get; }
}

/// <summary>
///     One row of the provider picker. <see cref="IsAvailable" /> is false for a
///     provider this build or this machine cannot offer — the row stays and says
///     why, because an empty picker reads as a different failure.
/// </summary>
public interface IAiProviderOption
{
    string Label { get; }

    bool IsAvailable { get; }
}

/// <summary>
///     Settings ▸ AI ▸ Match model: the trained rig estimator and its
///     pre-rendered tone library, each fetched once as a release asset. The two
///     downloads run and fail independently, so each has its own row, button,
///     progress and detail line.
///     <para>
///         It lives on the AI tab rather than beside the other on-disk model
///         downloads, whose row grammar it borrows: that tab is models for stem
///         SEPARATION, and a user looking for what makes "Match from audio" work
///         looks under AI. It is deliberately not gated on
///         <see cref="IAiSettingsScreen.IsAvailable" /> — a local model needs no
///         provider, no key and no account.
///     </para>
/// </summary>
public interface IMatchModelPart : INotifyPropertyChanged
{
    /// <summary>False when the composition root registered no downloader; the whole block is then hidden.</summary>
    bool IsGroupVisible { get; }

    bool IsModelRowVisible { get; }

    /// <summary>
    ///     One localized line answering both questions a model has: is it
    ///     installed, and does it LOAD. A model from a different label space is
    ///     installed and unusable, and a row reporting only the first would send
    ///     that user looking for a bug in the matcher.
    /// </summary>
    string ModelStatus { get; }

    bool ModelIsError { get; }

    bool IsLibraryRowVisible { get; }

    string LibraryStatus { get; }

    bool LibraryIsError { get; }

    bool CanDownloadModel { get; }

    ICommand DownloadModelCommand { get; }

    bool IsModelDownloading { get; }

    ICommand CancelModelDownloadCommand { get; }

    bool CanDownloadLibrary { get; }

    ICommand DownloadLibraryCommand { get; }

    bool IsLibraryDownloading { get; }

    ICommand CancelLibraryDownloadCommand { get; }

    /// <summary>Where the files go — also where a hand-placed export has to go.</summary>
    string ModelDirectory { get; }

    ICommand OpenFolderCommand { get; }

    ICommand RefreshCommand { get; }

    /// <summary>The installer's current step, or the outcome.</summary>
    string ModelDetail { get; }

    bool HasModelDetail { get; }

    string LibraryDetail { get; }

    bool HasLibraryDetail { get; }
}
