using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Infrastructure;

/// <summary>
///     The "replace every invalid char with _" loop was hand-inlined at nine
///     call sites, three of them character-for-character identical inside the
///     export commands alone. <see cref="SafeFileName" /> is the one copy.
/// </summary>
public sealed class SafeFileNameTests
{
    [Fact]
    public void An_ordinary_title_is_returned_unchanged() => Assert.Equal("Master of Puppets - Lead", SafeFileName.Make("Master of Puppets - Lead"));

    [Fact]
    public void Every_invalid_character_becomes_an_underscore()
    {
        var invalid = new string(Path.GetInvalidFileNameChars());

        var made = SafeFileName.Make("a" + invalid + "b");

        Assert.Equal("a" + new string('_', invalid.Length) + "b", made);
        Assert.Equal(-1, made.IndexOfAny(Path.GetInvalidFileNameChars()));
    }

    [Theory]
    [InlineData("AC/DC", "AC_DC")]
    [InlineData("Where Is My Mind?", "Where Is My Mind_")]
    [InlineData("1/2 : 3\\4", "1_2 _ 3_4")]
    public void Real_song_titles_survive_as_recognisable_file_names(string title, string expected) => Assert.Equal(expected, SafeFileName.Make(title));

    [Fact]
    public void Surrounding_whitespace_is_trimmed() =>
        // A trailing space is legal in a .NET string and illegal at the end of a
        // Windows file name, so the pickers get a name the OS will accept.
        Assert.Equal("Song", SafeFileName.Make("  Song  "));

    [Fact]
    public void Null_and_blank_return_empty_so_the_caller_picks_its_own_fallback()
    {
        Assert.Equal(string.Empty, SafeFileName.Make(null));
        Assert.Equal(string.Empty, SafeFileName.Make("   "));
    }

    [Fact]
    public void A_title_longer_than_the_stack_buffer_still_works()
    {
        var long_ = new string('x', 700) + "?";

        var made = SafeFileName.Make(long_);

        Assert.Equal(701, made.Length);
        Assert.EndsWith("_", made, StringComparison.Ordinal);
    }

    [Fact]
    public void ReplaceInvalid_replaces_and_keeps_the_exact_spelling_otherwise()
    {
        Assert.Equal(" AC_DC - Live ", SafeFileName.ReplaceInvalid(" AC/DC - Live "));
        Assert.Equal("   ", SafeFileName.ReplaceInvalid("   "));
        Assert.Equal(string.Empty, SafeFileName.ReplaceInvalid(string.Empty));
    }

    /// <summary>
    ///     #596: package, stem-cache, lyric, new-project, EOF-import and newer-writer
    ///     backup names each carried their own replace loop beside this type. A product
    ///     file may still READ the invalid set to validate a name (IndexOfAny), but only
    ///     <see cref="SafeFileName" /> replaces with it.
    /// </summary>
    [Fact]
    public void Only_SafeFileName_replaces_invalid_file_name_characters()
    {
        var root = TestPaths.RepoRoot();
        var offenders = new List<string>();
        foreach (var project in Directory.EnumerateDirectories(root, "DotCharter.*"))
        {
            if (Path.GetFileName(project) == "DotCharter.Tests") continue;
            foreach (var file in Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || Path.GetFileName(file) == "SafeFileName.cs")
                {
                    continue;
                }

                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];
                    if (!line.Contains("GetInvalidFileNameChars()", StringComparison.Ordinal)) continue;
                    if (line.Contains("IndexOfAny(", StringComparison.Ordinal)) continue;
                    offenders.Add($"{Path.GetRelativePath(root, file)}:{i + 1}: {line.Trim()}");
                }
            }
        }

        Assert.True(offenders.Count == 0, LintReport.Of("invalid file-name characters are replaced only by SafeFileName",
            offenders, "call SafeFileName.Make (trimmed) or SafeFileName.ReplaceInvalid (exact spelling)"));
    }
}
