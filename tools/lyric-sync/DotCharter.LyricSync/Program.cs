using DotCharter.Localization;
using DotCharter.Services.Audio.Lyrics;

namespace DotCharter.LyricSync;

/// <summary>
///     The entry point. Everything it does beyond wiring is in
///     <see cref="LyricSyncCommand" />, which is where the tests are.
/// </summary>
public static class Program
{
    /// <summary>Runs the tool. Ctrl+C cancels the decode or the alignment cleanly.</summary>
    /// <param name="args">The command line; see <c>--help</c>.</param>
    /// <returns>See <see cref="LyricSyncExit" />.</returns>
    public static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };

        var command = new LyricSyncCommand(
            Console.Out, Console.Error, BuildCore, EnsureModelsAsync);

        try
        {
            return await command.RunAsync(args, cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await Console.Error.WriteLineAsync("Cancelled.").ConfigureAwait(false);
            return LyricSyncExit.BadInput;
        }
    }

    /// <summary>
    ///     The SAME service the app resolves — <see cref="LyricAutoSyncService" />
    ///     implements <see cref="ILyricSyncCore" />, so this tool is not a second
    ///     implementation of anything.
    /// </summary>
    private static ILyricSyncCore BuildCore(LyricSyncArguments arguments) =>
        new LyricAutoSyncService(LocalizationService.Instance, modelRoots: Roots(arguments));

    /// <summary>
    ///     <c>--model-dir</c> replaces the search roots entirely; otherwise the
    ///     catalogue's own order applies (beside the executable first, then
    ///     per-user), so a portable copy carries its own models.
    /// </summary>
    private static IReadOnlyList<string>? Roots(LyricSyncArguments arguments) =>
        string.IsNullOrWhiteSpace(arguments.ModelDirectory) ? null : [arguments.ModelDirectory];

    /// <summary>
    ///     Fetches whatever this sheet needs, through the app's own installer — the
    ///     same never-overwrite, hash-checked, staged extraction.
    ///     <para>
    ///         <b>The assets live on a PRIVATE GitHub repository today</b>, so an
    ///         outside user needs a token in <c>DOTCHARTER_GITHUB_TOKEN</c> or
    ///         <c>GITHUB_TOKEN</c>, or a hand-installed <c>models</c> folder. That is
    ///         a publishing decision, not a limitation of this code: the moment the
    ///         assets are public the anonymous path works unchanged.
    ///     </para>
    ///     <para>
    ///         A failure here is never fatal on its own — the caller re-checks
    ///         <see cref="ILyricSyncCore.IsModelInstalled" />, so a machine that
    ///         already has the models works with no network at all.
    ///     </para>
    /// </summary>
    private static async Task<bool> EnsureModelsAsync(
        LyricSyncArguments arguments, IProgress<string>? status, CancellationToken ct)
    {
        var roots = Roots(arguments);
        var sheet = Analysis.Lyrics.LyricSheetParser.Parse(
            await File.ReadAllTextAsync(arguments.LyricsPath, ct).ConfigureAwait(false));

        using var downloads = new LyricModelDownloadService(
            new LyricModelAssetFetcher(Token), LocalizationService.Instance, logger: null, modelRoots: roots);

        if (downloads.IsRequiredModelInstalled &&
            (arguments.NoEnsemble || downloads.PendingMegabytesFor(sheet) == 0))
        {
            return true;
        }

        var megabytes = downloads.PendingMegabytesFor(sheet);
        if (megabytes > 0)
            status?.Report($"Fetching the alignment model ({megabytes} MB). This happens once.");

        try
        {
            return await downloads.EnsureModelsForAsync(sheet, status, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"Could not fetch the model: {ex.Message}")
                .ConfigureAwait(false);
            return downloads.IsRequiredModelInstalled;
        }
    }

    private static string? Token() =>
        Environment.GetEnvironmentVariable("DOTCHARTER_GITHUB_TOKEN")
        ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN");
}
