namespace DotCharter.Services.Ai.Chat;

/// <summary>What <see cref="AiChatSaveAsCarry.Carry" /> did: files copied, files left alone because the
/// target already had one by that name, and one line per file that could not be copied.</summary>
internal sealed record AiChatCarryResult(int Copied, int Skipped, IReadOnlyList<string> Failures)
{
    internal static readonly AiChatCarryResult None = new(0, 0, []);
}

/// <summary>
/// Save As into ANOTHER folder carries the chart's AI chats, as it carries its
/// checkpoints. The chats live beside the project, in
///     <c>ai-chats/&lt;chart id&gt;/</c> (<see cref="AiChatStore" />), and the new copy keeps the
///     chart id, so without this the copy opened with an empty history.
///     <para>
///         COPIED, never moved: the old project still exists and keeps its own chats. A file
///         already at the target is never replaced. Each copy lands under a temporary name and
///         is renamed into place without overwriting, so a failed copy leaves no half-written
///         conversation behind; it is reported, and the original is untouched either way.
///         Only files NAMED like a conversation are carried (a quarantined
///         <c>&lt;id&gt;.corrupt-…json</c> stays with the old project).
///     </para>
/// </summary>
internal static class AiChatSaveAsCarry
{
    private const string Extension = ".json";
    private const string TempSuffix = ".carrying";

    internal static AiChatCarryResult Carry(string? fromProjectFolder, string? toProjectFolder, string? chartId)
    {
        if (string.IsNullOrWhiteSpace(fromProjectFolder) || string.IsNullOrWhiteSpace(toProjectFolder) ||
            !ProjectIdentity.IsWellFormed(chartId))
        {
            return AiChatCarryResult.None;
        }

        var fromDir = Path.GetFullPath(Path.Combine(fromProjectFolder, AiChatStore.FolderName, chartId!));
        var toDir = Path.GetFullPath(Path.Combine(toProjectFolder, AiChatStore.FolderName, chartId!));
        if (string.Equals(fromDir, toDir, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(fromDir))
            return AiChatCarryResult.None;

        List<string> files;
        try
        {
            files = [.. Directory.EnumerateFiles(fromDir, "*" + Extension)
                .Where(f => ProjectIdentity.IsWellFormed(Path.GetFileNameWithoutExtension(f)))
                .Order(StringComparer.Ordinal)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new AiChatCarryResult(0, 0, [$"'{fromDir}' could not be listed: {ex.Message}"]);
        }

        var copied = 0;
        var skipped = 0;
        var failures = new List<string>();
        foreach (var from in files)
        {
            var name = Path.GetFileName(from);
            var to = Path.Combine(toDir, name);
            if (File.Exists(to))
            {
                skipped++;
                continue;
            }

            var temp = to + TempSuffix;
            try
            {
                Directory.CreateDirectory(toDir);
                File.Copy(from, temp, overwrite: true);
                AtomicFile.FlushFile(temp);
                File.Move(temp, to, overwrite: false);
                copied++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                BestEffortDelete.File(temp);
                failures.Add($"{name}: {ex.Message}");
            }
        }

        return new AiChatCarryResult(copied, skipped, failures);
    }
}
