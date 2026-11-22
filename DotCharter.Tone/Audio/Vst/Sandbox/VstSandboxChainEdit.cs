namespace DotCharter.Tone.Audio.Vst.Sandbox;

/// <summary>Applies changed seats independently, moving slot state before destination loads.</summary>
internal static class VstSandboxChainEdit
{
    private sealed record Edit(VstSandboxSeat Seat, List<VstChainEntry> Entries, List<VstChainEntry> Previous, bool Changed);

    public static async Task ApplyAsync(object gate, VstSandboxSeat[] seats, IReadOnlyList<VstChainEntry> entries,
        Func<VstSandboxSeat, Guid, bool, Task> unload,
        Func<VstSandboxSeat, IReadOnlyList<VstChainEntry>, Task> apply, IAppLogger? logger)
    {
        List<Edit> edits;
        lock (gate)
        {
            edits = seats.Select(seat => Plan(seat, entries)).Where(edit => edit.Changed).ToList();
            foreach (var edit in edits)
            {
                edit.Seat.Recovery.CancelPendingRestart();
                edit.Seat.Desired.Chain = edit.Entries;
            }
        }
        var desired = entries.ToDictionary(entry => entry.Id);
        var errors = new List<Exception>();

        // All source unloads finish before any destination can load a moved slot.
        foreach (var edit in edits)
        {
            foreach (var id in Candidates(gate, edit, desired))
            {
                try { await UnloadOrMoveAsync(gate, seats, edit, id, desired.GetValueOrDefault(id), unload, logger).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    RestoreBlockedOwner(gate, edits, edit, id);
                    Report(edit.Seat, ex, errors, logger);
                }
            }
        }

        foreach (var edit in edits)
        {
            try
            {
                List<VstChainEntry> safe;
                lock (gate)
                    safe = edit.Entries.Where(entry => CanLoad(seats, entry)).ToList();
                await apply(edit.Seat, safe).ConfigureAwait(false);
            }
            catch (Exception ex) { Report(edit.Seat, ex, errors, logger); }
            finally
            {
                try { await edit.Seat.StopIfEmptyAsync().ConfigureAwait(false); }
                catch (Exception ex) { Report(edit.Seat, ex, errors, logger); }
            }
        }
        if (errors.Count > 0) throw new AggregateException("One or more sandbox seats could not apply their chain.", errors);
    }

    private static Edit Plan(VstSandboxSeat seat, IReadOnlyList<VstChainEntry> entries)
    {
        var next = entries.Where(entry => entry.Player == seat.Player).Select(Clone).ToList();
        var previous = seat.Desired.Chain;
        seat.RejectedLoads.RetainUnchanged(previous, next);
        var changed = previous.Count != next.Count || previous.Where((entry, index) => !Same(entry, next[index])).Any();
        var missing = !seat.GaveUp && !seat.Recovery.IsRestartPending
            && next.Any(entry => !seat.RejectedLoads.Contains(entry)
                && (!seat.Slots.TryGetValue(entry.Id, out var live)
                    || VstSandboxChainPlan.MustUnload(entry, live.Path, live.Player)));
        return new Edit(seat, next, previous, changed || missing);
    }

    private static VstChainEntry Clone(VstChainEntry entry) => new()
    { Id = entry.Id, PluginPath = entry.PluginPath, Bypass = entry.Bypass, Player = entry.Player };

    private static bool Same(VstChainEntry entry, VstChainEntry desired) => entry.Id == desired.Id && entry.Player == desired.Player
        && entry.Bypass == desired.Bypass && string.Equals(entry.PluginPath, desired.PluginPath, StringComparison.OrdinalIgnoreCase);

    private static bool CanLoad(IEnumerable<VstSandboxSeat> seats, VstChainEntry entry) => !seats.Any(seat =>
        seat.Slots.TryGetValue(entry.Id, out var live) && VstSandboxChainPlan.MustUnload(entry, live.Path, seat.Player));

    private static List<Guid> Candidates(object gate, Edit edit, IReadOnlyDictionary<Guid, VstChainEntry> desired)
    {
        lock (gate)
        {
            var live = edit.Seat.Slots.Where(slot => VstSandboxChainPlan.MustUnload(desired.GetValueOrDefault(slot.Key),
                slot.Value.Path, edit.Seat.Player)).Select(slot => slot.Key);
            var moved = edit.Previous.Where(entry => desired.TryGetValue(entry.Id, out var target) && IsMove(edit.Seat, entry.PluginPath, target))
                .Select(entry => entry.Id);
            return live.Concat(moved).Distinct().ToList();
        }
    }

    private static bool IsMove(VstSandboxSeat source, string path, VstChainEntry? target) => target != null
        && target.Player != source.Player && string.Equals(path, target.PluginPath, StringComparison.OrdinalIgnoreCase);

    private static void RestoreBlockedOwner(object gate, IEnumerable<Edit> edits, Edit source, Guid id)
    {
        lock (gate)
        {
            if (!source.Seat.Slots.TryGetValue(id, out var live)) return;
            foreach (var edit in edits)
            {
                edit.Entries.RemoveAll(entry => entry.Id == id);
                if (edit.Seat != source.Seat) edit.Seat.Desired.PluginStates.Remove(id.ToString());
            }
            var index = Math.Max(0, source.Previous.FindIndex(entry => entry.Id == id));
            source.Entries.Insert(Math.Min(index, source.Entries.Count), new VstChainEntry
            { Id = id, PluginPath = live.Path, Bypass = live.Bypass, Player = source.Seat.Player });
        }
    }

    private static async Task UnloadOrMoveAsync(object gate, VstSandboxSeat[] seats, Edit edit, Guid id,
        VstChainEntry? target, Func<VstSandboxSeat, Guid, bool, Task> unload, IAppLogger? logger)
    {
        bool live;
        string? path;
        string? state;
        IVstSandboxHost? sourceHost;
        lock (gate)
        {
            sourceHost = edit.Seat.Host;
            live = edit.Seat.Slots.TryGetValue(id, out var slot);
            path = live ? slot.Path : edit.Previous.FirstOrDefault(entry => entry.Id == id)?.PluginPath;
            state = edit.Seat.Desired.PluginStates.GetValueOrDefault(id.ToString());
        }
        var move = path != null && IsMove(edit.Seat, path, target);
        if (move && live && sourceHost is { IsRunning: true })
        {
            var captured = await VstSandboxStateSync.CaptureAsync(sourceHost, [id.ToString()], (_, ex) =>
                logger?.Warning(AppLogCategory.Audio, $"Sandbox move capture({id}) failed; keeping the last captured state.", ex)).ConfigureAwait(false);
            if (captured.TryGetValue(id.ToString(), out var fresh))
            {
                state = fresh;
                lock (gate) edit.Seat.Desired.PluginStates[id.ToString()] = fresh;
            }
        }
        if (move)
        {
            lock (gate)
                if (!string.IsNullOrEmpty(state)) seats[target!.Player].Desired.PluginStates[id.ToString()] = state;
        }
        if (live)
        {
            try { await unload(edit.Seat, id, target == null).ConfigureAwait(false); }
            catch (IOException) when (move && sourceHost is { IsRunning: false })
            {
                // A dead owner cannot retain this slot; finish managed cleanup if
                // its crash notification did not already remove it.
                bool registered;
                lock (gate) registered = edit.Seat.Slots.ContainsKey(id);
                if (registered) await unload(edit.Seat, id, target == null).ConfigureAwait(false);
            }
        }
        if (move)
            lock (gate) edit.Seat.Desired.PluginStates.Remove(id.ToString());
    }

    private static void Report(VstSandboxSeat seat, Exception error, List<Exception> errors, IAppLogger? logger)
    {
        errors.Add(error);
        logger?.Warning(AppLogCategory.Audio, $"Sandbox chain edit failed for seat {seat.Player}.", error);
    }
}
