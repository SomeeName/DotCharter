using System.Text;
using DotCharter.Tone.Online.Tone3000;

namespace DotCharter.Tone.Ai;

/// <summary>
///     One neural-amp capture the designer may choose, as the model sees it.
///     <para>
///         <see cref="Id" /> is the capture's file name WITHOUT its extension,
///         lowercased — stable, unique inside one library directory, and readable
///         enough that a model asked for "a 5150 capture" can find
///         <c>5150-blockletter-boosted</c> by eye. It is deliberately NOT a path:
///         the model never names a file, it names a row of a closed list, and
///         <see cref="FileName" /> is what the app resolves that row to.
///     </para>
/// </summary>
/// <param name="Id">The catalogue id, as the model spells it.</param>
/// <param name="DisplayName">The capture's own title.</param>
/// <param name="FileName">The file name inside the NAM capture directory. This is
/// what a validated <c>namamp</c> module carries as its <c>modelPath</c>, so
/// <c>DcRigPresetPreparation.Prepare</c> resolves it to an absolute path exactly as
/// it does for a factory capture preset.</param>
/// <param name="Bundled">True for a capture that ships WITH the app (a negative
/// synthetic model id — see <see cref="BundledNamCapture.SyntheticId" />), false for
/// one the user downloaded from TONE3000.</param>
/// <param name="Author">Who captured it.</param>
/// <param name="GearType">TONE3000's own gear type (<c>amp</c>, <c>pedal</c>, …).</param>
/// <param name="Format">The model format (<c>nam</c> / <c>aidax</c>).</param>
/// <param name="SizeTier">The capture's size tier (<c>feather</c> … <c>wavenet</c>).</param>
public sealed record NamCaptureOption(
    string Id,
    string DisplayName,
    string FileName,
    bool Bundled,
    string Author,
    string GearType,
    string Format,
    string SizeTier);

/// <summary>
///     The CLOSED list of neural-amp captures a design may pick from: the captures
///     bundled with the app plus whatever the player downloaded, exactly as they sit
///     in the local <see cref="INamCaptureLibrary" /> (the bundled set is installed
///     into that same directory on first run, so one library read answers for both).
///
///     <para>
///         <b>The order is deterministic and most-stable-first</b> — bundled first,
///         then the user's own, each block sorted by id — because the rendered
///         <see cref="Block" /> sits inside the cacheable system prefix. Dictionary
///         order would miss the provider's prompt cache on every process restart,
///         which is the same reason <c>RsConverterIdiom</c> pins its ordering.
///     </para>
///     <para>
///         <b>An empty catalogue is a real answer, not a degenerate one.</b> A player
///         with no captures gets NO capture block at all and the prompt's old "avoid
///         namamp" rule instead: telling a model about a list it cannot be shown is
///         how a design comes back naming a capture nobody has.
///     </para>
///     <para>
///         Only NEURAL models are listed. An <c>ir</c>-format entry in the same
///         library is a cabinet impulse for <c>convreverb</c>/<c>cab</c>, not
///         something <c>namamp</c> can load, and offering one would produce a rig
///         that silently fails at apply time.
///     </para>
/// </summary>
public sealed class NamCaptureCatalog
{
    /// <summary>The extensions <c>namamp</c>'s loader can actually read. The
    /// extension is load-bearing everywhere in this feature — see
    /// <c>NamCaptureIndex.ExtensionFor</c>.</summary>
    private static readonly string[] NeuralExtensions = [".nam", ".aidax"];

    private readonly Dictionary<string, NamCaptureOption> _byId;
    private readonly Lazy<string> _block;

    private NamCaptureCatalog(IReadOnlyList<NamCaptureOption> options)
    {
        Options = options;
        _byId = options.ToDictionary(o => o.Id, StringComparer.OrdinalIgnoreCase);
        Fingerprint = ComputeFingerprint(options);
        _block = new Lazy<string>(() => BuildBlock(options), isThreadSafe: true);
    }

    /// <summary>No captures at all — what a build with no capture library, and a
    /// player who has deleted every capture, both get.</summary>
    public static NamCaptureCatalog Empty { get; } = new([]);

    /// <summary>Every choosable capture, bundled first and then the user's own,
    /// each block ordered by <see cref="NamCaptureOption.Id" />.</summary>
    public IReadOnlyList<NamCaptureOption> Options { get; }

    /// <summary>True when there is nothing to offer.</summary>
    public bool IsEmpty => Options.Count == 0;

    /// <summary>A stable hash of the ordered ids. It keys the cached system prompt
    /// (and the CLI provider's system-prompt FILE), so a library that gained or lost
    /// a capture gets a new prefix instead of a stale one, and an unchanged library
    /// keeps hitting the same cache entry.</summary>
    public string Fingerprint { get; }

    /// <summary>The rendered prompt block — empty when <see cref="IsEmpty" />.</summary>
    public string Block => _block.Value;

    /// <summary>The catalogue row for <paramref name="id" />, or null. Matching folds
    /// case, because a model that answers <c>5150-Blockletter</c> means the row it was
    /// shown and rejecting it teaches nothing.</summary>
    public NamCaptureOption? Find(string? id)
        => !string.IsNullOrWhiteSpace(id) && _byId.TryGetValue(id.Trim(), out var option) ? option : null;

    /// <summary>Builds a catalogue from a capture library. Throw-free: a library
    /// that cannot be read is an empty catalogue, which is the same outcome as a
    /// player who has no captures and never fails a design.</summary>
    public static NamCaptureCatalog FromLibrary(INamCaptureLibrary? library)
    {
        if (library is null) return Empty;
        try
        {
            return FromEntries(library.GetAll());
        }
        catch
        {
            return Empty;
        }
    }

    /// <summary>Builds a catalogue from already-read library entries. Pure, so the
    /// ordering and the rendering are unit-testable without a directory.</summary>
    public static NamCaptureCatalog FromEntries(IEnumerable<NamCaptureEntry>? entries)
    {
        if (entries is null) return Empty;

        var options = new List<NamCaptureOption>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            var manifest = entry?.Manifest;
            var fileName = manifest?.FileName;
            if (string.IsNullOrWhiteSpace(fileName)) continue;

            var extension = Path.GetExtension(fileName).ToLowerInvariant();
            if (!NeuralExtensions.Contains(extension)) continue;

            var id = Path.GetFileNameWithoutExtension(fileName).Trim().ToLowerInvariant();
            if (id.Length == 0 || !seen.Add(id)) continue;

            options.Add(new NamCaptureOption(
                id,
                string.IsNullOrWhiteSpace(manifest!.Title) ? id : manifest.Title.Trim(),
                fileName,
                // A bundled capture carries the negative synthetic id
                // BundledNamCapture.SyntheticId minted; TONE3000's are positive.
                manifest.ModelId < 0,
                manifest.Author?.Trim() ?? "",
                manifest.GearType?.Trim() ?? "",
                manifest.Format?.Trim() ?? extension.TrimStart('.'),
                manifest.SizeTier?.Trim() ?? ""));
        }

        if (options.Count == 0) return Empty;

        options.Sort(static (a, b) =>
        {
            // Bundled first: it is the half that is identical on every machine, so
            // it is the half of the block that can stay in a shared cache prefix.
            if (a.Bundled != b.Bundled) return a.Bundled ? -1 : 1;
            return string.CompareOrdinal(a.Id, b.Id);
        });

        return new NamCaptureCatalog(options);
    }

    private static string BuildBlock(IReadOnlyList<NamCaptureOption> options)
    {
        if (options.Count == 0) return string.Empty;

        var sb = new StringBuilder(8 * 1024);
        sb.AppendLine("# Neural amp captures");
        sb.AppendLine();
        sb.AppendLine("The namamp module PLAYS one of these captured amps. To use it, set the");
        sb.AppendLine("module's \"capture\" field to a capture id from this list, spelled exactly.");
        sb.AppendLine("A namamp with no capture, or with an id that is not listed here, is dropped.");
        sb.AppendLine("Every other module leaves \"capture\" empty.");

        var bundled = options.Where(o => o.Bundled).ToList();
        var mine = options.Where(o => !o.Bundled).ToList();

        AppendGroup(sb, "Bundled with DotCharter", bundled);
        AppendGroup(sb, "Downloaded by this player", mine);

        return sb.ToString();
    }

    private static void AppendGroup(StringBuilder sb, string heading, IReadOnlyList<NamCaptureOption> group)
    {
        if (group.Count == 0) return;

        sb.AppendLine();
        sb.Append("## ").AppendLine(heading);
        sb.AppendLine();
        foreach (var option in group)
        {
            // Id and TITLE, and the gear type only when it is not the usual "amp".
            //
            // The author and the size tier are deliberately NOT printed. They are
            // load-bearing metadata (provenance for the attribution policy, CPU cost
            // for the engine) and neither describes a SOUND, so in a block that is
            // charged by the token on every request they are 63 lines of noise: with
            // them the prompt measured ~11,750 estimated tokens against a 12,000
            // budget on the bundled set ALONE, i.e. no room for the player's own
            // downloads. The title is the amp/model tag the model needs.
            sb.Append(option.Id).Append(" — ").Append(option.DisplayName);
            if (option.GearType.Length > 0
                && !string.Equals(option.GearType, "amp", StringComparison.OrdinalIgnoreCase))
            {
                sb.Append(" (").Append(option.GearType).Append(')');
            }

            sb.AppendLine();
        }
    }

    /// <summary>FNV-1a over the ordered ids. Not a security hash — it only has to
    /// change when the list does and stay identical when it does not.</summary>
    private static string ComputeFingerprint(IReadOnlyList<NamCaptureOption> options)
    {
        if (options.Count == 0) return "none";

        var hash = 14695981039346656037UL;
        foreach (var option in options)
        {
            foreach (var b in Encoding.UTF8.GetBytes(option.Id + "" + option.FileName))
            {
                hash ^= b;
                hash *= 1099511628211UL;
            }
        }

        return hash.ToString("x16");
    }
}
