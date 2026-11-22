namespace DotCharter.Tests.Core.Utilities;

/// <summary>
///     The note-letter parser behind tuning names and derived tuning offsets. Naming
///     and offset derivation each carried a copy; <see cref="TuningNameResolver.NoteToPitchClass" />
///     is now the only one, so the two can never read a letter differently.
/// </summary>
public sealed class NoteToPitchClassTests
{
    [Theory]
    [InlineData("C", 0)]
    [InlineData("b#", 0)]
    [InlineData(" c# ", 1)]
    [InlineData("DB", 1)]
    [InlineData("Eb", 3)]
    [InlineData("fb", 4)]
    [InlineData("E#", 5)]
    [InlineData("Gb", 6)]
    [InlineData("Bb", 10)]
    [InlineData("cb", 11)]
    [InlineData("H", -1)]
    [InlineData("Ebb", -1)]
    [InlineData("", -1)]
    [InlineData(null, -1)]
    public void Reads_one_letter_with_either_accidental_in_any_case(string? note, int expected) =>
        Assert.Equal(expected, TuningNameResolver.NoteToPitchClass(note));

    [Fact]
    public void Only_the_resolver_spells_the_note_letter_table()
    {
        var root = TestPaths.RepoRoot();
        var offenders = new List<string>();
        foreach (var project in Directory.EnumerateDirectories(root, "DotCharter.*"))
        {
            if (Path.GetFileName(project) == "DotCharter.Tests") continue;
            foreach (var file in Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories))
            {
                if (Path.GetFileName(file) == "TuningNameResolver.cs") continue;
                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    if (lines[i].Contains("\"C#\" or \"Db\"", StringComparison.Ordinal))
                        offenders.Add($"{Path.GetRelativePath(root, file)}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }

        Assert.True(offenders.Count == 0, LintReport.Of("a note letter is parsed by TuningNameResolver.NoteToPitchClass",
            offenders, "call DotCharter.Core.Utilities.TuningNameResolver.NoteToPitchClass"));
    }
}
