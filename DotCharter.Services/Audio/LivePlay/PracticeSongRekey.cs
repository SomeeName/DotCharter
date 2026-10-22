using System.Text.Json;
using DotCharter.Services.Session;

namespace DotCharter.Services.Audio.LivePlay;

/// <summary>
///     Moves a practice store's entries from one own-chart song key (the project's path)
///     to another, for a project whose file name changed (<c>X.rscp</c> to <c>X.dcproj</c>).
///     <para>
///         Every arrangement of the song moves, keeping its sessions, bests, detector
///         bests, phrase mastery and take / replay file names. A song key that already
///         holds an entry for the same arrangement MERGES with it instead of replacing it,
///         so running the move twice, or after the new path has been played, drops nothing.
///         A store with no entry under the old key is left untouched (no write).
///     </para>
/// </summary>
internal static class PracticeSongRekey
{
    /// <summary>Re-keys <paramref name="store" /> in place; returns how many arrangements moved.</summary>
    public static int Apply(IDictionary<string, PracticeArrangementProfile> store, string fromSongKey, string toSongKey)
    {
        var moved = 0;
        foreach (var key in store.Keys.ToList())
        {
            if (!PracticeProfileService.TryParseKey(key, out var song, out var arrangement)) continue;
            if (string.Equals(song, toSongKey, StringComparison.Ordinal) || !ProjectStoreKeys.SamePath(song, fromSongKey)) continue;

            var target = $"{toSongKey}|{arrangement}";
            var source = store[key];
            store[target] = store.TryGetValue(target, out var existing) ? Merge(existing, source) : source;
            store.Remove(key);
            moved++;
        }

        return moved;
    }

    /// <summary>
    ///     Re-keys the store FILE of a player profile that is not the active one (the active
    ///     profile's store is the service's cached copy). Absent, unreadable and unparseable
    ///     files are left exactly as they are: a store that could not be read is presumed
    ///     intact and is never written over.
    /// </summary>
    public static int ApplyToFile(string filePath, string fromSongKey, string toSongKey,
        JsonSerializerOptions options, IAppLogger? logger)
    {
        try
        {
            if (!File.Exists(filePath)) return 0;
            var store = JsonSerializer.Deserialize<Dictionary<string, PracticeArrangementProfile>>(
                AtomicFile.ReadAllText(filePath), options);
            if (store is null) return 0;
            var moved = Apply(store, fromSongKey, toSongKey);
            if (moved > 0) AtomicFile.WriteAllText(filePath, JsonSerializer.Serialize(store, options));
            return moved;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger?.Warning(AppLogCategory.Audio,
                $"Practice store '{filePath}' was not re-keyed for the renamed project ({ex.GetType().Name}: {ex.Message}); it is left as it is.");
            return 0;
        }
    }

    /// <summary>
    ///     The union of two arrangement profiles of one song: bests and mastery are the larger
    ///     of the two, the best-run breakdown follows the better score, the session histories
    ///     are concatenated oldest first (the store's own cap trims on the next recorded run,
    ///     never here), and per-detector bests and per-phrase mastery are merged key by key.
    /// </summary>
    internal static PracticeArrangementProfile Merge(PracticeArrangementProfile target, PracticeArrangementProfile source)
    {
        if (source.BestScore > target.BestScore)
        {
            target.BestRunFullCombo = source.BestRunFullCombo;
            target.BestRunPerfects = source.BestRunPerfects;
            target.BestRunGreats = source.BestRunGreats;
            target.BestRunGoods = source.BestRunGoods;
        }
        else if (source.BestScore == target.BestScore)
        {
            target.BestRunFullCombo = target.BestRunFullCombo == true || source.BestRunFullCombo == true
                ? true
                : target.BestRunFullCombo ?? source.BestRunFullCombo;
            target.BestRunPerfects ??= source.BestRunPerfects;
            target.BestRunGreats ??= source.BestRunGreats;
            target.BestRunGoods ??= source.BestRunGoods;
        }

        target.BestScore = Math.Max(target.BestScore, source.BestScore);
        target.BestAccuracy = Math.Max(target.BestAccuracy, source.BestAccuracy);
        target.BestStreak = Math.Max(target.BestStreak, source.BestStreak);
        target.MasteryPercent = Math.Max(target.MasteryPercent, source.MasteryPercent);
        target.FullComboAchieved |= source.FullComboAchieved;
        if (string.IsNullOrWhiteSpace(target.DisplayName)) target.DisplayName = source.DisplayName;
        target.ChartDifficulty ??= source.ChartDifficulty;

        target.Sessions.AddRange(source.Sessions);
        target.Sessions.Sort(static (a, b) => a.EndedUtc.CompareTo(b.EndedUtc));

        foreach (var (detector, best) in source.DetectorBests ?? [])
        {
            target.DetectorBests ??= [];
            if (!target.DetectorBests.TryGetValue(detector, out var mine))
            {
                target.DetectorBests[detector] = best;
                continue;
            }

            mine.BestScore = Math.Max(mine.BestScore, best.BestScore);
            mine.BestAccuracy = Math.Max(mine.BestAccuracy, best.BestAccuracy);
            mine.BestStreak = Math.Max(mine.BestStreak, best.BestStreak);
            if (best.LastPlayedUtc > mine.LastPlayedUtc) mine.LastPlayedUtc = best.LastPlayedUtc;
        }

        foreach (var (phrase, record) in source.PhraseMastery)
        {
            if (!target.PhraseMastery.TryGetValue(phrase, out var mine))
            {
                target.PhraseMastery[phrase] = record;
                continue;
            }

            // Ladder state follows the more recent play; the play count is the sum.
            var newer = record.LastPlayedUtc > mine.LastPlayedUtc ? record : mine;
            target.PhraseMastery[phrase] = new PhraseMasteryRecord
            {
                Level = newer.Level,
                LadderSize = newer.LadderSize,
                DecayedAccuracy = newer.DecayedAccuracy,
                TimesPlayed = mine.TimesPlayed + record.TimesPlayed,
                LastPlayedUtc = newer.LastPlayedUtc
            };
        }

        return target;
    }
}
