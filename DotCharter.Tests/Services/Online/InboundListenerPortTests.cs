using System.Text.RegularExpressions;
using DotCharter.Tone.Online.Tone3000;

namespace DotCharter.Tests.Services.Online;

/// <summary>
///     Every inbound listener the app opens must bind inside 47810–47829.
///     <para>
///         The owner's machine carries permanent firewall allow-rules for exactly
///         that range and the consent dialog is disabled, so a listener outside it
///         is silently dropped: nothing throws, nothing logs, the connection just
///         never arrives. Since the LAN scoreboard and LAN multiplayer were removed
///         (2026-09-19, with their <c>LanPortRange</c> helper and its tests) the
///         ONE inbound listener left is the TONE3000 OAuth loopback catcher, and
///         this is its guard: a source lint over the whole service layer so a new
///         listener cannot appear unnoticed, plus the positive check on the one
///         sanctioned site.
///     </para>
/// </summary>
public sealed class InboundListenerPortTests
{
    private const int First = 47810;
    private const int Last = 47829;

    /// <summary>The one file allowed to open a listener. Everything about it is
    /// asserted positively below — an exclusion with no positive twin is how a
    /// lint goes blind.</summary>
    private const string SanctionedHttpListenerSite = "Tone3000AuthService.cs";

    private static bool Covers(int port) => port is >= First and <= Last;

    [Fact]
    public void NoOtherFileOpensAnInboundListener_OrBindsAnEphemeralPort()
    {
        var root = RepoPath("DotCharter.Services");
        // The TONE3000 client (the sanctioned listener) moved to DotCharter.Tone
        // with the rest of the rig; both projects are one service layer here.
        var files = new[] { root, RepoPath("DotCharter.Tone") }
            .SelectMany(r => Directory.EnumerateFiles(r, "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                            StringComparison.Ordinal) &&
                        !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                            StringComparison.Ordinal))
            .ToList();
        // Anti-vacuity: the service layer is hundreds of files; a scan that finds
        // a handful has lost the tree.
        Assert.True(files.Count > 200, $"only {files.Count} service files scanned — the walk lost the tree");
        Assert.Contains(files, f => Path.GetFileName(f) == SanctionedHttpListenerSite);

        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            var src = StripLineComments(File.ReadAllText(file));

            Assert.False(src.Contains("new TcpListener(", StringComparison.Ordinal),
                $"{name}: opens a TcpListener. Ports outside {First}-{Last} are silently dropped by the " +
                "firewall; a new inbound listener needs a fixed in-range port and its own guard here.");

            if (name != SanctionedHttpListenerSite)
            {
                Assert.False(src.Contains("new HttpListener(", StringComparison.Ordinal),
                    $"{name}: opens an HttpListener. The only sanctioned one is {SanctionedHttpListenerSite} " +
                    "(the OAuth loopback catcher, on Tone3000OAuth.CallbackPort).");
            }

            foreach (Match m in Regex.Matches(src, @"https?://(?:localhost|127\.0\.0\.1|\+|\*):(?<p>\d+)"))
            {
                var port = int.Parse(m.Groups["p"].Value);
                Assert.True(Covers(port),
                    $"{name}: a loopback listen prefix names port {port}, outside {First}-{Last}.");
            }

            foreach (Match m in Regex.Matches(src, @"\.Bind\(\s*new IPEndPoint\(([^()]*)\)"))
            {
                Assert.False(m.Groups[1].Value.Split(',').Last().Trim() == "0",
                    $"{name}: a socket is bound to port 0, outside the firewall allow-range.");
            }
        }
    }

    /// <summary>
    ///     The positive twin of the <c>HttpListener</c> ban: the one sanctioned
    ///     site really opens one, it takes its prefix from the CONSTANT rather
    ///     than a literal, and the constant's port is in range. <c>Tone3000OAuth</c>
    ///     carries the port twice — <c>CallbackPort</c> as an int and
    ///     <c>RedirectUri</c> as a whole URL — so the two are asserted equal
    ///     rather than trusted.
    /// </summary>
    [Fact]
    public void TheOAuthLoopbackCatcherListensInsideTheAllowRange()
    {
        var src = StripLineComments(
            File.ReadAllText(RepoPath("DotCharter.Tone/Online/Tone3000/Tone3000AuthService.cs")));

        Assert.Contains("new HttpListener(", src, StringComparison.Ordinal);
        Assert.Contains("Tone3000OAuth.RedirectUri", src, StringComparison.Ordinal);

        var uri = new Uri(Tone3000OAuth.RedirectUri);
        Assert.Equal(Tone3000OAuth.CallbackPort, uri.Port);
        Assert.True(Covers(uri.Port),
            $"the TONE3000 callback listens on {uri.Port}, outside {First}-{Last}. The firewall drops it " +
            "without a dialog, and sign-in reads as broken rather than blocked.");
        Assert.True(uri.IsLoopback, $"{uri} is not a loopback prefix — HTTP.SYS would need a URL ACL.");
    }

    private static string StripLineComments(string src)
    {
        var sb = new System.Text.StringBuilder(src.Length);
        foreach (var line in src.Split('\n'))
        {
            var idx = line.IndexOf("//", StringComparison.Ordinal);
            sb.Append(idx >= 0 ? line[..idx] : line).Append('\n');
        }

        return sb.ToString();
    }

    private static string RepoPath(string relative) =>
        Path.Combine(TestPaths.RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar));
}
