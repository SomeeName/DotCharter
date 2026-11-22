using DotCharter.Services.Export;
using DotCharter.Services.Export.Rocksmith;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Export;

public sealed class RocksmithTestPackageOwnershipTests : IDisposable
{
    private const string Id = "11111111111111111111111111111111";
    private const string OtherId = "22222222222222222222222222222222";
    private readonly string _root = Directory.CreateTempSubdirectory("test_package_owner_").FullName;
    private readonly List<string> _recycled = [];

    private string ProjectPath => Path.Combine(_root, "project", "song.dcproj");
    private string Folder => Path.Combine(_root, "game", "dlc");
    private string Ledger => Path.Combine(_root, "project", ".dotcharter", "test-packages", Id + ".json");

    public RocksmithTestPackageOwnershipTests()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ProjectPath)!);
        Directory.CreateDirectory(Folder);
        File.WriteAllText(ProjectPath, "project");
    }

    public void Dispose() => BestEffortDelete.Directory(_root);

    private RocksmithTestPackageOwnership Owner(string id = Id, Action<string>? recycle = null) =>
        new(ProjectPath, id, Folder, recycle ?? Recycle);

    private void Recycle(string path)
    {
        _recycled.Add(path);
        var retained = Path.Combine(_root, "recycled", Path.GetFileName(path));
        Directory.CreateDirectory(Path.GetDirectoryName(retained)!);
        File.Move(path, retained);
    }

    private static Task<RocksmithBuildResult> Publish(RocksmithTestPackageOwnership owner, string path, string bytes,
        params string[] superseded) => owner.BuildAndCleanUpAsync(async () =>
        {
            await owner.BeforePublishAsync(path, TestContext.Current.CancellationToken);
            File.WriteAllText(path, bytes);
            return new RocksmithBuildResult(path, "Song", [], []);
        }, superseded);

    [Fact]
    public async Task A_matching_foreign_test_name_is_never_recycled()
    {
        var old = Path.Combine(Folder, "Artist_Title_test_v2_p.psarc");
        File.WriteAllText(old, "foreign package");
        await Publish(Owner(), Path.Combine(Folder, "Artist_Title_test_p.psarc"), "new package", old);
        Assert.Equal("foreign package", File.ReadAllText(old));
        Assert.Empty(_recycled);
    }

    [Theory]
    [InlineData("oldkey_p.psarc", "Artist_Title_test_p.psarc")]
    [InlineData("Artist_Title_test_p.psarc", "Artist_Title_test_rsxr_p.psarc")]
    [InlineData("Artist_Title_test_rsxr_p.psarc", "Artist_Title_test_p.psarc")]
    [InlineData("Artist_Title_test_v2_m.psarc", "Artist_Title_test_v3_m.psarc")]
    public async Task Owned_old_and_new_naming_variants_are_recycled_after_complete_replacement(string oldName, string newName)
    {
        var old = Path.Combine(Folder, oldName);
        await Publish(Owner(), old, "previous package");
        await Publish(Owner(), Path.Combine(Folder, newName), "replacement", old);
        Assert.Equal([old], _recycled);
        Assert.False(File.Exists(old));
        Assert.Equal("previous package", File.ReadAllText(Path.Combine(_root, "recycled", oldName)));
    }

    [Fact]
    public async Task A_previously_owned_package_edited_after_publication_is_preserved()
    {
        var old = Path.Combine(Folder, "oldkey_p.psarc");
        await Publish(Owner(), old, "ours");
        File.WriteAllText(old, "edited package");
        await Publish(Owner(), Path.Combine(Folder, "Artist_Title_test_p.psarc"), "new", old);
        Assert.Equal("edited package", File.ReadAllText(old));
        Assert.Empty(_recycled);
    }

    [Fact]
    public async Task Another_project_in_the_same_project_directory_cannot_claim_the_package()
    {
        var old = Path.Combine(Folder, "oldkey_p.psarc");
        await Publish(Owner(), old, "project one");
        await Publish(Owner(OtherId), Path.Combine(Folder, "Artist_Title_test_p.psarc"), "project two", old);
        Assert.Equal("project one", File.ReadAllText(old));
        Assert.Empty(_recycled);
    }

    [Fact]
    public async Task An_existing_foreign_destination_is_refused_before_publication()
    {
        var path = Path.Combine(Folder, "Artist_Title_test_p.psarc");
        File.WriteAllText(path, "foreign");
        var refusal = await Assert.ThrowsAnyAsync<IOException>(() => Owner().BeforePublishAsync(path, TestContext.Current.CancellationToken));
        Assert.IsType<RocksmithTestPackageOwnershipException>(refusal);
        Assert.Equal("foreign", File.ReadAllText(path));
    }

    [Fact]
    public async Task A_changed_destination_is_refused_before_publication()
    {
        var path = Path.Combine(Folder, "Artist_Title_test_p.psarc");
        await Publish(Owner(), path, "ours");
        File.WriteAllText(path, "changed");
        await Assert.ThrowsAnyAsync<IOException>(() => Owner().BeforePublishAsync(path, TestContext.Current.CancellationToken));
        Assert.Equal("changed", File.ReadAllText(path));
    }

    [Fact]
    public async Task The_successfully_reused_output_is_never_recycled()
    {
        var path = Path.Combine(Folder, "Artist_Title_test_p.psarc");
        await Publish(Owner(), path, "previous");
        await Publish(Owner(), path, "replacement", Path.Combine(Folder, ".", Path.GetFileName(path)));
        Assert.Equal("replacement", File.ReadAllText(path));
        Assert.Empty(_recycled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_or_canceled_builds_preserve_prior_packages_and_their_ownership(bool cancel)
    {
        var owner = Owner();
        var old = Path.Combine(Folder, "oldkey_p.psarc");
        await Publish(owner, old, "previous");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var build = cancel ? Task.FromCanceled<RocksmithBuildResult>(cancellation.Token)
            : Task.FromException<RocksmithBuildResult>(new IOException("encoding failed"));
        await Assert.ThrowsAnyAsync<Exception>(() => owner.BuildAndCleanUpAsync(() => build, [old]));
        Assert.Equal("previous", File.ReadAllText(old));
        await Owner().BeforePublishAsync(old, TestContext.Current.CancellationToken);
        Assert.Empty(_recycled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_refused_recycle_preserves_both_the_package_and_its_ownership(bool backendThrows)
    {
        void Refuse(string _)
        {
            if (backendThrows) throw new IOException("Recycle unavailable");
        }
        var owner = Owner(recycle: Refuse);
        var old = Path.Combine(Folder, "oldkey_p.psarc");
        await Publish(owner, old, "previous");
        await Publish(owner, Path.Combine(Folder, "Artist_Title_test_p.psarc"), "replacement", old);
        Assert.Equal("previous", File.ReadAllText(old));
        await Owner().BeforePublishAsync(old, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Ownership_is_persisted_only_under_the_saved_project_root()
    {
        var path = Path.Combine(Folder, "Artist_Title_test_p.psarc");
        await Publish(Owner(), path, "ours");
        Assert.True(File.Exists(Ledger));
        Assert.False(Directory.Exists(Path.Combine(Folder, ".dotcharter")));
        Assert.Equal([path], Directory.GetFiles(Folder));
        await Owner().BeforePublishAsync(path, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task An_unsaved_project_never_claims_or_cleans_existing_packages()
    {
        var old = Path.Combine(Folder, "oldkey_p.psarc");
        File.WriteAllText(old, "previous");
        var owner = new RocksmithTestPackageOwnership(null, Id, Folder, Recycle);
        await Publish(owner, Path.Combine(Folder, "Artist_Title_test_p.psarc"), "new", old);
        Assert.Equal("previous", File.ReadAllText(old));
        Assert.False(File.Exists(Ledger));
        Assert.Empty(_recycled);
    }

    [Fact]
    public async Task Corrupt_ownership_refuses_replacement_without_adopting_existing_bytes()
    {
        var path = Path.Combine(Folder, "Artist_Title_test_p.psarc");
        await Publish(Owner(), path, "ours");
        Assert.True(File.Exists(Ledger));
        File.WriteAllText(Ledger, "not json");
        await Assert.ThrowsAnyAsync<IOException>(() => Owner().BeforePublishAsync(path, TestContext.Current.CancellationToken));
        Assert.Equal("ours", File.ReadAllText(path));
    }

    [Fact]
    public async Task Ownership_in_another_test_folder_does_not_authorize_cleanup_here()
    {
        var otherFolder = Directory.CreateDirectory(Path.Combine(_root, "other-game")).FullName;
        var old = Path.Combine(otherFolder, "oldkey_p.psarc");
        var other = new RocksmithTestPackageOwnership(ProjectPath, Id, otherFolder, Recycle);
        await Publish(other, old, "other folder package");
        await Publish(Owner(), Path.Combine(Folder, "Artist_Title_test_p.psarc"), "new", old);
        Assert.Equal("other folder package", File.ReadAllText(old));
        Assert.Empty(_recycled);
    }

    [Fact]
    public async Task A_returned_path_without_this_builds_publication_approval_cannot_be_adopted()
    {
        var path = Path.Combine(Folder, "Artist_Title_test_p.psarc");
        File.WriteAllText(path, "foreign");
        var owner = Owner();
        await owner.BuildAndCleanUpAsync(() => Task.FromResult(new RocksmithBuildResult(path, "Song", [], [])), []);
        Assert.False(File.Exists(Ledger));
        await Assert.ThrowsAnyAsync<IOException>(() => owner.BeforePublishAsync(path, TestContext.Current.CancellationToken));
        Assert.Equal("foreign", File.ReadAllText(path));
    }

    [Fact]
    public async Task An_unsaved_instance_can_reuse_its_own_publication_without_persistence_or_cleanup()
    {
        var owner = new RocksmithTestPackageOwnership(null, Id, Folder, Recycle);
        var path = Path.Combine(Folder, "Artist_Title_test_p.psarc");
        await Publish(owner, path, "first");
        await Publish(owner, path, "second", path);
        Assert.Equal("second", File.ReadAllText(path));
        Assert.False(File.Exists(Ledger));
        Assert.Empty(_recycled);
    }

    [Fact]
    public async Task Recycling_forgets_the_receipt_before_an_identical_foreign_file_reappears()
    {
        var old = Path.Combine(Folder, "oldkey_p.psarc");
        await Publish(Owner(), old, "previous");
        await Publish(Owner(), Path.Combine(Folder, "Artist_Title_test_p.psarc"), "replacement", old);
        File.WriteAllText(old, "previous");
        await Assert.ThrowsAnyAsync<IOException>(() => Owner().BeforePublishAsync(old, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Canceled_publication_approval_leaves_a_foreign_destination_untouched()
    {
        var path = Path.Combine(Folder, "Artist_Title_test_p.psarc");
        File.WriteAllText(path, "foreign");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Owner().BeforePublishAsync(path, cancellation.Token));
        Assert.Equal("foreign", File.ReadAllText(path));
        Assert.False(File.Exists(Ledger));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Uncertain_target_absence_never_authorizes_publication(bool accessDenied)
    {
        var path = Path.Combine(Folder, "Artist_Title_test_p.psarc");
        FileAttributes Attributes(string _)
        {
            if (accessDenied) throw new UnauthorizedAccessException("Metadata access denied");
            throw new IOException("Metadata unavailable");
        }
        var owner = new RocksmithTestPackageOwnership(ProjectPath, Id, Folder, Recycle, Attributes);
        var refusal = await Assert.ThrowsAnyAsync<IOException>(() => owner.BeforePublishAsync(path, TestContext.Current.CancellationToken));
        Assert.IsType<RocksmithTestPackageOwnershipException>(refusal);
        Assert.NotNull(refusal.InnerException);
        Assert.False(File.Exists(path));
        Assert.False(File.Exists(Ledger));
        Assert.Empty(_recycled);
    }

    [Fact]
    public void A_project_id_cannot_escape_the_project_owned_metadata_directory() =>
        Assert.Throws<ArgumentException>(() => new RocksmithTestPackageOwnership(ProjectPath, "../escape", Folder));
}
