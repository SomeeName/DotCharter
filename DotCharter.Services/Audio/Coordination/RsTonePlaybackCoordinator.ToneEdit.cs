// RsTonePlaybackCoordinator: the song-tone edit — one of a song's tones held on the rig for the player to edit — and the song's saved edits laid over the package's conversions.

using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.Parsing.Psarc;

namespace DotCharter.Services.Audio.Coordination;

/// <summary>
///     The song-tone EDIT: the pre-play screen hands the rig one of the song's
///     tones, the player reshapes it, and the host saves the result for that song
///     (<c>SongToneEditStore</c> — DotCharter's data, never the package).
///     <para>
///         <b>It is a third DRIVER, not a second owner.</b> The edit takes the rig
///         exactly the way a run does — through <c>StartDriving</c>, so the
///         player's own board is snapshotted ONCE and put back by the same
///         generation-checked restore — with a schedule that never changes tone.
///         A run that begins mid-edit takes the rig over from it the way it does
///         from the editor's preview.
///     </para>
///     <para>
///         <b>Saved edits REPLACE the conversion, per SONG.</b> A package's
///         conversions are cached per package path, and one package (a
///         shared multi-song archive) can hold hundreds of songs, so the edits
///         cannot be baked into that cache: every prepare lays the prepared song's
///         edits over a copy of it (<see cref="OverlaySongEdits" />), under the
///         tone's key and every name alias that pointed at the converted preset,
///         with an empty report — the player built it, so nothing is approximated.
///     </para>
/// </summary>
public sealed partial class RsTonePlaybackCoordinator
{
    private static readonly IReadOnlyDictionary<string, string> NoAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlySet<string> NoKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    // The bare conversions of _convertedPsarcPath, and every trimmed tone key AND
    // name → its manifest key. Written under _convertGate.
    private Dictionary<string, DcRigPreset> _basePresets = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, ConversionReport> _baseReports = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<string, string> _baseAliases = NoAliases;

    /// <summary>Manifest keys the prepared song plays an edit for.</summary>
    private volatile IReadOnlySet<string> _editedKeys = NoKeys;

    private string _editingTone = "";

    /// <summary>True while a song tone is held on the rig for editing.</summary>
    public bool IsEditingTone => _driver == RsToneDriver.SongToneEdit;

    /// <summary>The tone being edited, as the caller named it; empty when none.</summary>
    public string EditingTone => IsEditingTone ? _editingTone : "";

    /// <summary>
    ///     The package's own key for a tone the chart names by key OR by name —
    ///     what an edit is saved under, so it is found again whichever the chart
    ///     used. Null when the prepared package has no such tone.
    /// </summary>
    public string? ResolvePackageToneKey(string tone) =>
        _baseAliases.TryGetValue(NormalizeKey(tone), out var key) ? key : null;

    /// <summary>
    ///     Put one of the prepared song's tones on the rig for editing: the
    ///     player's board is snapshotted and the tone (its saved edit if it has
    ///     one, else its conversion) applied. Returns false — and changes nothing —
    ///     while a run owns the rig, before the song's tones have converted, on a
    ///     build with no native rig, for an own chart, or for a tone the song does
    ///     not have. Editing a second tone hands the rig straight across.
    /// </summary>
    public bool BeginToneEdit(string tone)
    {
        if (_driver == RsToneDriver.PlayModeRun)
        {
            _logger?.Info(AppLogCategory.Audio, "RsTone: tone edit refused — a Play Mode run owns the rig.");
            return false;
        }
        if (_chain is NullDspChainService || _prepareInFlight is { IsCompleted: false } ||
            _preparedFromProject || _convertedPsarcPath is null)
        {
            _logger?.Info(AppLogCategory.Audio,
                "RsTone: tone edit refused — no package tones are prepared (or no native rig).");
            return false;
        }

        var presets = _presets;
        if (string.IsNullOrWhiteSpace(tone) || !presets.ContainsKey(NormalizeKey(tone)))
        {
            _logger?.Info(AppLogCategory.Audio, $"RsTone: tone edit refused — no converted preset for '{tone}'.");
            return false;
        }

        if (_driver is RsToneDriver.EditorPreview or RsToneDriver.SongToneEdit) StopDriving();

        _activePresets = presets;
        _activeIsProjectSource = false;
        _activeReports = _reports;
        _editingTone = tone;
        StartDriving(new RsToneSchedule(tone, new List<(double, string)>()), RsToneDriver.SongToneEdit);
        return true;
    }

    /// <summary>Stop editing and put the player's own board back. Idempotent, and
    /// a no-op while anything else owns the rig.</summary>
    public void EndToneEdit()
    {
        if (_driver != RsToneDriver.SongToneEdit) return;
        StopDriving();
        _editingTone = "";
    }

    /// <summary>
    ///     Re-apply the edited tone from the CURRENT maps — after a reset dropped
    ///     its edit and a re-prepare published the conversion again. Returns true
    ///     when an apply was queued.
    /// </summary>
    public bool ReapplyEditedTone()
    {
        if (_driver != RsToneDriver.SongToneEdit || _editingTone.Length == 0) return false;
        _activePresets = _presets;
        _activeIsProjectSource = false;
        _activeReports = _reports;
        if (_applyChannel is null) return false;
        RequestApply(_editingTone);
        return true;
    }

    /// <summary>Publishes a package's fresh conversions as the cached base.
    /// Called under the convert gate.</summary>
    private void PublishPackage(
        Dictionary<string, DcRigPreset> presets,
        Dictionary<string, ConversionReport> reports,
        IReadOnlyList<PsarcToneDefinition> defs)
    {
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var def in defs)
        {
            var key = NormalizeKey(def.Key);
            if (key.Length == 0) continue;
            aliases.TryAdd(key, key);
            var name = NormalizeKey(def.Name);
            if (name.Length > 0) aliases.TryAdd(name, key);
        }

        _basePresets = presets;
        _baseReports = reports;
        _baseAliases = aliases;
    }

    /// <summary>The cached conversions with <paramref name="songIdentity" />'s saved
    /// edits laid over a COPY, published as the maps a run and an edit apply from.</summary>
    private void OverlaySongEdits(string? songIdentity)
    {
        lock (_convertGate)
        {
            var presets = new Dictionary<string, DcRigPreset>(_basePresets, StringComparer.OrdinalIgnoreCase);
            var reports = new Dictionary<string, ConversionReport>(_baseReports, StringComparer.OrdinalIgnoreCase);
            var edited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            IReadOnlyDictionary<string, DcRigPreset> edits;
            try
            {
                edits = string.IsNullOrWhiteSpace(songIdentity)
                    ? new Dictionary<string, DcRigPreset>()
                    : SongTonesSource(songIdentity!);
            }
            catch (Exception ex)
            {
                _logger?.Warning(AppLogCategory.Audio, $"RsTone: reading the edited tones of '{songIdentity}' failed.", ex);
                edits = new Dictionary<string, DcRigPreset>();
            }

            foreach (var (rawKey, rig) in edits)
            {
                var key = NormalizeKey(rawKey);
                if (!_basePresets.TryGetValue(key, out var converted))
                {
                    _logger?.Info(AppLogCategory.Audio, $"RsTone: saved edit for unknown tone '{rawKey}' ignored.");
                    continue;
                }

                // The key, and every alias that resolved to the SAME conversion — a
                // name another tone already claimed keeps pointing at that tone.
                foreach (var (alias, target) in _baseAliases)
                {
                    if (string.Equals(target, key, StringComparison.OrdinalIgnoreCase) &&
                        presets.TryGetValue(alias, out var current) && ReferenceEquals(current, converted))
                    {
                        presets[alias] = rig;
                        reports[alias] = new ConversionReport();
                    }
                }

                edited.Add(key);
            }

            if (edited.Count > 0)
            {
                _logger?.Info(AppLogCategory.Audio,
                    $"RsTone: {edited.Count} tone(s) of '{songIdentity}' play the player's edit.");
            }

            _presets = presets;
            _reports = reports;
            _editedKeys = edited;
        }
    }

    /// <summary>The song half of a prepare's summary: which chips play an edit,
    /// and whether an edit may be saved at all (it needs a song to key it by).</summary>
    private RsToneConversionSummary WithSongEdits(RsToneConversionSummary summary, string? songIdentity)
    {
        var edited = _editedKeys;
        var aliases = _baseAliases;
        return summary with
        {
            EditedToneNames = summary.ToneNames
                .Where(n => aliases.TryGetValue(NormalizeKey(n), out var key) && edited.Contains(key))
                .ToList(),
            CanSaveToneEdits = summary.HasTones && !string.IsNullOrWhiteSpace(songIdentity)
        };
    }
}
