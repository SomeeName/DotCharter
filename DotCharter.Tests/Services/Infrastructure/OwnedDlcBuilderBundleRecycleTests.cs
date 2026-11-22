using DotCharter.Services.Export.DlcBuilder;

namespace DotCharter.Tests.Services.Infrastructure;

public sealed class OwnedDlcBuilderBundleRecycleTests
{
    [Fact]
    public void A_retained_stage_is_reported_as_a_failure_before_the_caller_can_reuse_it()
    {
        var directory = Directory.CreateTempSubdirectory("retained_bundle_").FullName;
        try
        {
            Assert.Throws<IOException>(() => OwnedDlcBuilderBundle.Recycle(directory, _ => { }));
            Assert.True(Directory.Exists(directory));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void A_recycled_stage_allows_the_caller_to_continue()
    {
        var directory = Directory.CreateTempSubdirectory("recycled_bundle_").FullName;
        try
        {
            OwnedDlcBuilderBundle.Recycle(directory, path => Directory.Delete(path));
            Assert.False(Directory.Exists(directory));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
