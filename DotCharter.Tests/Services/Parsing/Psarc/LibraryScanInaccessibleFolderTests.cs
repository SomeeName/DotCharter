using DotCharter.Services.Library;
using DotCharter.Tests.Services.Library;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Parsing.Psarc;

/// <summary>
///     A library scan must SKIP a folder it cannot read, not abandon the whole
///     library.
///     <para>
///         The scan walked the root with <c>SearchOption.AllDirectories</c>,
///         which is <c>EnumerationOptions.Compatible</c> — and that sets
///         <c>IgnoreInaccessible = false</c>. So the first denied directory threw
///         <see cref="UnauthorizedAccessException" /> out of the enumeration,
///         before a single archive was opened: no partial result, no use of the
///         index cache, both song lists empty and a "scan failed" status.
///     </para>
///     <para>
///         A library pointed at a whole DRIVE is a supported configuration —
///         <c>PurgeMissing</c> has a drive-root case for it — and every NTFS
///         volume root carries <c>System Volume Information</c>, denied to a
///         non-elevated user. The supported case was the broken one.
///     </para>
/// </summary>
public sealed class LibraryScanInaccessibleFolderTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dotcharter-scan-acl-").FullName;

    /// <summary>The enumeration options the scan actually uses. Structural: flip
    /// either flag and this fails, without needing an ACL.</summary>
    [Fact]
    public void TheScanWalksTheRootIgnoringWhatItCannotRead()
    {
        var options = LibraryArchiveFiles.RecursiveScanOptions;

        Assert.True(options.RecurseSubdirectories);
        Assert.True(options.IgnoreInaccessible, "one denied folder would abort the whole library scan");
        // Cleared back to zero: EnumerationOptions' own default skips hidden and
        // system entries, and a user may keep a library in a hidden folder. Only
        // the throw was meant to change.
        Assert.Equal(default, options.AttributesToSkip);
    }

    /// <summary>The behavioural half, on a real denied directory.</summary>
    [Fact]
    public async Task AFolderTheUserCannotListDoesNotAbandonTheRestOfTheLibrary()
    {
        // NOTE: this must stay a bare `return` — CA1416 (warnings are errors
        // here) only recognizes an OperatingSystem.IsWindows() early-return as
        // the guard for the Windows-only ACL APIs below.
        if (!OperatingSystem.IsWindows()) return;

        var readable = Path.Combine(_root, "readable");
        Directory.CreateDirectory(readable);
        // Not a valid archive — the scan must reach it and reject it on its
        // CONTENT, which is the proof the enumeration got past the denied folder.
        await File.WriteAllTextAsync(Path.Combine(readable, "song.dcsong"), "not a package",
            TestContext.Current.CancellationToken);

        var denied = new DirectoryInfo(Path.Combine(_root, "System Volume Information"));
        denied.Create();
        await File.WriteAllTextAsync(Path.Combine(denied.FullName, "hidden.dcsong"), "x",
            TestContext.Current.CancellationToken);

        var user = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        var rule = new System.Security.AccessControl.FileSystemAccessRule(
            user,
            System.Security.AccessControl.FileSystemRights.ListDirectory,
            System.Security.AccessControl.AccessControlType.Deny);
        var security = denied.GetAccessControl();
        security.AddAccessRule(rule);
        denied.SetAccessControl(security);

        try
        {
            // Enumerating it directly still throws — otherwise this test proves
            // nothing about the scan.
            Assert.Throws<UnauthorizedAccessException>(() =>
                Directory.EnumerateFiles(_root, "*.dcsong", SearchOption.AllDirectories).ToArray());

            var service = TestLibraries.Create(new FakeAppLogger());
            var entries = await service.ScanAsync(_root, progress: null,
                TestContext.Current.CancellationToken);

            // The bad archive yields no songs; the point is that the call
            // RETURNED instead of throwing the whole library away.
            Assert.NotNull(entries);
        }
        finally
        {
            var restore = denied.GetAccessControl();
            restore.RemoveAccessRule(rule);
            denied.SetAccessControl(restore);
        }
    }

    public void Dispose() => BestEffortDelete.Directory(_root);
}
