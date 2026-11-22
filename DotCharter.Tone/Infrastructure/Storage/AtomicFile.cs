namespace DotCharter.Tone.Infrastructure.Storage;

/// <summary>
///     Write-a-file-without-being-able-to-lose-it.
///
///     <para>A bare <see cref="File.WriteAllText(string,string)"/> TRUNCATES the target
///     before it writes. If the process dies in that window — a crash, a kill, a power
///     cut — the file on disk is empty or half-written, and the previous contents are
///     gone. For a scratch file that is a shrug. For the file holding every score,
///     personal best and phrase-mastery record the player has ever earned, it is the
///     whole database.</para>
///
///     <para>So: serialize to a randomly named sibling temp file (created exclusively),
///     force it to the platter, then RENAME
///     over the target. A rename within a volume is atomic on NTFS, so a reader either
///     sees the whole old file or the whole new one — never a torn one — and a crash at
///     any point leaves the previous version intact.</para>
///
///     <para><b>The flush is not optional.</b> <c>File.WriteAllText</c> returns once the
///     bytes are in the OS page cache, not once they are durable. Without
///     <c>Flush(flushToDisk: true)</c> a power cut can persist the RENAME while the
///     temp file's contents are still in flight — you get an atomically-swapped-in empty
///     file, which is the very failure this is supposed to prevent. This is the one part
///     the three hand-rolled copies of this pattern in the repo all missed.</para>
/// </summary>
public static class AtomicFile
{
    public const string TempSuffix = ".tmp";

    // On Windows the rename FAILS if anything currently has the destination open for
    // reading — File.ReadAllText takes a share mode that denies delete, so a save that
    // happens to collide with a load throws UnauthorizedAccessException. Every caller
    // here wraps the write in a try/catch that merely LOGS, so an unretried collision
    // would silently drop the save: a worse bug than the one this class exists to fix.
    //
    // v0.31 root-cause: the old 12 × 3 ms flat retry (36 ms total) was tuned for the
    // microsecond reader-collision window, but two real holders outlast 36 ms: a
    // CPU-starved concurrent reader (the torn-file test under full-suite load — the
    // documented flake) and, in production, an antivirus/indexer scan hold on the
    // just-replaced file. Beyond the budget the exception escaped and the save was
    // silently dropped. Now: bounded EXPONENTIAL backoff — 16 attempts, 2 ms doubling
    // per attempt pair, capped at 48 ms/step (~430 ms worst case, still snappy for a
    // user save). The growing, irregular spacing also de-synchronizes the retry from
    // a periodic reader's open/close cadence instead of losing every race the same way.
    //
    // The retry is the fallback for holders we do NOT own. Our
    // OWN readers (ReadAllText/ReadAllBytes, FileShare.Delete) do not need it at
    // all, because ReplaceWithRetry renames with POSIX semantics first — see
    // WindowsPosixRename. v0.31 believed the share-delete reader already let
    // MoveFileEx through; it did not (measured: MoveFileEx is denied by ANY open
    // handle on the destination), which is why the "root-caused" flake came back
    // twice under a full parallel gate as UnauthorizedAccessException out of
    // ReplaceWithRetry, never as a torn read.
    private const int MoveAttempts = 16;
    private const int MoveBackoffBaseMs = 2;
    private const int MoveBackoffCapMs = 48;

    /// <summary>
    ///     A sibling stage name no other process can know in advance:
    ///     <c>&lt;target&gt;.&lt;random&gt;.tmp</c>. The writers create it with
    ///     <see cref="FileMode.CreateNew" />, so a file or link planted at a guessed name is
    ///     never opened, truncated or written through - a collision (vanishingly unlikely
    ///     with 128 random bits) is an error, not a takeover of someone else's file.
    /// </summary>
    private static string NewTempPath(string path)
        => path + "." + Guid.NewGuid().ToString("N") + TempSuffix;

    /// <summary>Atomically replaces <paramref name="path"/> with
    /// <paramref name="contents"/>. Creates the directory if needed.</summary>
    public static void WriteAllText(string path, string contents)
    {
        Write(path, stream =>
        {
            using var writer = new StreamWriter(stream, leaveOpen: true);
            writer.Write(contents);
            writer.Flush();
        });
    }

    /// <summary>Same contract for bytes.</summary>
    public static void WriteAllBytes(string path, byte[] contents)
        => Write(path, stream => stream.Write(contents, 0, contents.Length));

    /// <summary>Asynchronous UTF-8 text export with the same staging and durability
    /// contract as <see cref="WriteAllText" />. Cancellation before publication
    /// leaves an existing destination untouched and removes the stage.</summary>
    public static Task WriteAllTextAsync(string path, string contents, CancellationToken cancellationToken = default) =>
        WriteAllTextCoreAsync(path, contents, cancellationToken, WaitForReplacement);

    internal static async Task WriteAllTextCoreAsync(string path, string contents, CancellationToken cancellationToken,
        Action<int, CancellationToken> wait)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var temp = NewTempPath(path);
        var created = false;
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       bufferSize: 4096, useAsync: true))
            {
                created = true;
                using var writer = new StreamWriter(stream, leaveOpen: true);
                await writer.WriteAsync(contents.AsMemory(), cancellationToken).ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            ReplaceWithRetryCore(temp, path, cancellationToken, wait);
        }
        finally
        {
            if (created) BestEffortDelete.File(temp);
        }
    }

    /// <summary>
    ///     Atomically replaces <paramref name="path"/> with whatever
    ///     <paramref name="write"/> streams into a randomly named sibling temp file.
    ///     The stream is flushed to disk before the swap and the swap retries, exactly
    ///     like <see cref="WriteAllBytes"/>; use this for serializers, encoders and
    ///     <c>BinaryWriter</c>s that own the stream. The stream is left open for
    ///     <paramref name="write"/>'s caller to flush: do not dispose it inside the
    ///     callback. A throw from <paramref name="write"/> removes the temp file and
    ///     leaves the destination untouched.
    /// </summary>
    public static void Write(string path, Action<Stream> write) => WriteCore(path, write, CancellationToken.None);

    internal static void WriteCore(string path, Action<Stream> write, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var temp = NewTempPath(path);
        var created = false;
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                write(stream);
                stream.Flush(true);
            }
        }
        catch
        {
            if (created) BestEffortDelete.File(temp);
            throw;
        }

        ReplaceWithRetryCore(temp, path, cancellationToken, WaitForReplacement);
    }

    /// <summary>
    ///     Publishes a staged file that something else wrote (an encoder, a child
    ///     process, a download): flush it to disk, then <see cref="ReplaceWithRetry"/>.
    ///     Replaces an existing destination.
    /// </summary>
    public static void Publish(string staged, string path)
    {
        FlushFile(staged);
        ReplaceWithRetry(staged, path);
    }

    /// <summary>Flushes and publishes a stage only if the destination is missing.
    /// An existing destination wins, including a concurrent publisher. The owned
    /// stage is swept when publication is refused or fails.</summary>
    public static bool PublishNew(string staged, string path)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(Path.GetFullPath(staged), Path.GetFullPath(path), comparison))
            throw new ArgumentException("The stage must differ from its destination.", nameof(staged));

        try
        {
            if (File.Exists(path)) return false;
            FlushFile(staged);
            try
            {
                File.Move(staged, path);
                return true;
            }
            catch (IOException) when (File.Exists(path))
            {
                return false;
            }
        }
        finally
        {
            BestEffortDelete.File(staged);
        }
    }

    /// <summary>
    ///     Moves existing bytes with explicit replacement policy. Without replacement,
    ///     a destination collision fails atomically; the source is never swept, even on failure.
    ///     Replacement delegates to the quarantine overload with its existing retry contract.
    /// </summary>
    public static void MoveAside(string path, string aside, bool overwrite)
    {
        if (!overwrite)
        {
            File.Move(path, aside);
            return;
        }
        MoveAside(path, aside);
    }

    /// <summary>Quarantines a corrupt store, replacing the aside while preserving the source if the move fails.</summary>
    public static void MoveAside(string path, string aside)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (WindowsPosixRename.TryReplace(path, aside)) return;
                File.Move(path, aside, overwrite: true);
                return;
            }
            catch (Exception ex) when (attempt < MoveAttempts &&
                                       ex is UnauthorizedAccessException or IOException)
            {
                Thread.Sleep(Math.Min(MoveBackoffBaseMs << ((attempt - 1) / 2), MoveBackoffCapMs));
            }
        }
    }

    /// <summary>
    ///     The read counterpart (v0.31): opens with <see cref="FileShare.Delete"/>
    ///     (+ ReadWrite) so a concurrent <see cref="WriteAllText"/> rename can
    ///     proceed WHILE the file is being read — the open handle keeps reading
    ///     the old content in full (rename atomicity), the writer never gets its
    ///     rename denied. A plain <c>File.ReadAllText</c> denies delete, which is
    ///     exactly the save/load collision the write-side retry exists to absorb;
    ///     prefer this for any file that <see cref="AtomicFile"/> writes. No
    ///     bounded write-retry can beat a zero-gap reader — the sharing mode is
    ///     the structural fix, the retry the fallback for readers we don't own
    ///     (AV, indexers).
    ///     <para>
    ///         <b>The share mode is only half of that fix.</b> A share-delete
    ///         handle lets a POSIX-semantics rename through; it does NOT let
    ///         <c>MoveFileEx</c> through (measured — see the note above the retry
    ///         constants). <see cref="ReplaceWithRetry" /> therefore renames via
    ///         <see cref="WindowsPosixRename" /> first, and it is the PAIR — this
    ///         reader plus that writer — that makes a save-during-load collision
    ///         a non-event. Pinned by <c>AtomicFileTests
    ///         .ReplaceWithRetry_SucceedsWhileAShareDeleteReaderHoldsTheDestination</c>.
    ///     </para>
    /// </summary>
    public static string ReadAllText(string path)
    {
        using var stream = OpenRead(path);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>The read handle <see cref="ReadAllText" /> takes, for a reader that
    /// parses straight from the stream: shares read/write/DELETE, so a writer's
    /// swap proceeds while this handle keeps reading the whole old file.</summary>
    public static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    /// <summary>Asynchronous text read with the same share-delete contract.</summary>
    public static async Task<string> ReadAllTextAsync(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, bufferSize: 4096, useAsync: true);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync().ConfigureAwait(false);
    }

    /// <summary>Same contract for bytes.</summary>
    public static byte[] ReadAllBytes(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>
    ///     The durability half of the pattern, for writers that cannot go through
    ///     <see cref="WriteAllText" />/<see cref="WriteAllBytes" /> because they stream a
    ///     large artifact into the temp file through some other API (a zip, an encoder, a
    ///     serializer that owns its own stream). Call it on the TEMP file, between the
    ///     write and <see cref="ReplaceWithRetry" />.
    ///     <para>
    ///         Without it the staged-then-swap is only half done: the rename is atomic,
    ///         but a power cut can persist the RENAME while the temp file's contents are
    ///         still in the page cache — you get an atomically-swapped-in empty or
    ///         truncated file. <c>FlushFileBuffers</c> (what <c>Flush(flushToDisk: true)</c>
    ///         issues) flushes everything cached for the file, not just what this handle
    ///         wrote, so opening it purely to flush is enough.
    ///     </para>
    /// </summary>
    public static void FlushFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        stream.Flush(flushToDisk: true);
    }

    /// <summary>
    ///     The atomic swap. Retries a locked destination (a concurrent reader, an
    ///     AV/indexer hold) with bounded exponential backoff rather than letting
    ///     the caller's catch-and-log quietly eat the save.
    ///     <para>
    ///         Public because plenty of writers cannot go through
    ///         <see cref="WriteAllText" /> — they stream a large artifact to a
    ///         sibling temp file (a project, a psarc, a stem archive) and then
    ///         swap. Those need the SAME retry: a hand-rolled
    ///         <c>File.Move(overwrite: true)</c> throws on the first transient
    ///         lock, which surfaces as "save failed" and leaves the temp file
    ///         orphaned next to the real one.
    ///     </para>
    ///     <para>
    ///         When the retry budget runs out the exception propagates — and the
    ///         staged file is DELETED on the way past. It is dead weight the
    ///         moment the swap gives up: nothing in the app ever reads a
    ///         leftover stage back (a <c>.saving</c> project, a <c>.tmp</c>
    ///         config), the destination still holds the previous version, and
    ///         the caller has already been told the write failed. Leaving it
    ///         parked next to the real file is how a user ends up with
    ///         <c>MySong.dcproj.saving</c> beside <c>MySong.dcproj</c> after one
    ///         antivirus hiccup — a file that looks like a newer save and is
    ///         not, and that nothing ever cleans up. The delete is best-effort
    ///         and never masks the original failure.
    ///     </para>
    /// </summary>
    public static void ReplaceWithRetry(string temp, string path) =>
        ReplaceWithRetryCore(temp, path, CancellationToken.None, WaitForReplacement);

    private static void WaitForReplacement(int milliseconds, CancellationToken cancellationToken)
    {
        if (cancellationToken.WaitHandle.WaitOne(milliseconds)) cancellationToken.ThrowIfCancellationRequested();
    }

    internal static void ReplaceWithRetryCore(string temp, string path, CancellationToken cancellationToken,
        Action<int, CancellationToken> wait)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                // POSIX-semantics rename FIRST (Windows 10 1607+, NTFS): it
                // replaces a destination that another handle holds open with
                // FILE_SHARE_DELETE — our own AtomicFile.ReadAllText readers —
                // and that handle goes on reading the OLD file whole. The plain
                // MoveFileEx below is DENIED by any open handle on the target,
                // share-delete or not (measured on Windows 11 26200: "Access to
                // the path is denied" against a FileShare.ReadWrite|Delete
                // reader), so it only ever won in the gaps between a reader's
                // open/close, and a CPU-starved parallel test suite could keep a
                // reader in the way for longer than the whole retry budget. Any
                // failure here — a volume without the flag, an older Windows, a
                // holder that did NOT share delete — falls through to the classic
                // path, whose exceptions the retry loop already understands.
                if (WindowsPosixRename.TryReplace(temp, path)) return;

                File.Move(temp, path, overwrite: true);
                return;
            }
            catch (Exception ex) when (attempt < MoveAttempts &&
                                       ex is UnauthorizedAccessException or IOException)
            {
                // 2,2,4,4,8,8,… capped at 48 ms — ~430 ms total across 16 attempts.
                var backoff = Math.Min(MoveBackoffBaseMs << ((attempt - 1) / 2), MoveBackoffCapMs);
                try
                {
                    wait(backoff, cancellationToken);
                }
                catch
                {
                    BestEffortDelete.File(temp);
                    throw;
                }
            }
            catch
            {
                // Out of budget, or an exception class the retry does not
                // understand. The swap is not happening, so the stage is
                // garbage — sweep it before the failure propagates. See the
                // doc above: an orphaned "<project>.dcproj.saving" reads as a
                // newer save that nothing will ever open.
                BestEffortDelete.File(temp);
                throw;
            }
        }
    }
}
