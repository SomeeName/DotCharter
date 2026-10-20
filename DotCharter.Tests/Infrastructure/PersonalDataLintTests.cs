using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     The repository is public (#750, #752): no tracked text file may carry the
///     owner's or his family's real names, their e-mail addresses, or a path that
///     only exists on the owner's machine (his user profile, his Rocksmith library
///     drive, his sorted media drive). Owner-only fixtures are reached through
///     environment variables instead (<see cref="LocalFixtures" />,
///     <c>DotCharter.Tests/AGENTS.md</c>); the public GitHub handle stays.
///     <para>
///         The patterns live here, and every one is assembled from pieces so
///         this file never matches itself — it is scanned like every other file.
///         The public export's leak check (<c>tools/release/PublicSnapshot.psm1</c>,
///         <c>Get-PersonalDataPatterns</c>) carries the same list for the snapshot
///         tree; <see cref="PublicSnapshotExportTests" /> fails when the two differ.
///         "Tracked" means <c>git ls-files</c>: the owner's git-ignored
///         <c>LocalOnly_*</c> probes and <c>TestData/</c> may keep real paths.
///         The submodule is its own repository and is not scanned here.
///     </para>
/// </summary>
public sealed class PersonalDataLintTests
{
    internal static readonly Regex[] Patterns =
    [
        new(Join("lu", "kas"), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(Join("stad", "ler"), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"\b" + Join("Be", "la") + @"\b", RegexOptions.CultureInvariant),
        new(Regex.Escape(Join("luk.", "stad", "ler", "@", "outlook.com")), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(Regex.Escape(Join("somee.", "name1", "@", "gmail.com")), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(Regex.Escape(Join("beci.", "music", "@", "outlook.com")), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(Join(@"\b", "F", @":[\\/]+", "Rock", "smith"), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(Join(@"\b", "D", @":[\\/]+", "Sort", "ed"), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
    ];

    [Fact]
    public void No_tracked_text_file_holds_personal_data()
    {
        var root = TestPaths.RepoRoot();
        Assert.SkipUnless(Path.Exists(Path.Combine(root, ".git")),
            "Not a git checkout (no .git) — the tracked-file list is unknowable here.");

        var tracked = TrackedFiles(root);
        // The sentinel the scan must reach: a lint that lists no files passes vacuously.
        Assert.Contains("DotCharter.Tests/Infrastructure/PersonalDataLintTests.cs", tracked);
        Assert.True(tracked.Count > 1000, $"Only {tracked.Count} tracked files listed.");

        var hits = new List<string>();
        var scanned = 0;
        foreach (var relative in tracked)
        {
            var path = Path.Combine(root, relative);
            if (!File.Exists(path)) continue; // the submodule gitlink, or a file deleted in the work tree

            var bytes = File.ReadAllBytes(path);
            if (IsBinary(bytes)) continue;

            scanned++;
            var lines = Encoding.UTF8.GetString(bytes).Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (Patterns.Any(p => p.IsMatch(lines[i])))
                    hits.Add($"{relative}:{i + 1}");
            }
        }

        Assert.True(scanned > 1000, $"Only {scanned} tracked text files scanned.");
        Assert.True(hits.Count == 0,
            "Personal data (a real name, a personal e-mail or an owner-machine path) in tracked files — " +
            "use a neutral name, a generic example path or an environment variable (see DotCharter.Tests/AGENTS.md):\n" +
            string.Join('\n', hits));
    }

    /// <summary>The probe twin: every pattern fires on its own target, and the
    /// generic examples the tree uses on purpose stay legal.</summary>
    [Fact]
    public void Every_pattern_matches_its_target_and_nothing_generic()
    {
        string[] targets =
        [
            Join("Welcome ", "Lu", "kas"),
            Join("C:", @"\Users\", "lu", "kas", @"\AppData"),
            Join("charter: ", "STAD", "LER"),
            Join("thanks ", "Be", "la", "!"),
            Join("luk.", "stad", "ler", "@", "outlook.com"),
            Join("somee.", "name1", "@", "gmail.com"),
            Join("beci.", "music", "@", "outlook.com"),
            Join("F", @":\", "Rock", @"smith\tests\rs7"),
            Join("f", ":/", "rock", "smith2014/dlc"),
            Join("D", @":\", "Sort", @"ed\01_Media")
        ];
        foreach (var target in targets)
            Assert.True(Patterns.Any(p => p.IsMatch(target)), $"No pattern fires on '{target}'.");

        // One pattern per kind of target: each must be the one that fires somewhere.
        foreach (var pattern in Patterns)
            Assert.True(targets.Any(pattern.IsMatch), $"Pattern '{pattern}' has no probe target.");

        string[] generic =
        [
            @"E:\Rocksmith\dlc", @"C:\Users\you\.local\bin\claude.exe", @"D:\Charts\Muse",
            "SomeeName/DotCharter", "Isabela", "Belarus", "bela"
        ];
        foreach (var text in generic)
            Assert.False(Patterns.Any(p => p.IsMatch(text)), $"A generic example is flagged: '{text}'.");
    }

    private static string Join(params string[] parts) => string.Concat(parts);

    internal static bool IsBinary(byte[] bytes) => bytes.AsSpan(0, Math.Min(bytes.Length, 8192)).Contains((byte)0);

    internal static List<string> TrackedFiles(string root)
    {
        var start = new ProcessStartInfo("git", "ls-files -z")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8
        };
        using var git = Process.Start(start) ?? throw new InvalidOperationException("git did not start.");
        var stderr = git.StandardError.ReadToEndAsync();
        var output = git.StandardOutput.ReadToEnd();
        git.WaitForExit();
        Assert.True(git.ExitCode == 0, $"git ls-files failed ({git.ExitCode}): {stderr.Result}");
        return [.. output.Split('\0', StringSplitOptions.RemoveEmptyEntries)];
    }
}
