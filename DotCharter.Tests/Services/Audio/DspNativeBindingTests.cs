using System.Text.RegularExpressions;
using DotCharter.Tone.Audio.Dsp;

namespace DotCharter.Tests.Services.Audio;

/// <summary>
///     <b>The app must not bind the native audio ABI itself.</b>
///     <para>
///         This replaces <c>DspNativeMarshallingTests</c>, and it is the general
///         form of what that lint was patching. Until JuceSharp 0.8.x the wrapper
///         declared none of the ~30 <c>dca_engine_dsp_*</c> exports, so
///         <c>DotCharter.Services/Audio/Dsp/DspNativeMethods.cs</c> was a SECOND,
///         independent P/Invoke set to the same entry points — and it inherited
///         none of the wrapper's guarantees. Two of them cost real bugs:
///     </para>
///     <list type="bullet">
///         <item>
///             Six declarations were still <c>LPStr</c> (the SYSTEM CODEPAGE)
///             after the wrapper's last one had been converted to UTF-8 — the
///             rule was enforced on the copy that was not on this call path.
///             Nothing had broken only because module and param ids are ASCII,
///             which is byte-identical in both.
///         </item>
///         <item>
///             Every call went through <c>JuceAudioEngine.NativeHandle</c>, an
///             UNPINNED <c>dca_engine*</c>. That is the entire reason
///             <c>JuceDspChainService</c> has to hold its gate across the detach:
///             nothing else stood between a pedalboard knob drag and a device
///             change freeing the engine underneath it.
///         </item>
///     </list>
///     <para>
///         Deleting the file fixes both permanently — but only for as long as
///         nobody adds the next one. A missing export is genuinely easier to
///         P/Invoke than to add to the submodule, which is exactly how the first
///         copy appeared, so the ban is linted rather than written down.
///     </para>
/// </summary>
public sealed class DspNativeBindingTests
{
    private static readonly string[] ScannedProjects = ["DotCharter.Services", "DotCharter.Tone", "DotCharter.UI"];

    /// <summary>
    /// No product code outside the submodule declares a P/Invoke against the
    /// native audio library, under any of the three attribute spellings.
    /// </summary>
    [Fact]
    public void NoProductCodeDeclaresItsOwnPInvokeAgainstTheAudioLibrary()
    {
        var scanned = 0;
        var offenders = new List<string>();

        foreach (var file in ProductSources())
        {
            scanned++;
            var text = StripLineComments(File.ReadAllText(file));

            foreach (Match m in Regex.Matches(text, @"\[\s*(DllImport|LibraryImport)\s*\(([^)]*)\)"))
            {
                var args = m.Groups[2].Value;
                // Only the audio library is banned — an app that P/Invokes
                // kernel32 for its own shutdown path is not the failure mode
                // this lint exists for.
                if (args.Contains("juce_sharp", StringComparison.OrdinalIgnoreCase)
                    || args.Contains("dca_", StringComparison.Ordinal))
                {
                    offenders.Add($"{Rel(file)}: {m.Value.Trim()}");
                }
            }

            // The entry-point name is the real tell, and it survives whatever
            // the library constant is called.
            foreach (Match m in Regex.Matches(text, @"""dca_[a-z0-9_]+"""))
                offenders.Add($"{Rel(file)}: entry point {m.Value}");
        }

        // Non-vacuity floor: a scan that stops finding files passes silently.
        Assert.True(scanned >= 200,
            $"Only {scanned} product source files were scanned — the scan is broken, not the code.");

        Assert.True(offenders.Count == 0,
            "The native audio ABI is bound by JuceSharp and by nothing else. A second binding gets "
            + "neither the wrapper's UTF-8 marshalling lint nor its handle pinning — both of which "
            + "the last copy was missing. Add the export to the submodule instead:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// ...and the tone rig is actually driven THROUGH the wrapper. A ban with
    /// nothing on the other side of it would also pass if the rig had simply
    /// stopped working.
    /// </summary>
    [Fact]
    public void TheToneRigIsDrivenThroughTheJuceSharpWrapper()
    {
        var src = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(),
            "DotCharter.Tone", "Audio", "Dsp", "DspNativeInterop.cs"));

        foreach (var expected in new[]
                 {
                     "GetDspChain(Player)",       // the pinned, seat-scoped chain
                     "chain.AddNodeBypassed(",    // ABI v32 parked insertion
                     "chain.ProcessOffline(",     // the take re-amp path
                     "JuceDspModuleCatalog.Find(",// the static catalogue
                 })
        {
            Assert.Contains(expected, src, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// <see cref="DspPlayers"/> mirrors two ABI v28 constants as <c>const</c>
    /// (they cannot forward to <c>JuceAudioEngine</c>'s static properties). A
    /// seat count that disagrees with the engine's hands out node ids naming a
    /// seat the engine does not have — which the ABI REJECTS rather than
    /// clamps, so the whole seat would silently go quiet.
    /// </summary>
    [Fact]
    public void DspPlayersMirrorsTheEnginesSeatConstants()
    {
        Assert.Equal(DspPlayers.MaxPlayers, JuceSharp.JuceAudioEngine.MaxPlayers);
        Assert.Equal(DspPlayers.NodeIdStride, JuceSharp.JuceAudioEngine.PlayerNodeIdStride);

        // And the id arithmetic both sides use is the same arithmetic.
        for (var seat = 0; seat < DspPlayers.MaxPlayers; seat++)
            Assert.Equal(seat, DspPlayers.PlayerOfNodeId(seat * DspPlayers.NodeIdStride + 7));
    }

    // --------------------------------------------------------------- helpers

    private static IEnumerable<string> ProductSources()
    {
        foreach (var project in ScannedProjects)
        {
            var root = Path.Combine(TestPaths.RepoRoot(), project);
            if (!Directory.Exists(root)) continue;
            foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                var rel = Rel(file);
                if (rel.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                    || rel.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                {
                    continue;
                }

                yield return file;
            }
        }
    }

    /// <summary>A commented-out declaration is exactly as inert as a
    /// <c>see cref</c>, and this file's own prose names the entry points.</summary>
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

    private static string Rel(string file) => Path.GetRelativePath(TestPaths.RepoRoot(), file);
}
