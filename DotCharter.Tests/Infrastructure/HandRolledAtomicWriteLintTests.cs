using System.Text.RegularExpressions;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     No product code stages a temp file and swaps it in with a bare
///     <c>File.Move</c> or <c>File.Replace</c> instead of going through AtomicFile (#374).
///     <para>
///         <c>AtomicFile</c> flushes the staged bytes to disk before the rename (a power cut
///         otherwise persists a zero-length renamed file), retries the swap while an
///         antivirus or indexer holds the destination, and names its temp file at random so
///         two writers never collide. A hand-rolled <c>path + ".tmp"</c> then
///         <c>File.Move(overwrite: true)</c> does none of that. Use
///         <c>AtomicFile.WriteAllText/WriteAllBytes/Write</c>, <c>Publish</c> (a staged file
///         something else wrote) or <c>MoveAside</c> (quarantine a corrupt store).
///     </para>
///     <para>
///         The allow-list below holds the moves that are NOT an overwrite-publish (a rename
///         that must fail when the target exists, a backup or relocate). Each entry carries
///         its reason and the exact number of calls; the list may only shrink, and an entry
///         that no longer hits fails the test.
///     </para>
/// </summary>
public sealed class HandRolledAtomicWriteLintTests
{
    private static readonly Regex MoveCall = new(@"\bFile\.(Move|Replace)\s*\(", RegexOptions.Compiled);

    private static readonly (string File, int Count, string Reason)[] Allowed =
    [
        ("DotCharter.Services/Ai/Chat/AiChatSaveAsCarry.cs", 1, "overwrite:false carry to a Save-As name; a clash must fail, not replace"),
        ("DotCharter.Services/Audio/LivePlay/PracticeProfileService.cs", 1, "one-time legacy-file migration, overwrite:false"),
        ("DotCharter.Services/Audio/StemSeparation/Cache/LegacyLibraryRekey.cs", 1, "overwrite:false re-key; never clobbers an existing archive"),
        ("DotCharter.Services/Audio/StemSeparation/Cache/StemCacheStore.cs", 2, "relocates an archive between cache keys, overwrite:false"),
        ("DotCharter.Services/Audio/StemSeparation/StemFolderBackup.cs", 3, "moves whole stem backups between folders; not a stage-then-swap of a write"),
        ("DotCharter.Services/Import/Audio/WavSilencePadder.cs", 1, "overwrite:false partial-to-final rename"),
        ("DotCharter.Services/Parsing/Rscp/ProjectFiles.cs", 1, "renames the original to a backup name before the writer runs"),
        ("DotCharter.Services/Update/SplitInstallCleanup.cs", 1, "relocates a legacy file to its current name, overwrite:false"),
        ("DotCharter.Tone/Audio/Reamp/DiTakeLibrary.cs", 1, "renames a take, overwrite:false"),
        ("DotCharter.Tone/Audio/Reamp/DiTakeRecorder.cs", 1, "finalizes a recording to a unique name, overwrite:false"),
        ("DotCharter.Tone/Online/Tone3000/NamCaptureLibrary.cs", 4, "download finalize and rename of a capture, overwrite:false")
    ];

    [Fact]
    public void NoProductCode_SwapsAStagedFileWithABareFileMove()
    {
        var root = TestPaths.RepoRoot();
        var counts = new Dictionary<string, int>();
        var offenders = new List<string>();
        var scanned = 0;

        foreach (var project in Directory.EnumerateDirectories(root, "DotCharter.*"))
        {
            var name = Path.GetFileName(project);
            if (name is "DotCharter.Tests") continue;

            foreach (var file in Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (relative.Contains("/obj/") || relative.Contains("/bin/")) continue;
                if (relative.EndsWith("Infrastructure/Storage/AtomicFile.cs", StringComparison.Ordinal)) continue;

                scanned++;
                var text = File.ReadAllText(file);
                if (!text.Contains("File.", StringComparison.Ordinal)) continue;
                var code = SourceText.StripCommentsKeepingLines(text);
                foreach (Match m in MoveCall.Matches(code))
                {
                    counts[relative] = counts.GetValueOrDefault(relative) + 1;
                    offenders.Add($"{relative}:{SourceText.LineAt(code, m.Index)}: File.{m.Groups[1].Value}(");
                }
            }
        }

        Assert.True(scanned > 500, $"only {scanned} product files scanned; the sweep is looking in the wrong place.");

        var allowed = Allowed.ToDictionary(a => a.File, a => a.Count);
        var unexpected = offenders.Where(o =>
        {
            var file = o[..o.IndexOf(':')];
            return !allowed.ContainsKey(file);
        }).ToList();
        var drift = Allowed
            .Where(a => counts.GetValueOrDefault(a.File) != a.Count)
            .Select(a => $"{a.File}: allow-list says {a.Count} call(s), source has {counts.GetValueOrDefault(a.File)} ({a.Reason})")
            .ToList();

        Assert.True(unexpected.Count == 0 && drift.Count == 0, LintReport.Of(
            "Product code must publish files through AtomicFile, not a bare File.Move/File.Replace.",
            unexpected.Concat(drift),
            "route the write through AtomicFile.WriteAllText/WriteAllBytes/Write/Publish/MoveAside; an allow-list entry may only be removed or lowered.",
            "docs/agents/services/infrastructure-update/storage.md"));
    }
}
