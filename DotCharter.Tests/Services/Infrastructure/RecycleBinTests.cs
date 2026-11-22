using DotCharter.Services.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Infrastructure;

public sealed class RecycleBinTests
{
    [Theory]
    [InlineData(false, true, true, 100L, 10L, 1L)] // Network/removable/unknown volume.
    [InlineData(true, false, true, 100L, 10L, 1L)] // Cloud placeholder/reparse point/uncertain ancestry.
    [InlineData(true, true, false, 100L, 10L, 1L)] // Bin disabled.
    [InlineData(true, true, null, 100L, 10L, 1L)] // Unknown enabled setting.
    [InlineData(true, true, true, null, 10L, 1L)] // Unknown quota.
    [InlineData(true, true, true, 100L, null, 1L)] // Unknown current bin usage.
    [InlineData(true, true, true, 100L, 10L, null)] // Unknown candidate size.
    [InlineData(true, true, true, 100L, 10L, 101L)] // Item exceeds bin quota.
    [InlineData(true, true, true, 100L, 99L, 2L)] // Not enough remaining capacity.
    [InlineData(true, true, true, 0L, 0L, 0L)] // Zero-sized quota is unavailable.
    [InlineData(true, true, true, 100L, -1L, 1L)]
    [InlineData(true, true, true, 100L, 0L, -1L)]
    [InlineData(true, true, true, long.MaxValue, long.MaxValue, 1L)] // Addition must not overflow.
    public void Unproven_recycle_capacity_leaves_the_item_and_logs_once(bool fixedLocal, bool plainPath,
        bool? enabled, long? quota, long? used, long? size)
    {
        WithFile(file =>
        {
            var messages = new List<string>();
            RecycleBin.Remove(file, _ => new RecycleBinStatus(fixedLocal, plainPath, enabled, quota, used, size),
                (path, _) => File.Delete(path), messages.Add);
            Assert.True(File.Exists(file));
            Assert.Equal("keep", File.ReadAllText(file));
            Assert.Single(messages);
        });
    }

    [Fact]
    public void A_failed_probe_leaves_the_item_and_logs_once()
    {
        WithFile(file =>
        {
            var messages = new List<string>();
            RecycleBin.Remove(file, _ => throw new IOException("Volume settings unavailable"),
                (path, _) => File.Delete(path), messages.Add);
            Assert.True(File.Exists(file));
            Assert.Equal("keep", File.ReadAllText(file));
            Assert.Single(messages);
        });
    }

    [Fact]
    public void A_missing_native_probe_entry_point_leaves_the_item_and_logs_once()
    {
        WithFile(file =>
        {
            var messages = new List<string>();
            RecycleBin.Remove(file, _ => throw new EntryPointNotFoundException("SHQueryRecycleBinW"),
                (path, _) => File.Delete(path), messages.Add);
            Assert.Equal("keep", File.ReadAllText(file));
            Assert.Single(messages);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Repeated_refusals_for_the_same_path_are_logged_once(bool probeFails)
    {
        WithFile(file =>
        {
            var messages = new List<string>();
            RecycleBinStatus Probe(string _) => probeFails
                ? throw new IOException("Volume settings unavailable")
                : new RecycleBinStatus(true, true, null, null, null, null);
            RecycleBin.Remove(file, Probe, (path, _) => File.Delete(path), messages.Add);
            RecycleBin.Remove(file, Probe, (path, _) => File.Delete(path), messages.Add);
            Assert.Equal("keep", File.ReadAllText(file));
            Assert.Single(messages);
        });
    }

    [Fact]
    public void Concurrent_refusals_for_the_same_path_are_logged_once()
    {
        WithFile(file =>
        {
            var messages = new System.Collections.Concurrent.ConcurrentBag<string>();
            RecycleBinStatus Probe(string _) => new(true, true, null, null, null, null);
            void Execute(string path, uint _) => File.Delete(path);
            Parallel.For(0, 8, _ => RecycleBin.Remove(file, Probe, Execute, messages.Add));
            Assert.Equal("keep", File.ReadAllText(file));
            Assert.Single(messages);
        });
    }

    [Theory]
    [InlineData(null, 1)]
    [InlineData(2, 1)]
    [InlineData("0", 1)]
    [InlineData(0L, 1)]
    [InlineData(0, null)]
    [InlineData(0, 0)]
    [InlineData(0, -1)]
    [InlineData(0, "1")]
    [InlineData(0, 1L)]
    [InlineData(1, 1)]
    public void Missing_unknown_or_disabled_volume_registry_settings_leave_the_item(object? nukeOnDelete, object? maxCapacity)
    {
        WithFile(file =>
        {
            var settings = RecycleBinSettings.FromValues(nukeOnDelete, maxCapacity);
            var messages = new List<string>();
            RecycleBin.Remove(file, _ => new RecycleBinStatus(true, true, settings.IsEnabled, settings.QuotaBytes, 0, 1),
                (path, _) => File.Delete(path), messages.Add);
            Assert.True(File.Exists(file));
            Assert.Equal("keep", File.ReadAllText(file));
            Assert.Single(messages);
        });
    }

    [Fact]
    public void Explicit_volume_DWORD_quota_is_measured_in_mebibytes()
    {
        WithFile(file =>
        {
            var settings = RecycleBinSettings.FromValues(0, 1);
            var messages = new List<string>();
            RecycleBin.Remove(file, _ => new RecycleBinStatus(true, true, settings.IsEnabled, settings.QuotaBytes, 0, 1024 * 1024),
                (path, _) => File.Delete(path), messages.Add);
            Assert.False(File.Exists(file));
            Assert.Empty(messages);
        });
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData(0, null, true)]
    [InlineData(1, null, false)]
    [InlineData("0", null, false)]
    [InlineData(0L, null, false)]
    [InlineData(null, 10, false)]
    [InlineData(0, 0, false)]
    public void Policy_conflicts_or_unknown_policy_types_veto_unattended_recycling(object? noRecycleFiles,
        object? recycleBinSize, bool allowed) =>
        Assert.Equal(allowed, RecycleBinSettings.PolicyAllowsRecycling(noRecycleFiles, recycleBinSize));

    [Theory]
    [InlineData(100, 10, 1)]
    [InlineData(100, 99, 1)]
    public void Verified_capacity_permits_only_a_silent_recycle_operation(long quota, long used, long size)
    {
        WithFile(file =>
        {
            var messages = new List<string>();
            RecycleBin.Remove(file, _ => new RecycleBinStatus(true, true, true, quota, used, size), (path, flags) =>
            {
                Assert.Equal(file, path);
                Assert.NotEqual(0u, flags & 0x00800000u); // Suppress third-party copy hooks that can show their own UI.
                Assert.Equal(0x00980614u, flags); // Recycle, early-failure, no errors/confirmation/progress UI.
                File.Delete(path);
            }, messages.Add);
            Assert.False(File.Exists(file));
            Assert.Empty(messages);
        });
    }

    [Fact]
    public void An_item_another_caller_already_took_counts_as_removed_but_a_refusal_still_throws()
    {
        // A second tidy of the same folder reached the shell after the first had
        // recycled the item; IFileOperation failed 0x80070002 and the tidy retried forever.
        static RecycleBinStatus Probe(string _) => new(true, true, true, 100, 0, 1);
        WithFile(file => RecycleBin.Remove(file, Probe, (path, _) =>
        {
            File.Move(path, path + ".taken");
            throw new IOException("The Recycle Bin could not accept the item; it was left in place.",
                new FileNotFoundException("gone", path));
        }, _ => Assert.Fail("Nothing was refused")));
        WithFile(file =>
        {
            Assert.Throws<IOException>(() => RecycleBin.Remove(file, Probe, (_, _) =>
                throw new IOException("The Recycle Bin could not accept the item; it was left in place.",
                    new FileNotFoundException("shell could not resolve it")), _ => { }));
            Assert.Equal("keep", File.ReadAllText(file));
        });
    }

    private static void WithFile(Action<string> action)
    {
        var directory = Directory.CreateTempSubdirectory("recycle_eligibility_").FullName;
        var file = Path.Combine(directory, "owned.txt");
        File.WriteAllText(file, "keep");
        try { action(file); }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void Removals_require_recycling_and_leave_the_file_when_the_backend_refuses()
    {
        var directory = Directory.CreateTempSubdirectory("recycle_contract_").FullName;
        var file = Path.Combine(directory, "owned.txt");
        File.WriteAllText(file, "keep");
        try
        {
            Assert.Throws<IOException>(() => RecycleBin.Remove(file, (path, flags) =>
            {
                Assert.Equal(file, path);
                Assert.NotEqual(0u, flags & 0x00080000); // FOFX_RECYCLEONDELETE: never permanent deletion.
                Assert.NotEqual(0u, flags & 0x00100000); // FOFX_EARLYFAILURE with FOF_NOERRORUI.
                throw new IOException("Recycling is unavailable");
            }));
            Assert.Equal("keep", File.ReadAllText(file));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
