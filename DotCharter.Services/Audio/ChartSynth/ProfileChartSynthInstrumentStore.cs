using System.Text;
using System.Text.Json;
using DotCharter.Tone.Audio.ChartSynth;
using DotCharter.Tone.Audio.Dsp;

namespace DotCharter.Services.Audio.ChartSynth;

/// <summary>How a profile's instrument file read.</summary>
public enum ProfileInstrumentReadState
{
    /// <summary>No file: the profile has never chosen (defaults, or the one-time move of the old app-wide choice).</summary>
    Absent,

    /// <summary>Read; a family whose entry was missing or nonsense took its default.</summary>
    Loaded,

    /// <summary>Not an instrument file; served as defaults, quarantined by the next save.</summary>
    Corrupt,

    /// <summary>Could not be READ (a lock, an AV hold, a permission): presumed intact — defaults are
    /// served, the caller must not cache them, and a save refuses to write over it.</summary>
    Unreadable,
}

/// <summary>What <see cref="ProfileChartSynthInstrumentStore.Read" /> returns.</summary>
public sealed record ProfileInstrumentRead(
    ProfileInstrumentReadState State, ChartSynthInstrument Guitar, ChartSynthInstrument Bass)
{
    /// <summary>The family's instrument.</summary>
    public ChartSynthInstrument For(bool bass) => bass ? Bass : Guitar;
}

/// <summary>
/// The chart synth's instrument per PLAYER PROFILE: one
///     <c>chart-synth-instrument.json</c> under <c>profiles/&lt;id&gt;/</c>, holding
///     that player's guitar and bass (model, pickup, coil split).
///     <para>
///         <b>A file in the profile directory</b>, like the tone rig
///         (<c>ProfileToneRigStore</c>): the existing directory rules already keep
///         it out of LAN update packages and in whole-install backups, a profile
///         export carries it, and deleting a profile takes it along.
///     </para>
///     <para>
///         <b>The three read outcomes are kept apart</b> (config-and-json-stores.md):
///         ABSENT is no choice yet; CORRUPT reads as the defaults and is moved aside
///         as <c>&lt;name&gt;.corrupt-&lt;stamp&gt;.json</c> before a save replaces
///         it; UNREADABLE is presumed intact and a save refuses to write over it.
///         Writes are atomic (<see cref="AtomicFile" />). Nothing is cached here —
///         <see cref="ChartSynthInstrumentCoordinator" /> holds the active profile's
///         choice and re-reads after an unreadable result. Never writes for the
///         registry's degraded stand-in profile. Never throws.
///     </para>
/// </summary>
public sealed class ProfileChartSynthInstrumentStore
{
    /// <summary>The file in each profile's data directory.</summary>
    public const string FileName = "chart-synth-instrument.json";

    private const int FormatVersion = 1;

    private static readonly JsonDocumentOptions ReadOptions = new() { AllowTrailingCommas = true };

    private readonly IToneRigProfileScope _profiles;
    private readonly object _gate = new();

    public ProfileChartSynthInstrumentStore(IToneRigProfileScope profiles)
        => _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));

    /// <summary>The profile's instruments; the defaults for any outcome but <see cref="ProfileInstrumentReadState.Loaded" />.</summary>
    public ProfileInstrumentRead Read(Guid profileId)
    {
        lock (_gate)
        {
            var path = PathFor(profileId, out _);
            if (path == null)
            {
                return new ProfileInstrumentRead(ProfileInstrumentReadState.Unreadable,
                    ChartSynthInstrumentSelection.DefaultGuitar, ChartSynthInstrumentSelection.DefaultBass);
            }

            // TryLoad leaves the defaults in both families unless it Loaded.
            var state = TryLoad(path, out var guitar, out var bass, out _);
            return new ProfileInstrumentRead(state, guitar, bass);
        }
    }

    /// <summary>
    ///     Saves both families for <paramref name="profileId" />. False — and
    ///     nothing written — when the existing file cannot be read, a corrupt one
    ///     cannot be moved aside, the profile is the degraded stand-in, or the
    ///     write itself fails; <paramref name="error" /> says which.
    /// </summary>
    public bool Save(Guid profileId, ChartSynthInstrument guitar, ChartSynthInstrument bass, out string? error)
    {
        lock (_gate)
        {
            var path = PathFor(profileId, out error);
            if (path == null) return false;
            switch (TryLoad(path, out _, out _, out error))
            {
                case ProfileInstrumentReadState.Unreadable:
                    return false;
                case ProfileInstrumentReadState.Corrupt when !TryQuarantine(path, out error):
                    return false;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                AtomicFile.WriteAllText(path, Serialize(guitar, bass));
                error = null;
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error = ex.Message;
                return false;
            }
        }
    }

    /// <summary>The profile's file, or null (with why) when nothing may be read or written for it.</summary>
    internal string? PathFor(Guid profileId, out string? error)
    {
        error = null;
        try
        {
            if (_profiles.IsDegradedProfile(profileId))
            {
                error = "the profile registry is degraded";
                return null;
            }
            return Path.Combine(_profiles.GetDataDirectory(profileId), FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            error = ex.Message;
            return null;
        }
    }

    private static ProfileInstrumentReadState TryLoad(
        string path, out ChartSynthInstrument guitar, out ChartSynthInstrument bass, out string? error)
    {
        guitar = ChartSynthInstrumentSelection.DefaultGuitar;
        bass = ChartSynthInstrumentSelection.DefaultBass;
        error = null;
        string json;
        try
        {
            if (!File.Exists(path)) return ProfileInstrumentReadState.Absent;
            json = AtomicFile.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return ProfileInstrumentReadState.Unreadable;
        }

        try
        {
            using var document = JsonDocument.Parse(json, ReadOptions);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return ProfileInstrumentReadState.Corrupt;
            // A family that is missing or names nothing real costs that family, never the file.
            if (ChartSynthInstrumentJson.TryRead(root, "guitar", bass: false, out var g)) guitar = g;
            if (ChartSynthInstrumentJson.TryRead(root, "bass", bass: true, out var b)) bass = b;
            return ProfileInstrumentReadState.Loaded;
        }
        catch (JsonException)
        {
            return ProfileInstrumentReadState.Corrupt;
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

    private static string Serialize(ChartSynthInstrument guitar, ChartSynthInstrument bass)
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", FormatVersion);
            ChartSynthInstrumentJson.Write(writer, "guitar", guitar);
            ChartSynthInstrumentJson.Write(writer, "bass", bass);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
