using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DotCharter.Tone.Ai;

namespace DotCharter.Tests.Ai.Eval;

/// <summary>
///     An <see cref="IToneDesignModel" /> that talks to a model through the FILE
///     SYSTEM instead of HTTP: it writes each <see cref="ToneDesignPrompt" /> to
///     <c>&lt;dir&gt;/&lt;index&gt;.prompt.json</c> and reads the answer back from
///     <c>&lt;dir&gt;/&lt;index&gt;.reply.json</c>.
///     <para>
///         <b>It bypasses the transport and NOTHING else.</b> The prompt it writes
///         is the one <c>ToneDesignPromptBuilder</c> built, the reply it reads goes
///         through <see cref="ToneDraftJson.ExtractJsonObject" /> exactly as the
///         Claude and OpenAI-compatible providers do, and the service, validator,
///         preparation and scorer downstream are the shipping ones. That is what
///         makes a number produced this way comparable with a number produced over
///         the wire — and it is the only way to grade a model on a box with no API
///         key and no local server, which is the case it was written for
///         (2026-09-12, grading Claude Opus 5 by hand).
///     </para>
///     <para>
///         <b>A missing reply is a per-row <see cref="ToneDesignFault.InvalidOutput" />,
///         never a crash.</b> The runner records such a row as a failure and carries
///         on, so a first "dump" pass over an empty directory writes EVERY prompt in
///         one go rather than stopping at the first unanswered one. Answer them, run
///         again, and the same run is scored.
///     </para>
/// </summary>
public sealed class TranscriptToneDesignModel : IToneDesignModel
{
    private static readonly JsonSerializerOptions PromptJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _directory;
    private readonly ToneTranscriptIndex _index;

    /// <summary>Creates the provider over <paramref name="directory" />, naming its
    /// files through <paramref name="index" />.</summary>
    /// <param name="directory">Where the prompt/reply pairs live. Created on demand.</param>
    /// <param name="index">The stable prompt→file-name map. Never call order —
    /// see <see cref="ToneTranscriptIndex" />.</param>
    public TranscriptToneDesignModel(string directory, ToneTranscriptIndex index)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
        _index = index ?? throw new ArgumentNullException(nameof(index));
    }

    /// <inheritdoc />
    /// <remarks>
    ///     <see cref="AiProviderKind.Claude" />, and honestly so: the thing on the
    ///     other end of the transcript IS Claude, reading the prompt file and
    ///     writing the reply file. The service uses this for one status string
    ///     ("Asking Claude…") and nothing else. The REPORT header does not read it —
    ///     <see cref="ToneEvalEnvironment.ToOptions" /> labels the provider
    ///     <c>transcript</c> so an artifact can never be mistaken for a wire run.
    /// </remarks>
    public AiProviderKind Kind => AiProviderKind.Claude;

    /// <summary>The file base name this prompt maps to, without an extension.</summary>
    public string FileBaseFor(ToneDesignPrompt prompt) => _index.Resolve(prompt);

    /// <inheritdoc />
    public Task<ToneDesignModelReply> CompleteAsync(ToneDesignPrompt prompt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ct.ThrowIfCancellationRequested();

        Directory.CreateDirectory(_directory);
        var name = _index.Resolve(prompt);
        var promptPath = Path.Combine(_directory, name + ".prompt.json");
        var replyPath = Path.Combine(_directory, name + ".reply.json");

        // Written on EVERY call, including the ones that then fail for a missing
        // reply: the dump pass and the scored pass are the same code path, so a
        // prompt file can never be stale relative to the run that reads it.
        File.WriteAllText(promptPath, JsonSerializer.Serialize(new TranscriptPromptFile(
            prompt.System,
            prompt.Messages.Select(m => new TranscriptTurn(m.Role, m.Text)).ToList(),
            prompt.JsonSchema,
            prompt.SchemaName), PromptJson), Encoding.UTF8);

        if (!File.Exists(replyPath))
        {
            throw new ToneDesignException(
                ToneDesignFault.InvalidOutput,
                $"No transcript reply: \"{replyPath}\" is missing. The prompt was written to " +
                $"\"{promptPath}\" — answer it there and run again.",
                replyPath);
        }

        var text = File.ReadAllText(replyPath);
        var json = ToneDraftJson.ExtractJsonObject(text) ?? text;

        // ESTIMATED, and the report says "transcript" beside it: there is no
        // provider to bill and no usage to read back, so four-characters-to-a-token
        // over what was actually sent and received is the honest answer. It keeps
        // the cost column meaningful as a SIZE, never as money spent.
        var input = ToneDesignPromptBuilder.EstimateTokens(prompt.System) +
                    prompt.Messages.Sum(m => ToneDesignPromptBuilder.EstimateTokens(m.Text)) +
                    ToneDesignPromptBuilder.EstimateTokens(prompt.JsonSchema);

        return Task.FromResult(new ToneDesignModelReply(
            json,
            new AiUsage("transcript", input, ToneDesignPromptBuilder.EstimateTokens(json))));
    }

    /// <inheritdoc />
    /// <remarks>Always Ok: the "connection" is a directory, and it is created on
    /// demand. Nothing is spent and nothing can be unreachable.</remarks>
    public Task<AiProbeResult> ProbeAsync(CancellationToken ct)
        => Task.FromResult(new AiProbeResult(true, "transcript directory: " + _directory, "transcript"));

    private sealed record TranscriptPromptFile(
        string System, IReadOnlyList<TranscriptTurn> Messages, string JsonSchema, string SchemaName);

    private sealed record TranscriptTurn(ToneDesignRole Role, string Text);
}

/// <summary>
///     The prompt→file-name map for <see cref="TranscriptToneDesignModel" />.
///     <para>
///         <b>The number is the corpus ENTRY's position, not the call's.</b> Naming
///         files in the order the provider happened to be called would renumber
///         every file the moment a run is capped differently, filtered, or run in
///         parallel — and a transcript whose numbering moves between runs is a
///         transcript nobody can answer. The map is built once from the corpus in
///         its own (SongId-sorted) order, and a prompt is matched back to it by the
///         song identity the prompt itself carries, so two runs of the same corpus
///         always produce the same file names.
///     </para>
///     <para>
///         Matching is on the trailing <c>Request:</c> / <c>Song:</c> /
///         <c>Artist:</c> lines of the last user turn — the lines
///         <c>ToneDesignPromptBuilder</c> appends last, and the only part of the
///         message that is the same in anchored and unanchored mode. A prompt that
///         matches nothing still gets a STABLE name (<c>unmatched-&lt;hash&gt;</c>,
///         over the prompt's own bytes) rather than an ordinal, because a fallback
///         that counts calls is the very failure this class exists to avoid.
///     </para>
/// </summary>
public sealed class ToneTranscriptIndex
{
    private readonly IReadOnlyDictionary<string, string> _names;

    private ToneTranscriptIndex(IReadOnlyDictionary<string, string> names) => _names = names;

    /// <summary>An index that matches nothing; every prompt gets a hashed name.</summary>
    public static ToneTranscriptIndex Empty { get; } =
        new(new Dictionary<string, string>(StringComparer.Ordinal));

    /// <summary>How many prompts this index can name by ordinal.</summary>
    public int Count => _names.Count;

    /// <summary>
    ///     Builds the map from <paramref name="entries" /> in their own order: entry
    ///     <c>i</c> owns <c>i</c> (zero-padded) for its full prompt and
    ///     <c>i-short</c> for the sparse one.
    /// </summary>
    public static ToneTranscriptIndex FromCorpus(IReadOnlyList<ToneEvalEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var ordinal = i.ToString("D2", CultureInfo.InvariantCulture);
            names[KeyOf(entry.Prompt, entry.SongTitle, entry.Artist)] = ordinal;
            names[KeyOf(entry.ShortPrompt, entry.SongTitle, entry.Artist)] = ordinal + "-short";
        }

        return new ToneTranscriptIndex(names);
    }

    /// <summary>The file base name for <paramref name="prompt" />.</summary>
    public string Resolve(ToneDesignPrompt prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        var trailing = TrailingIdentity(prompt);
        if (trailing is not null && _names.TryGetValue(trailing, out var name)) return name;

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(
            prompt.System + " " + string.Join(" ", prompt.Messages.Select(m => m.Text))));
        return "unmatched-" + Convert.ToHexString(bytes)[..8].ToLowerInvariant();
    }

    /// <summary>The identity key for a corpus entry's request.</summary>
    internal static string KeyOf(string description, string? songTitle, string? artist)
    {
        var sb = new StringBuilder();
        sb.Append("Request: ").Append(description?.Trim()).Append('\n');
        if (!string.IsNullOrWhiteSpace(songTitle)) sb.Append("Song: ").Append(songTitle.Trim()).Append('\n');
        if (!string.IsNullOrWhiteSpace(artist)) sb.Append("Artist: ").Append(artist.Trim()).Append('\n');
        return sb.ToString();
    }

    /// <summary>The <c>Request:</c>/<c>Song:</c>/<c>Artist:</c> tail of the last
    /// user turn, normalized to <c>\n</c>. Null when the turn has no such tail.</summary>
    private static string? TrailingIdentity(ToneDesignPrompt prompt)
    {
        var last = prompt.Messages.LastOrDefault(m => m.Role == ToneDesignRole.User);
        if (last is null) return null;

        var sb = new StringBuilder();
        foreach (var raw in last.Text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("Request: ", StringComparison.Ordinal) ||
                line.StartsWith("Song: ", StringComparison.Ordinal) ||
                line.StartsWith("Artist: ", StringComparison.Ordinal))
            {
                sb.Append(line).Append('\n');
            }
        }

        return sb.Length == 0 ? null : sb.ToString();
    }
}

/// <summary>
///     The <see cref="IToneDesignModelFactory" /> the eval hands the REAL
///     <c>ToneDesignService</c> when <c>DOTCHARTER_AI_PROVIDER=transcript</c>. It
///     mints one <see cref="TranscriptToneDesignModel" /> and ignores the settings'
///     key and base URL, because a transcript has neither.
/// </summary>
public sealed class TranscriptToneDesignModelFactory : IToneDesignModelFactory
{
    private readonly TranscriptToneDesignModel _model;

    /// <summary>Creates the factory over <paramref name="directory" />.</summary>
    /// <param name="directory">The transcript directory.</param>
    /// <param name="index">The stable naming map, usually
    /// <see cref="ToneTranscriptIndex.FromCorpus" />.</param>
    public TranscriptToneDesignModelFactory(string directory, ToneTranscriptIndex index)
        => _model = new TranscriptToneDesignModel(directory, index);

    /// <inheritdoc />
    public IToneDesignModel Create(AiProviderSettings settings) => _model;

    /// <inheritdoc />
    /// <remarks>The transcript answers for whichever provider the settings name —
    /// it is the transport that was replaced, not the model.</remarks>
    public bool Supports(AiProviderKind kind)
        => kind is AiProviderKind.Claude or AiProviderKind.OpenAiCompatible;
}
