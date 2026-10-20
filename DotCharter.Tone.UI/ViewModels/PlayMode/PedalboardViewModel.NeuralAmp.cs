// PedalboardViewModel: neural-amp (namamp) capture state - the bounded async status-badge poll and the capture-manifest lookup the faceplate names a capture from.

using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.Online.Tone3000;
using DotCharter.Tone.Infrastructure.Shell;

namespace DotCharter.Tone.UI.ViewModels.PlayMode;

/// <summary>One local-library capture as a pickable row on a card's file strip
/// (and the dock's file column): the manifest title — fallback: the bare file
/// name — over the absolute path <see cref="PedalStringParamViewModel.LoadFile"/>
/// pushes.</summary>
public sealed record NamCaptureChoice(string Title, string FilePath);

public sealed partial class PedalboardViewModel
{
    /// <summary>Re-reads each neural-amp card's poll-only <c>loaded</c> /
    /// <c>rateMismatch</c> status from the service (no timer — piggybacks on the
    /// natural Sync rebuild + <see cref="ScheduleNeuralAmpStatusRefresh"/>, per the
    /// pedalboard's push-only sync model).</summary>
    public void RefreshNeuralAmpStatus()
    {
        foreach (var n in Nodes)
            n.RefreshNamStatus();
    }

    // ---- neural-amp async load: the bounded status poll -------------------------
    //
    // `SetNodeString(namamp, modelPath, …)` returns TRUE immediately and the native
    // capture load finishes a few hundred ms later, so a status read taken right
    // after the push ALWAYS sees loaded=0. With only the Sync-time read the card sat
    // on its "no model loaded" warning for the rest of the session even though the
    // amp was live — the badge lied. This is a SELF-TERMINATING burst (it cannot
    // exist while idle, unlike a standing timer): it stops the moment every neural
    // amp reports loaded, or after the budget below.

    private int _namRefreshPending;

    /// <summary>Poll cadence / budget (test seams — the real board polls ~250 ms
    /// for ~3 s, which comfortably covers a NAM/AIDA-X load).</summary>
    internal TimeSpan NamPollInterval { get; set; } = TimeSpan.FromMilliseconds(250);
    internal int NamPollAttempts { get; set; } = 12;

    /// <summary>The in-flight status poll (null when idle) — a test seam so the
    /// bounded burst can be awaited deterministically.</summary>
    internal Task? NamRefreshTask { get; private set; }

    /// <summary>Arms a bounded re-read of the neural-amp status badges after a
    /// capture was bound to a namamp node (the native load is ASYNCHRONOUS). Inert
    /// without a UI dispatcher (unit tests drive <see cref="RefreshNeuralAmpStatus"/>
    /// directly); at most one burst runs at a time.</summary>
    public void ScheduleNeuralAmpStatusRefresh()
    {
        if (_dispatcher == null) return;
        if (System.Threading.Interlocked.Exchange(ref _namRefreshPending, 1) == 1) return;

        NamRefreshTask = Task.Run(async () =>
        {
            try
            {
                for (var i = 0; i < NamPollAttempts; i++)
                {
                    await Task.Delay(NamPollInterval).ConfigureAwait(false);
                    _dispatcher.Post(RefreshNeuralAmpStatus);
                    if (AllNeuralAmpsLoaded()) break;
                }
            }
            catch
            {
                // The badge is cosmetic — a failed poll must never break the rig.
            }
            finally
            {
                System.Threading.Volatile.Write(ref _namRefreshPending, 0);
            }
        });
    }

    /// <summary>True when every neural amp in the chain reports a loaded capture
    /// (also true when the chain carries none) — the poll's stop condition. Read off
    /// the UI thread: <see cref="IDspChainService"/> is lock-guarded and throw-free.</summary>
    private bool AllNeuralAmpsLoaded()
    {
        foreach (var node in _dsp.GetNodes())
        {
            if (!string.Equals(node.ModuleTypeId, NamampModuleTypeId, StringComparison.Ordinal)) continue;
            if (_dsp.GetParam(node.NodeId, "loaded") is not 1f) return false;
        }

        return true;
    }

    /// <summary>The native neural-amp module id (mirrors
    /// <see cref="PedalNodeViewModel.IsNeuralAmp"/>'s key).</summary>
    private const string NamampModuleTypeId = "namamp";

    /// <summary>A capture was bound to a neural amp (the NAM browser's "Load into
    /// rig", or a pedal card's modelPath file picker): rebuild the board and arm the
    /// bounded status poll so the badge catches the async load.</summary>
    /// <summary>The NAM browser's attribution link: true when the browser opened.</summary>
    private static bool TryOpenUrl(IShellLauncher shell, string url)
    {
        try
        {
            shell.OpenUrl(url);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception
                                       or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void OnNeuralAmpModelChanged()
    {
        Sync();
        ScheduleNeuralAmpStatusRefresh();
    }

    /// <summary>Resolves a capture file path to its local-library manifest so the
    /// neural amp head can print the capture's real NAME and AUTHOR on its
    /// faceplate instead of a file name. Null whenever we cannot prove it: no
    /// library wired (unit tests, preset-only hosts), a capture picked from an
    /// arbitrary folder, or an unreadable library — the faceplate then falls back
    /// to the de-slugged file name, which is still a name and never a path.</summary>
    private NamCaptureManifest? LookupCaptureManifest(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        return EnsureCaptureIndex().TryGetValue(path, out var manifest) ? manifest : null;
    }

    /// <summary>Builds (once per <see cref="Sync"/> — see <see cref="_captureIndex"/>)
    /// the path→manifest index the faceplate lookup and the library picker
    /// share. Throw-free: a broken library yields an empty index, never a
    /// broken board.</summary>
    private Dictionary<string, NamCaptureManifest> EnsureCaptureIndex()
    {
        if (_captureIndex != null) return _captureIndex;

        var index = new Dictionary<string, NamCaptureManifest>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var entry in _namLibrary.GetAll())
                index[entry.FilePath] = entry.Manifest;
        }
        catch
        {
            // A broken library is a missing byline, never a broken board.
        }

        return _captureIndex = index;
    }

    /// <summary>
    ///     The local library's files that fit a file param's wildcard filter
    ///     ("*.nam;*.aidax" offers the NAM/AIDA-X captures, a cab's "*.wav"
    ///     offers only the downloaded IRs), titled from the manifest (fallback:
    ///     the bare file name) and ordered by title. Empty when no library is
    ///     wired or nothing fits — the card strip's picker then hides.
    /// </summary>
    internal IReadOnlyList<NamCaptureChoice> ListLibraryChoices(string fileFilter)
    {
        var accepted = RigDockUnit.ExtensionsFrom(fileFilter);
        if (accepted.Count == 0) return [];

        var choices = new List<NamCaptureChoice>();
        foreach (var (path, manifest) in EnsureCaptureIndex())
        {
            if (!accepted.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                continue;
            var title = manifest.Title is { Length: > 0 } t
                ? t
                : System.IO.Path.GetFileNameWithoutExtension(path);
            choices.Add(new NamCaptureChoice(title, path));
        }

        choices.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase));
        return choices;
    }
}
