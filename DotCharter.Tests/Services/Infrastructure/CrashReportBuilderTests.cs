using DotCharter.Services.Infrastructure.CrashReporting;

namespace DotCharter.Tests.Services.Infrastructure;

public sealed class CrashReportBuilderTests
{
    private static CrashReportInput SampleInput() => new()
    {
        Source = "AppDomain",
        ExceptionType = "System.InvalidOperationException",
        ExceptionMessage = "boom",
        StackTrace = "   at DotCharter.Foo.Bar()",
        TimestampUtc = new DateTimeOffset(2026, 7, 10, 13, 5, 9, TimeSpan.Zero),
        AppVersion = "0.14.0",
        SystemInfo = new[]
        {
            new KeyValuePair<string, string>("OS", "Windows"),
            new KeyValuePair<string, string>("Processor count", "16")
        },
        RecentLogLines = new[] { "line one", "line two" }
    };

    [Fact]
    public void Build_ContainsAllSections()
    {
        var report = CrashReportBuilder.Build(SampleInput());

        Assert.Contains(CrashReportBuilder.Header, report);
        Assert.Contains("2026-07-10 13:05:09Z", report);
        Assert.Contains("Source: AppDomain", report);
        Assert.Contains("System.InvalidOperationException", report);
        Assert.Contains("Message: boom", report);
        Assert.Contains("at DotCharter.Foo.Bar()", report);
        Assert.Contains("--- System info ---", report);
        Assert.Contains("OS: Windows", report);
        Assert.Contains("Processor count: 16", report);
        Assert.Contains("--- Recent log", report);
        Assert.Contains("line one", report);
        Assert.Contains("line two", report);
    }

    [Fact]
    public void Build_AppliesRedactionToFreeTextFields()
    {
        var input = SampleInput() with
        {
            UserName = "Alex",
            ExceptionMessage = @"failed reading C:\Users\Alex\secret.txt with token=abc123secret",
            RecentLogLines = new[] { "signed in as Alex", "password=hunter2" }
        };

        var report = CrashReportBuilder.Build(input);

        Assert.DoesNotContain("Alex", report);
        Assert.DoesNotContain("hunter2", report);
        Assert.DoesNotContain("abc123secret", report);
        Assert.Contains(@"C:\Users\<user>\secret.txt", report);
    }

    [Fact]
    public void Build_HandlesEmptyLogAndSystemInfo()
    {
        var input = SampleInput() with
        {
            SystemInfo = Array.Empty<KeyValuePair<string, string>>(),
            RecentLogLines = Array.Empty<string>()
        };

        var report = CrashReportBuilder.Build(input);

        Assert.Contains("--- System info ---", report);
        Assert.Contains("--- Recent log (last 0 line(s)) ---", report);
        Assert.Contains("(none)", report);
    }

    [Fact]
    public void Build_IncludesInnerExceptionWhenPresent()
    {
        var input = SampleInput() with { InnerException = "[[1]] System.IOException: disk gone" };

        var report = CrashReportBuilder.Build(input);

        Assert.Contains("Inner exception(s):", report);
        Assert.Contains("System.IOException: disk gone", report);
    }

    [Fact]
    public void Build_OmitsInnerSection_WhenAbsent()
    {
        var report = CrashReportBuilder.Build(SampleInput());

        Assert.DoesNotContain("Inner exception(s):", report);
    }
}
