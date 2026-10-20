// RsAmpTableHandover: hands Rocksmith's own amp chains (the native rsamp table built from the player's install, ABI v45) to the engine when the game tables are published and on every engine build.
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using JuceSharp.Dsp;

namespace DotCharter.Tone.Audio.Engine;

/// <summary>
///     Hands <see cref="RsGameTables.NativeAmpTable" /> to the native <c>rsamp</c> module
///     (<c>dca_rsamp_set_table</c>). The native table is PROCESS-wide and survives an
///     engine rebuild, but the game tables are built in the background after the first
///     frame — usually after the engine is up, sometimes before it exists (when the native
///     library may not be loaded yet). So <c>JuceAudioPlaybackService</c> owns one of these:
///     it watches <see cref="RsGameTables.Published" /> (pushing only while an engine
///     exists) and the master-bus replay calls <see cref="Push" /> on every engine build,
///     so no ordering of "tables published" and "engine built" leaves a live engine
///     without the chains. Without an install there is nothing to hand over and every
///     <c>rsamp</c> module passes audio through — and the converter's amp index is empty
///     then, so it emits no <c>rsamp</c> at all.
/// </summary>
internal sealed class RsAmpTableHandover : IDisposable
{
    private readonly IAppLogger? _logger;
    private readonly Func<bool> _engineExists;
    private readonly Lock _gate = new();
    private byte[]? _loaded;

    public RsAmpTableHandover(IAppLogger? logger, Func<bool> engineExists)
    {
        _logger = logger;
        _engineExists = engineExists;
        RsGameTables.Published += OnPublished;
    }

    public void Dispose() => RsGameTables.Published -= OnPublished;

    private void OnPublished(RsGameTables tables)
    {
        if (_engineExists()) Push(tables, "the game tables were loaded");
    }

    /// <summary>Hands <paramref name="tables" />' native chains over, unless they are the
    /// ones the engine already took (the native side treats an identical reload as a
    /// no-op as well). Never throws.</summary>
    public void Push(RsGameTables tables, string reason)
    {
        var blob = tables.NativeAmpTable;
        if (blob is null) return;
        lock (_gate)
        {
            if (ReferenceEquals(blob, _loaded)) return;
            try
            {
                if (JuceRsAmpTable.TryLoad(blob))
                {
                    _loaded = blob;
                    _logger?.Info(AppLogCategory.Audio,
                        $"Rocksmith amp chains handed to the engine ({JuceRsAmpTable.LoadedRowCount} rows; {reason}).");
                    return;
                }

                // The engine log says why (a row whose key is not that row's amp: an
                // install whose amp list differs from the engine's).
                _logger?.Warning(AppLogCategory.Audio,
                    $"The engine refused the Rocksmith amp chains ({reason}); converted Rocksmith amps pass audio through.");
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                _logger?.Warning(AppLogCategory.Audio, $"Rocksmith amp chains not handed to the engine ({reason}): {ex.Message}");
            }
        }
    }
}
