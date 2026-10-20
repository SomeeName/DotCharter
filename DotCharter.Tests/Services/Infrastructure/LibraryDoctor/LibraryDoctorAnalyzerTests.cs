using DotCharter.Services.Infrastructure.LibraryDoctor;

namespace DotCharter.Tests.Services.Infrastructure.LibraryDoctor;

public sealed class LibraryDoctorAnalyzerTests
{
    private static LibraryDoctorSong Song(
        string path,
        string key = "SONG",
        string charter = "",
        string version = "",
        string tuning = "E A D G B E",
        int arrangements = 1,
        AudioAvailability audio = AudioAvailability.Present,
        string title = "Title",
        string artist = "Artist")
        => new()
        {
            PsarcPath = path,
            SongKey = key,
            Charter = charter,
            Version = version,
            TuningSummary = tuning,
            LoadableArrangementCount = arrangements,
            Audio = audio,
            Title = title,
            Artist = artist
        };

    private static LibraryDoctorReport Analyze(params LibraryDoctorSong[] songs)
        => LibraryDoctorAnalyzer.Analyze(new LibraryDoctorSnapshot { Songs = songs }, FakeLocalization.English());

    [Fact]
    public void CleanLibrary_ReportsHealthy()
    {
        var report = Analyze(
            Song("a.psarc", "S1"),
            Song("b.psarc", "S2"));

        Assert.True(report.IsHealthy);
        Assert.Empty(report.Issues);
        Assert.Equal(2, report.SongsScanned);
        Assert.Equal(2, report.PackagesScanned);
    }

    [Fact]
    public void SameIdentityAcrossTwoPackages_FlagsBothAsDuplicates()
    {
        var report = Analyze(
            Song("copy1.psarc", "HEY", charter: "Bob", version: "1"),
            Song("copy2.psarc", "HEY", charter: "Bob", version: "1"));

        var dupes = report.Issues.Where(i => i.Kind == LibraryIssueKind.DuplicatePackage).ToList();
        Assert.Equal(2, dupes.Count);
        Assert.Contains(dupes, d => d.PsarcPath == "copy1.psarc");
        Assert.Contains(dupes, d => d.PsarcPath == "copy2.psarc");
    }

    /// <summary>The finding's words come from the string table (they were an English literal
    /// ending "other package(s).") and the count picks its form: one other package of a pair.</summary>
    [Fact]
    public void ADuplicatePair_NamesOneOtherPackage_InTheTablesWords()
    {
        var report = Analyze(
            Song("copy1.psarc", "HEY"),
            Song("copy2.psarc", "HEY"));

        var detail = report.Issues.First(i => i.Kind == LibraryIssueKind.DuplicatePackage).Detail;
        Assert.Equal(FakeLocalization.English().Format("LibraryDoctor_Detail_DuplicateOne", 1, "HEY"), detail);
        Assert.DoesNotContain("(s)", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void DifferentCharterOrVersion_IsNotADuplicate()
    {
        var report = Analyze(
            Song("a.psarc", "HEY", charter: "Bob", version: "1"),
            Song("b.psarc", "HEY", charter: "Bob", version: "2"),
            Song("c.psarc", "HEY", charter: "Sue", version: "1"));

        Assert.DoesNotContain(report.Issues, i => i.Kind == LibraryIssueKind.DuplicatePackage);
    }

    [Fact]
    public void MultiSongPack_SameKeyOneFile_IsNotADuplicate()
    {
        // Two songs sharing the SAME identity inside the SAME package is not a
        // cross-package duplicate.
        var report = Analyze(
            Song("pack.psarc", "HEY"),
            Song("pack.psarc", "HEY"));

        Assert.DoesNotContain(report.Issues, i => i.Kind == LibraryIssueKind.DuplicatePackage);
    }

    [Fact]
    public void MissingAudio_IsFlagged_UnknownIsNot()
    {
        var report = Analyze(
            Song("silent.psarc", "S1", audio: AudioAvailability.Missing),
            Song("unknown.psarc", "S2", audio: AudioAvailability.Unknown),
            Song("ok.psarc", "S3", audio: AudioAvailability.Present));

        var missing = report.Issues.Where(i => i.Kind == LibraryIssueKind.MissingAudio).ToList();
        Assert.Single(missing);
        Assert.Equal("silent.psarc", missing[0].PsarcPath);
    }

    [Fact]
    public void NoLoadableArrangement_IsAnError()
    {
        var report = Analyze(Song("broken.psarc", "S1", arrangements: 0));

        var issue = Assert.Single(report.Issues, i => i.Kind == LibraryIssueKind.NoArrangements);
        Assert.Equal(LibraryIssueSeverity.Error, issue.Severity);
    }

    [Theory]
    [InlineData("E A D G B E")] // standard
    [InlineData("D A D G B E")] // drop D
    [InlineData("E Standard")] // parser textual fallback
    public void StandardTunings_AreKnown(string tuning)
        => Assert.Equal(LibraryDoctorAnalyzer.TuningClassification.Known,
            LibraryDoctorAnalyzer.ClassifyTuning(tuning));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("E Standard Tuning Garbage Here Extra")] // >7 tokens / non-notes
    [InlineData("X Y Z Q P R")] // note-shaped but not valid notes
    [InlineData("E A D")] // too few strings
    public void BrokenTunings_AreInvalid(string tuning)
        => Assert.Equal(LibraryDoctorAnalyzer.TuningClassification.Invalid,
            LibraryDoctorAnalyzer.ClassifyTuning(tuning));

    [Fact]
    public void ValidButUnnamedTuning_IsUnknown()
    {
        // Six valid note tokens that don't match any named tuning pattern.
        Assert.Equal(LibraryDoctorAnalyzer.TuningClassification.Unknown,
            LibraryDoctorAnalyzer.ClassifyTuning("C# F# B Eb G# C#"));
    }

    [Fact]
    public void InvalidTuning_ProducesErrorIssue_UnknownProducesInfo()
    {
        var report = Analyze(
            Song("bad.psarc", "S1", tuning: "not a tuning at all really"),
            Song("weird.psarc", "S2", tuning: "C# F# B Eb G# C#"));

        var invalid = Assert.Single(report.Issues, i => i.Kind == LibraryIssueKind.InvalidTuning);
        Assert.Equal(LibraryIssueSeverity.Error, invalid.Severity);

        var unknown = Assert.Single(report.Issues, i => i.Kind == LibraryIssueKind.UnknownTuning);
        Assert.Equal(LibraryIssueSeverity.Info, unknown.Severity);
    }

    [Fact]
    public void UnreadablePackages_AreReported()
    {
        var report = LibraryDoctorAnalyzer.Analyze(new LibraryDoctorSnapshot
        {
            Songs = [Song("ok.psarc", "S1")],
            UnreadablePackages = [new LibraryDoctorUnreadablePackage("corrupt.psarc", "bad header")]
        }, FakeLocalization.English());

        var issue = Assert.Single(report.Issues, i => i.Kind == LibraryIssueKind.UnreadablePackage);
        Assert.Equal(LibraryIssueSeverity.Error, issue.Severity);
        Assert.Contains("bad header", issue.Detail);
        // Two distinct packages contributed (one song file + one unreadable).
        Assert.Equal(2, report.PackagesScanned);
    }

    [Fact]
    public void Groups_AreOrderedBySeverity_AndCountsMatch()
    {
        var report = Analyze(
            Song("a.psarc", "S1", audio: AudioAvailability.Missing),
            Song("b.psarc", "S2", arrangements: 0),
            Song("c.psarc", "S3", tuning: "C# F# B Eb G# C#"));

        Assert.NotEmpty(report.Groups);
        // Error groups sort before info groups.
        Assert.Equal(LibraryIssueSeverity.Error, report.Groups[0].Severity);
        Assert.Equal(report.TotalIssueCount, report.Groups.Sum(g => g.Count));
    }

    [Fact]
    public void EmptySnapshot_IsHealthy()
    {
        var report = LibraryDoctorAnalyzer.Analyze(new LibraryDoctorSnapshot(), FakeLocalization.English());
        Assert.True(report.IsHealthy);
        Assert.Equal(0, report.PackagesScanned);
    }
}
