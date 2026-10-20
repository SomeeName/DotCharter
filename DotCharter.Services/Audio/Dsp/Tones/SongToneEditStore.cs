using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotCharter.Services.Audio.ChartSynth;
using DotCharter.Tone.Audio.Dsp.Presets;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Services.Library;

namespace DotCharter.Services.Audio.Dsp.Tones;

/// <summary>Why a save or a reset of a song-tone edit did or did not land.</summary>
public enum SongToneEditWriteOutcome
{
    /// <summary>The song's edits file was written (or deleted with its last edit).</summary>
    Written,

    /// <summary>Nothing to do: a reset of a tone that had no edit, or a
    /// <see cref="SongToneEditStore.MoveKeys" /> with nothing under its keys.</summary>
    NothingToRemove,

    /// <summary>The existing file could not be READ (a lock, an AV hold, a
    /// permission). It is presumed intact and was not written over.</summary>
    Failed
}

/// <summary>The outcome of <see cref="SongToneEditStore.Save" /> /
/// <see cref="SongToneEditStore.Remove" />.</summary>
public sealed record SongToneEditWriteResult(SongToneEditWriteOutcome Outcome, string? Detail = null)
{
    public bool Ok => Outcome is SongToneEditWriteOutcome.Written or SongToneEditWriteOutcome.NothingToRemove;
}

/// <summary>
///     The player's edits of a song's in-game tones: one JSON file per song under
///     <c>&lt;config root&gt;/song-tones/</c>, holding the edited rig per tone key in
///     the shareable tone-file shape (<see cref="TonePresetJsonFile" />).
///     <para>
///         <b>Never inside the song's package.</b> The rig does not map back onto
///         Rocksmith's gear list, and a package can be SHARED by hundreds of
///         songs and must never be rewritten — so an edit is DotCharter's data, keyed by the song's
///         identity (<c>PlaySongItem.SongIdentity</c>, e.g. <c>lib:CallMe</c>),
///         and the package stays exactly as the game shipped it.
///     </para>
///     <para>
///         <b>A library song's file saved under the old identity</b> is named after its
///         <c>psarc:</c> identity. The first access under the <c>lib:</c> identity
///         rewrites it under the new name and only then deletes the old file
///         (<see cref="MigrateLegacyLocked" />); a file already under the new name wins.
///     </para>
///     <para>
///         <b>The three read outcomes are kept apart</b> (config-and-json-stores):
///         an ABSENT file is no edits; a CORRUPT one reads as no edits and is moved
///         aside as <c>&lt;name&gt;.corrupt-&lt;stamp&gt;.json</c> before a save
///         replaces it; an UNREADABLE one is presumed intact and a save refuses to
///         write over it. Writes are atomic (<see cref="AtomicFile" />).
///     </para>
///     <para>
///         <b>A tone may also carry its own chart-synth INSTRUMENT</b>
///         (<see cref="ChartSynthToneOverride" />) under <c>"instruments"</c>, keyed
///         like the edits — independent of them: a converted tone can say "neck
///         pickup" without being edited, and resetting an edit keeps it. A file
///         that predates it has none.
///     </para>
///     Never throws.
/// </summary>
public sealed class SongToneEditStore
{
    /// <summary>The folder under the config root. Listed in the backup allow-list
    /// (<c>ConfigBackupRules</c>), which the update payload filter derives from.</summary>
    public const string FolderName = "song-tones";

    private const int FormatVersion = 1;

    private static readonly Lazy<SongToneEditStore> LazyDefault =
        new(() => new SongToneEditStore(Path.Combine(WritablePaths.ConfigRoot, FolderName)));

    private static readonly JsonDocumentOptions ReadOptions = new() { AllowTrailingCommas = true };

    private readonly string _directory;
    private readonly object _gate = new();

    public SongToneEditStore(string directory) => _directory = directory;

    /// <summary>The store under the app's config root.</summary>
    public static SongToneEditStore Default => LazyDefault.Value;

    /// <summary>The edited rigs of <paramref name="songIdentity" />, by tone key
    /// (case-insensitive). Empty when the song has none, or its file cannot be read.</summary>
    public IReadOnlyDictionary<string, DcRigPreset> Read(string songIdentity)
    {
        var result = new Dictionary<string, DcRigPreset>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(songIdentity)) return result;
        songIdentity = LibrarySongIdentity.Normalize(songIdentity);
        lock (_gate)
        {
            if (MigrateLegacyLocked(songIdentity) != null) return result;
            if (TryLoad(PathFor(songIdentity), out var tones, out _, out _) is not LoadState.Loaded) return result;
            foreach (var (key, tone) in tones) result[key] = tone.Rig;
        }

        return result;
    }

    /// <summary>Saves <paramref name="rig" /> as the edit of <paramref name="toneKey" />
    /// for this song, replacing any earlier edit of that tone.</summary>
    public SongToneEditWriteResult Save(string songIdentity, string toneKey, string toneName, DcRigPreset rig)
    {
        ArgumentNullException.ThrowIfNull(rig);
        var key = toneKey.Trim();
        return Rewrite(songIdentity, (tones, _) =>
        {
            tones[key] = new DspTonePreset(key, toneName, DspToneCategory.Clean, string.Empty, rig, DspToneOrigin.User);
            return true;
        });
    }

    /// <summary>The chart-synth instrument overrides of <paramref name="songIdentity" />'s
    /// tones, by tone key (case-insensitive). Empty when it has none, or its
    /// file cannot be read.</summary>
    public IReadOnlyDictionary<string, ChartSynthToneOverride> ReadInstruments(string songIdentity)
    {
        var result = new Dictionary<string, ChartSynthToneOverride>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(songIdentity)) return result;
        songIdentity = LibrarySongIdentity.Normalize(songIdentity);
        lock (_gate)
        {
            if (MigrateLegacyLocked(songIdentity) != null) return result;
            if (TryLoad(PathFor(songIdentity), out _, out var instruments, out _) is not LoadState.Loaded) return result;
            foreach (var (key, value) in instruments) result[key] = value;
        }

        return result;
    }

    /// <summary>Saves <paramref name="value" /> as <paramref name="toneKey" />'s own
    /// instrument, or drops it when null — the tone follows the player profile again.</summary>
    public SongToneEditWriteResult SaveInstrument(string songIdentity, string toneKey, ChartSynthToneOverride? value)
    {
        var key = (toneKey ?? string.Empty).Trim();
        if (key.Length == 0) return new SongToneEditWriteResult(SongToneEditWriteOutcome.Failed, "no tone");
        return Rewrite(songIdentity, (_, instruments) =>
        {
            if (value == null) return instruments.Remove(key);
            instruments[key] = value;
            return true;
        });
    }

    /// <summary>Drops the edit of <paramref name="toneKey" />, so the tone converts
    /// from its gear again. The file goes with the song's last edit.</summary>
    public SongToneEditWriteResult Remove(string songIdentity, string toneKey) =>
        Rewrite(songIdentity, (tones, _) => tones.Remove(toneKey.Trim()));

    /// <summary>
    ///     Moves an OWN chart's edits and instruments with its tone keys: the entries of
    ///     <paramref name="fromIdentity" />, each key in <paramref name="keyMoves" /> (saved key →
    ///     key now) renamed — all at once, so a swap is an exchange — land under
    ///     <paramref name="toIdentity" />.
    ///     <list type="bullet">
    ///         <item>The same identity (a save): the song's file is rewritten in place. A key that
    ///         had nothing keeps whatever its new key already held.</item>
    ///         <item>Another identity (a Save As): a COPY, merged into the new song's file, the copied
    ///         entries winning. The old song's file is never touched — its chart is still on disk
    ///         with its old keys.</item>
    ///     </list>
    ///     <b>A library song (<c>lib:</c>) is never written</b>: its package keys never change.
    /// </summary>
    public SongToneEditWriteResult MoveKeys(string fromIdentity, string toIdentity, IReadOnlyDictionary<string, string> keyMoves)
    {
        ArgumentNullException.ThrowIfNull(keyMoves);
        if (string.IsNullOrWhiteSpace(fromIdentity) || string.IsNullOrWhiteSpace(toIdentity))
            return new SongToneEditWriteResult(SongToneEditWriteOutcome.Failed, "no song");
        if (IsLibraryIdentity(toIdentity))
            return new SongToneEditWriteResult(SongToneEditWriteOutcome.Failed, "a library song's tone keys never move");

        var moves = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (from, to) in keyMoves)
        {
            if (from.Trim().Length > 0 && to.Trim().Length > 0) moves[from.Trim()] = to.Trim();
        }

        lock (_gate)
        {
            if (string.Equals(fromIdentity, toIdentity, StringComparison.Ordinal))
            {
                if (moves.Count == 0) return new SongToneEditWriteResult(SongToneEditWriteOutcome.NothingToRemove);
                return Rewrite(toIdentity, (tones, instruments) =>
                {
                    var movedTones = MoveInPlace(tones, moves, Rekeyed);
                    var movedInstruments = MoveInPlace(instruments, moves, (value, _) => value);
                    return movedTones || movedInstruments;
                });
            }

            switch (TryLoad(PathFor(fromIdentity), out var sourceTones, out var sourceInstruments, out var error))
            {
                case LoadState.Unreadable:
                    return new SongToneEditWriteResult(SongToneEditWriteOutcome.Failed, error);
                case LoadState.Absent or LoadState.Corrupt:
                    return new SongToneEditWriteResult(SongToneEditWriteOutcome.NothingToRemove);
            }

            if (sourceTones.Count == 0 && sourceInstruments.Count == 0)
                return new SongToneEditWriteResult(SongToneEditWriteOutcome.NothingToRemove);
            return Rewrite(toIdentity, (tones, instruments) =>
            {
                foreach (var (key, tone) in sourceTones)
                {
                    var target = moves.GetValueOrDefault(key, key);
                    tones[target] = Rekeyed(tone, target);
                }

                foreach (var (key, value) in sourceInstruments) instruments[moves.GetValueOrDefault(key, key)] = value;
                return true;
            });
        }
    }

    /// <summary>A song from the player's library (<c>PlaySongItem.SongIdentity</c> = <c>lib:&lt;key&gt;</c>,
    /// or the <c>psarc:&lt;key&gt;</c> earlier builds used).</summary>
    public static bool IsLibraryIdentity(string? songIdentity) => LibrarySongIdentity.IsLibrary(songIdentity);

    private static DspTonePreset Rekeyed(DspTonePreset tone, string key) => tone with { Id = key };

    /// <summary>Every moved key's entry, taken out first and put back under its new key — so
    /// <c>a → b, b → a</c> exchanges them. True when anything moved.</summary>
    private static bool MoveInPlace<T>(Dictionary<string, T> entries, IReadOnlyDictionary<string, string> moves,
        Func<T, string, T> rekey)
    {
        var taken = new List<(string Key, T Value)>();
        foreach (var (from, to) in moves)
        {
            if (entries.Remove(from, out var value)) taken.Add((to, value));
        }

        foreach (var (key, value) in taken) entries[key] = rekey(value, key);
        return taken.Count > 0;
    }

    /// <summary>The song's file: a readable stem plus a short hash of the full
    /// identity, so two identities that sanitize alike never share a file.</summary>
    internal string PathFor(string songIdentity)
    {
        var stem = SafeFileName.Make(songIdentity.Replace(':', '_'));
        if (stem.Length > 80) stem = stem[..80];
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(songIdentity)))[..10].ToLowerInvariant();
        return Path.Combine(_directory, $"{stem}-{hash}.json");
    }

    private SongToneEditWriteResult Rewrite(
        string songIdentity,
        Func<Dictionary<string, DspTonePreset>, Dictionary<string, ChartSynthToneOverride>, bool> edit)
    {
        if (string.IsNullOrWhiteSpace(songIdentity))
            return new SongToneEditWriteResult(SongToneEditWriteOutcome.Failed, "no song");
        songIdentity = LibrarySongIdentity.Normalize(songIdentity);

        lock (_gate)
        {
            if (MigrateLegacyLocked(songIdentity) is { } migrationError)
                return new SongToneEditWriteResult(SongToneEditWriteOutcome.Failed, migrationError);
            var path = PathFor(songIdentity);
            switch (TryLoad(path, out var tones, out var instruments, out var error))
            {
                case LoadState.Unreadable:
                    return new SongToneEditWriteResult(SongToneEditWriteOutcome.Failed, error);
                case LoadState.Corrupt:
                    if (!TryQuarantine(path, out error))
                        return new SongToneEditWriteResult(SongToneEditWriteOutcome.Failed, error);
                    tones.Clear();
                    instruments.Clear();
                    break;
            }

            if (!edit(tones, instruments)) return new SongToneEditWriteResult(SongToneEditWriteOutcome.NothingToRemove);

            try
            {
                if (tones.Count == 0 && instruments.Count == 0)
                {
                    File.Delete(path);
                }
                else
                {
                    Directory.CreateDirectory(_directory);
                    AtomicFile.WriteAllText(path, Serialize(songIdentity, tones, instruments));
                }

                return new SongToneEditWriteResult(SongToneEditWriteOutcome.Written);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new SongToneEditWriteResult(SongToneEditWriteOutcome.Failed, ex.Message);
            }
        }
    }

    private enum LoadState { Absent, Loaded, Corrupt, Unreadable }

    /// <summary>
    ///     Moves a library song's file under the old identity (named after its <c>psarc:</c> identity)
    ///     to its <c>lib:</c> name. The new file is WRITTEN first and the old one deleted
    ///     only after that succeeded; a file already under the new name wins and the old
    ///     one is dropped. A corrupt old file is quarantined like any other. Returns null
    ///     when the song's file may be used, or why it may not: an old file that could
    ///     not be read (or the new one not written) is presumed intact, so nothing may
    ///     be written under the new name over the edits it still holds. Idempotent.
    /// </summary>
    private string? MigrateLegacyLocked(string songIdentity)
    {
        if (LibrarySongIdentity.LegacyFormOf(songIdentity) is not { } legacyIdentity) return null;
        var legacyPath = PathFor(legacyIdentity);
        if (!File.Exists(legacyPath)) return null;

        var path = PathFor(songIdentity);
        try
        {
            if (!File.Exists(path))
            {
                switch (TryLoad(legacyPath, out var tones, out var instruments, out var error))
                {
                    case LoadState.Unreadable:
                        return error ?? "the song's earlier tone edits could not be read";
                    case LoadState.Corrupt:
                        return TryQuarantine(legacyPath, out error) ? null : error;
                    case LoadState.Absent:
                        return null;
                }

                Directory.CreateDirectory(_directory);
                AtomicFile.WriteAllText(path, Serialize(songIdentity, tones, instruments));
            }

            File.Delete(legacyPath);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    private static LoadState TryLoad(
        string path,
        out Dictionary<string, DspTonePreset> tones,
        out Dictionary<string, ChartSynthToneOverride> instruments,
        out string? error)
    {
        tones = new Dictionary<string, DspTonePreset>(StringComparer.OrdinalIgnoreCase);
        instruments = new Dictionary<string, ChartSynthToneOverride>(StringComparer.OrdinalIgnoreCase);
        error = null;
        string json;
        try
        {
            if (!File.Exists(path)) return LoadState.Absent;
            json = AtomicFile.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return LoadState.Unreadable;
        }

        try
        {
            using var document = JsonDocument.Parse(json, ReadOptions);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("tones", out var body) ||
                body.ValueKind != JsonValueKind.Object)
            {
                return LoadState.Corrupt;
            }

            // Each tone goes through the defensive tone-file parser: an unknown
            // module or a bad value costs that one tone, never the song's set.
            foreach (var tone in body.EnumerateObject())
            {
                var key = tone.Name.Trim();
                if (key.Length == 0) continue;
                if (TonePresetJsonFile.Parse(tone.Value.GetRawText()).Preset is { } preset) tones[key] = preset;
            }

            // The instrument overrides are optional: a file saved without them has none.
            if (document.RootElement.TryGetProperty("instruments", out var overrides) &&
                overrides.ValueKind == JsonValueKind.Object)
            {
                foreach (var entry in overrides.EnumerateObject())
                {
                    var key = entry.Name.Trim();
                    if (key.Length == 0) continue;
                    if (ChartSynthInstrumentJson.ReadOverride(entry.Value) is { } value) instruments[key] = value;
                }
            }

            return LoadState.Loaded;
        }
        catch (JsonException)
        {
            return LoadState.Corrupt;
        }
    }

    private static bool TryQuarantine(string path, out string? error)
    {
        error = null;
        var aside = Path.Combine(Path.GetDirectoryName(path) ?? string.Empty,
            $"{Path.GetFileNameWithoutExtension(path)}.corrupt-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}.json");
        try
        {
            File.Move(path, aside, true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string Serialize(
        string songIdentity,
        IReadOnlyDictionary<string, DspTonePreset> tones,
        IReadOnlyDictionary<string, ChartSynthToneOverride> instruments)
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", FormatVersion);
            writer.WriteString("song", songIdentity);
            writer.WriteStartObject("tones");
            foreach (var (key, tone) in tones.OrderBy(t => t.Key, StringComparer.OrdinalIgnoreCase))
            {
                writer.WritePropertyName(key);
                using var body = JsonDocument.Parse(TonePresetJsonFile.Serialize(tone));
                body.RootElement.WriteTo(writer);
            }

            writer.WriteEndObject();
            if (instruments.Count > 0)
            {
                writer.WriteStartObject("instruments");
                foreach (var (key, value) in instruments.OrderBy(t => t.Key, StringComparer.OrdinalIgnoreCase))
                    ChartSynthInstrumentJson.WriteOverride(writer, key, value);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
