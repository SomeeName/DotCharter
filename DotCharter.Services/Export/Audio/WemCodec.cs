namespace DotCharter.Services.Export.Audio;

/// <summary>
///     The audio codec inside a Wwise <c>.wem</c>.
/// </summary>
/// <remarks>
///     Rocksmith 2014's own audio is always <see cref="Vorbis" />, and Wwise's
///     encoder is the only thing that can produce it — Audiokinetic's EULA
///     forbids redistributing the authoring tool, so we cannot ship one.
///     <para>
///         We can still build a package without Wwise, because
///         <see cref="Pcm" /> and <see cref="ImaAdpcm" /> are <em>core</em> Wwise
///         codecs: Audiokinetic's SDK documents Vorbis and Opus as the only
///         <em>pluggable</em> (i.e. omittable) formats, so PCM and ADPCM decoders
///         are compiled into every sound engine build, Rocksmith's included.
///         Both are simple enough to emit directly — see <see cref="WemWriter" />.
///     </para>
///     <para>
///         The trade is size. For a 4-minute stereo song: Vorbis ~4 MB,
///         <see cref="ImaAdpcm" /> ~13 MB, <see cref="Pcm" /> ~46 MB. PSARC stores
///         <c>.wem</c> entries uncompressed, so that size lands in the archive
///         verbatim — prefer ADPCM for anything you intend to share.
///     </para>
///     <para>
///         NOT YET VERIFIED IN-GAME. The codec-availability argument is solid and
///         the container layout is copied from real Rocksmith wems, but no
///         PCM/ADPCM package has been loaded by Rocksmith 2014 itself. Treat a
///         silent song in-game as the first thing to suspect.
///     </para>
/// </remarks>
public enum WemCodec
{
    /// <summary>Wwise Vorbis. Requires an installed Wwise to produce.</summary>
    Vorbis,

    /// <summary>16-bit little-endian PCM. Largest, simplest, no encoder.</summary>
    Pcm,

    /// <summary>Wwise's IMA-ADPCM variant. ~3.6x PCM compression.</summary>
    ImaAdpcm
}

internal static class WemCodecInfo
{
    /// <summary>
    ///     The Wwise sound-engine plugin id for the codec, as written into the
    ///     <c>.bnk</c> HIRC sound object. Encoding is
    ///     <c>pluginId &lt;&lt; 16 | pluginType</c>, where pluginType 1 = codec, and the
    ///     codec ids come from <c>AkTypes.h</c>: PCM 1, ADPCM 2, VORBIS 4.
    ///     The bank and the wem must agree, or the game picks the wrong decoder.
    /// </summary>
    public static uint PluginId(this WemCodec codec) => codec switch
    {
        WemCodec.Pcm => 0x00010001u,      // 65537
        WemCodec.ImaAdpcm => 0x00020001u, // 131073
        WemCodec.Vorbis => 0x00040001u,   // 262145
        _ => throw new ArgumentOutOfRangeException(nameof(codec), codec, "Unknown wem codec.")
    };

    /// <summary>The RIFF <c>fmt </c> chunk's format tag for the codec.</summary>
    public static ushort FormatTag(this WemCodec codec) => codec switch
    {
        WemCodec.Pcm => 0x0001,
        WemCodec.ImaAdpcm => 0x0002,
        WemCodec.Vorbis => 0xFFFF,
        _ => throw new ArgumentOutOfRangeException(nameof(codec), codec, "Unknown wem codec.")
    };

    /// <summary>
    ///     Identifies the codec of an existing <c>.wem</c> from its <c>fmt </c>
    ///     format tag (little-endian u16 at absolute offset 0x14). Returns null
    ///     when the stream is not a recognisable wem. Leaves the stream position
    ///     where it found it.
    /// </summary>
    public static WemCodec? Detect(Stream wem)
    {
        var origin = wem.Position;
        try
        {
            if (wem.Length < 0x16) return null;
            wem.Position = 0;

            Span<byte> head = stackalloc byte[0x16];
            if (wem.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) < head.Length) return null;
            if (!head[..4].SequenceEqual("RIFF"u8) || !head[8..12].SequenceEqual("WAVE"u8)) return null;

            var tag = (ushort)(head[0x14] | (head[0x15] << 8));
            return tag switch
            {
                0xFFFF => WemCodec.Vorbis,
                0x0002 => WemCodec.ImaAdpcm,
                // 0xFFFE (extensible) is how Wwise Authoring tags PCM; vgmstream
                // maps both it and 0x0001 to plain PCM.
                0x0001 or 0xFFFE => WemCodec.Pcm,
                _ => null
            };
        }
        finally
        {
            wem.Position = origin;
        }
    }
}
