using DotCharter.Services.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Infrastructure;

public sealed class RecyclePathSafetyTests
{
    [Theory]
    [InlineData(@"C:\Cloud\ONEDRI~1\Charts\owned.txt", @"C:\Cloud\OneDrive", @"C:\Cloud\OneDrive\Charts\owned.txt", @"C:\Cloud\OneDrive")]
    [InlineData(@"C:\Cloud\OneDrive\Charts\owned.txt", @"\\?\C:\Cloud\OneDrive", @"C:\Cloud\OneDrive\Charts\owned.txt", @"C:\Cloud\OneDrive")]
    [InlineData(@"\\?\C:\Cloud\OneDrive\Charts\owned.txt", @"C:\Cloud\OneDrive", @"C:\Cloud\OneDrive\Charts\owned.txt", @"C:\Cloud\OneDrive")]
    [InlineData(@"C:\Cloud\OneDrive\Charts\owned.txt", @"C:\Cloud\ONEDRI~1", @"C:\Cloud\OneDrive\Charts\owned.txt", @"C:\Cloud\OneDrive")]
    public void Short_and_namespace_aliases_cannot_hide_a_cloud_location(string path, string root,
        string canonicalPath, string canonicalRoot)
    {
        string Canonicalize(string value) => value == path ? canonicalPath : canonicalRoot;
        Assert.False(RecyclePathSafety.IsOutsideCloudRoot(path, root, Canonicalize));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Uncertain_candidate_or_cloud_root_canonicalization_refuses_removal(bool candidateFails)
    {
        const string path = @"C:\Charts\owned.txt";
        const string root = @"C:\Cloud\OneDrive";
        string Canonicalize(string value) => (value == path) == candidateFails
            ? throw new IOException("Canonical path unavailable") : value;
        Assert.False(RecyclePathSafety.IsOutsideCloudRoot(path, root, Canonicalize));
    }

    [Theory]
    [InlineData(@"\\server\share\OneDrive")]
    [InlineData(@"\\?\UNC\server\share\OneDrive")]
    [InlineData(@"C:OneDrive")]
    public void Unknown_cloud_root_namespaces_refuse_removal_without_native_resolution(string root)
    {
        var resolved = new List<string>();
        string Canonicalize(string value)
        {
            resolved.Add(value);
            return value;
        }
        Assert.False(RecyclePathSafety.IsOutsideCloudRoot(@"C:\Charts\owned.txt", root, Canonicalize));
        Assert.Empty(resolved);
    }

    [Fact]
    public void Only_an_unambiguous_matching_volume_root_can_establish_canonical_identity()
    {
        Assert.False(RecyclePathSafety.HasSingleVolumeRoot(@"C:\", []));
        Assert.False(RecyclePathSafety.HasSingleVolumeRoot(@"C:\", [@"D:\"]));
        Assert.False(RecyclePathSafety.HasSingleVolumeRoot(@"C:\", [@"C:\", @"D:\"]));
        Assert.True(RecyclePathSafety.HasSingleVolumeRoot(@"C:\", [@"c:\"]));
    }

    [Fact]
    public void A_known_local_path_outside_the_canonical_cloud_root_is_eligible() =>
        Assert.True(RecyclePathSafety.IsOutsideCloudRoot(@"C:\Charts\owned.txt", @"C:\Cloud\OneDrive", path => path));
}
