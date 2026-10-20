using System.Text.RegularExpressions;

namespace DotCharter.Tests.UI;

/// <summary>
///     A XAML <c>CommandParameter="1"</c> is a <b>string</b>. Binding one to a
///     <c>[RelayCommand]</c> whose method takes an <c>int</c> makes
///     CommunityToolkit generate a <c>RelayCommand&lt;int&gt;</c> whose
///     <c>CanExecute</c> throws <see cref="ArgumentException" /> the moment the
///     Button attaches to the logical tree — an unhandled crash during a layout
///     pass, which takes the whole application down.
///     <para>
///         This shipped once (the inspector's fret/string/sustain steppers) and is
///         invisible to every other guard: it builds clean, the view CONSTRUCTS
///         clean, and it only fires when the button is realised — which for a
///         control inside a collapsed disclosure may be long after startup. A
///         source lint is the only cheap check that covers every site.
///     </para>
///     <para>
///         The fix is to take a <c>string?</c> and parse, which is what
///         <c>SheetMusicPaneViewModel.ApplyDurationValue</c> has always done.
///     </para>
/// </summary>
public sealed class CommandParameterTypeTests
{
    /// <summary>Value types a XAML literal can never satisfy.</summary>
    private static readonly string[] ForbiddenParameterTypes =
        ["int", "double", "bool", "long", "float", "decimal", "short", "byte"];

    [Fact]
    public void NoLiteralCommandParameterFeedsAValueTypedCommand()
    {
        var root = TestPaths.RepoRoot();
        var uiRoot = Path.Combine(root, "DotCharter.UI");

        var commandsWithLiteralParameters = new HashSet<string>(StringComparer.Ordinal);
        foreach (var axaml in UiSourceTree.EnumerateFiles(uiRoot, "*.axaml", SearchOption.AllDirectories))
        {
            var markup = UiSourceTree.ReadAllText(axaml);
            // Command="{Binding FooCommand}" ... CommandParameter="literal"
            const string pattern =
                "Command=\"\\{Binding\\s+(?<name>\\w+)Command\\}\"[^>]*?CommandParameter=\"(?<value>[^\"{]+)\"";
            foreach (Match match in Regex.Matches(markup, pattern, RegexOptions.Singleline))
                commandsWithLiteralParameters.Add(match.Groups["name"].Value);
        }

        Assert.NotEmpty(commandsWithLiteralParameters); // the lint must not pass by finding nothing

        var offenders = new List<string>();
        foreach (var source in UiSourceTree.EnumerateFiles(uiRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (source.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            var code = UiSourceTree.ReadAllText(source);

            foreach (var command in commandsWithLiteralParameters)
            {
                foreach (var type in ForbiddenParameterTypes)
                {
                    // [RelayCommand] ... private void Foo(int x)
                    var pattern = $@"\[RelayCommand[^\]]*\][\s\S]{{0,400}}?\b{Regex.Escape(command)}\s*\(\s*{type}[\s?]";
                    if (Regex.IsMatch(code, pattern))
                    {
                        offenders.Add($"{Path.GetFileName(source)}: {command}({type}) is bound to a literal " +
                                  "CommandParameter — take a string? and parse instead");
                    }
                }
            }
        }

        Assert.True(offenders.Count == 0, string.Join("\n", offenders));
    }
}
