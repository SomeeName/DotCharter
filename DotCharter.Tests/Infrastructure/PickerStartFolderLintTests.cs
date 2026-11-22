using System.Text.RegularExpressions;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     Every file or folder dialog opens in the folder last used for its kind (#832). The
///     start-folder argument is the only place a dialog can be told where to open, so this
///     scan reads every call of the four <c>IFileStorageService</c> pickers and fails when one
///     leaves it out or passes a literal <c>null</c>. There is no allow-list: a new dialog
///     remembers its folder or this test is red.
/// </summary>
public sealed partial class PickerStartFolderLintTests
{
    private static readonly string[] Projects =
        ["DotCharter.UI", "DotCharter.Presentation", "DotCharter.Tone.UI", "DotCharter.Services", "DotCharter.RigLab"];

    /// <summary>Picker method -> zero-based position of its start-folder parameter.</summary>
    private static readonly Dictionary<string, int> StartArgument = new(StringComparer.Ordinal)
    {
        ["OpenFilePickerAsync"] = 2,
        ["OpenFilesPickerAsync"] = 2,
        ["OpenFolderPickerAsync"] = 1,
        ["SaveFilePickerAsync"] = 3
    };

    [GeneratedRegex(@"\b(OpenFilePickerAsync|OpenFilesPickerAsync|OpenFolderPickerAsync|SaveFilePickerAsync)\s*\(")]
    private static partial Regex PickerCall();

    [Fact]
    public void Every_picker_call_passes_a_start_folder()
    {
        var offenders = new List<string>();
        var calls = 0;
        foreach (var (file, code) in SourceFiles())
        {
            foreach (var call in Calls(code))
            {
                calls++;
                if (!HasStartFolder(call.Method, call.Arguments))
                    offenders.Add($"{Path.GetRelativePath(TestPaths.RepoRoot(), file)}: {call.Method}({Shorten(call.Arguments)})");
            }
        }

        Assert.True(calls >= 30, $"the scan found only {calls} picker calls; the call pattern or the project list drifted");
        Assert.True(offenders.Count == 0, "Picker calls without a start folder:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void Scan_recognises_a_missing_and_a_null_start_folder()
    {
        Assert.False(HasStartFolder("OpenFilePickerAsync", Split("title, filters")));
        Assert.False(HasStartFolder("OpenFilePickerAsync", Split("title, filters, null")));
        Assert.False(HasStartFolder("OpenFolderPickerAsync", Split("title")));
        Assert.False(HasStartFolder("SaveFilePickerAsync", Split("title, name, [new F(\"a\", [\"*.x\"])]")));
        Assert.True(HasStartFolder("OpenFilePickerAsync", Split("title, null, config.Dir(a, b)")));
        Assert.True(HasStartFolder("OpenFilePickerAsync", Split("title, startingDirectory: dir")));
        Assert.True(HasStartFolder("SaveFilePickerAsync", Split("t, n, [new F(\"a,b\", [\"*.x\"])], dir")));
    }

    private static bool HasStartFolder(string method, IReadOnlyList<string> args)
    {
        var named = args.FirstOrDefault(a => a.TrimStart().StartsWith("startingDirectory:", StringComparison.Ordinal));
        var value = named is not null
            ? named[(named.IndexOf(':') + 1)..].Trim()
            : args.Count > StartArgument[method] ? args[StartArgument[method]].Trim() : null;
        return !string.IsNullOrEmpty(value) && value != "null";
    }

    private static IEnumerable<(string File, string Code)> SourceFiles()
    {
        foreach (var project in Projects)
        {
            var root = Path.Combine(TestPaths.RepoRoot(), project);
            foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file);
                if (relative.StartsWith("obj", StringComparison.Ordinal) || relative.StartsWith("bin", StringComparison.Ordinal)) continue;
                yield return (file, SourceText.StripCommentLines(File.ReadAllText(file)));
            }
        }
    }

    private readonly record struct Call(string Method, IReadOnlyList<string> Arguments);

    private static IEnumerable<Call> Calls(string code)
    {
        foreach (Match m in PickerCall().Matches(code))
        {
            var lineStart = code.LastIndexOf('\n', m.Index) + 1;
            var before = code[lineStart..m.Index];
            // The Avalonia provider's own method and a picker's declaration are not call sites.
            if (before.Contains("StorageProvider.", StringComparison.Ordinal) || before.Contains("Task<", StringComparison.Ordinal)) continue;
            var open = m.Index + m.Length;
            yield return new Call(m.Groups[1].Value, Split(code, open));
        }
    }

    private static List<string> Split(string argumentText) => Split(argumentText + ")", 0);

    /// <summary>The top-level arguments of the call whose argument list starts at <paramref name="start" />.</summary>
    private static List<string> Split(string code, int start)
    {
        var args = new List<string>();
        var depth = 0;
        var current = start;
        for (var i = start; i < code.Length; i++)
        {
            var c = code[i];
            if (c == '"')
            {
                for (i++; i < code.Length && code[i] != '"'; i++)
                    if (code[i] == '\\') i++;
                continue;
            }

            if (c is '(' or '[' or '{')
            {
                depth++;
            }
            else if (c is ')' or ']' or '}')
            {
                if (depth == 0)
                {
                    if (i > current || args.Count > 0) args.Add(code[current..i]);
                    return args;
                }

                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                args.Add(code[current..i]);
                current = i + 1;
            }
        }

        return args;
    }

    private static string Shorten(IReadOnlyList<string> args)
    {
        var text = Regex.Replace(string.Join(",", args), @"\s+", " ").Trim();
        return text.Length > 110 ? text[..110] + "..." : text;
    }
}
