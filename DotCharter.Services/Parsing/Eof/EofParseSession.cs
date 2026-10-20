using System.Globalization;

namespace DotCharter.Services.Parsing.Eof;

/// <summary>
///     Encapsulates the per-load mutable state used by <see cref="EofImporter"/>:
///     the underlying byte reader and the metadata being populated. Methods walk
///     the EOF format sections in the canonical order documented by
///     <c>eof_load_song_pf</c>.
/// </summary>
internal sealed class EofParseSession
{
    private readonly EofImportMetadata? _meta;
    private readonly EofPackReader _r;

    public EofParseSession(EofPackReader reader, EofImportMetadata? metadata = null)
    {
        _r = reader;
        _meta = metadata;
    }

    public void ReadHeaderAndProperties()
    {
        var header = _r.ReadFixedString(16);
        if (!header.StartsWith(EofConstants.FileHeader, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Not an Editor on Fire project (header was '{header}').");

        var revision = _r.ReadU32();
        var timing = _r.ReadU8();
        if (timing == 1) throw new NotSupportedException("EOF delta timing is not supported.");
        var timeDivision = _r.ReadU32();

        if (_meta != null)
        {
            _meta.Revision = revision;
            _meta.TimeDivision = timeDivision;
        }
    }

    public void ReadIniBlocks()
    {
        // INI strings.
        var stringCount = _r.ReadU16();
        for (var i = 0; i < stringCount; i++)
        {
            var type = _r.ReadU8();
            var value = _r.ReadString();
            if (_meta == null) continue;
            switch (type)
            {
                case 2: _meta.Artist = value; break;
                case 3: _meta.Title = value; break;
                case 4: _meta.Charter = value; break;
                case 6: _meta.Year = value; break;
                case 8: _meta.Album = value; break;
                case 9: _meta.Genre = value; break;
            }
        }

        // INI booleans (single byte each: low 7 bits = type, bit 0xF0 = value).
        var boolCount = _r.ReadU16();
        for (var i = 0; i < boolCount; i++) _r.ReadU8();

        // INI numbers (u8 type + u32 value).
        var numCount = _r.ReadU16();
        for (var i = 0; i < numCount; i++)
        {
            _r.ReadU8();
            _r.ReadU32();
        }
    }

    public void ReadOggProfiles()
    {
        var count = _r.ReadU16();
        for (var i = 0; i < count; i++)
        {
            var filename = _r.ReadString(); // filename
            _r.ReadString(); // original audio filename
            _r.ReadString(); // description
            var midiOffset = _r.ReadU32();
            _r.ReadU32(); // flags

            // Capture the first non-empty profile filename — EOF defaults to "guitar.ogg".
            if (_meta != null && i == 0)
            {
                _meta.MidiOffsetMs = midiOffset;
                if (!string.IsNullOrWhiteSpace(filename)) _meta.AudioFileName = filename;
            }
        }
    }

    public void ReadBeats()
    {
        var count = _r.ReadU32();
        // The count is file-controlled and each beat record is 13 bytes —
        // bound it by the bytes actually left so a corrupt value fails as
        // "corrupt file" instead of pre-allocating a multi-GB list.
        if (count > (uint)(_r.Remaining / 13))
        {
            throw new InvalidDataException(
                $"EOF declares {count} beats but only {_r.Remaining} bytes remain — corrupt file.");
        }

        // Two-pass tempo derivation: first record raw ppqn+ms+flags, then derive
        // BPM (60_000_000 / ppqn) and time signature, then attribute "is measure
        // start" using the relevant beat flag bits.
        var rawBeats = new List<(uint ppqn, uint pos, uint flags, byte key)>((int)count);
        for (var i = 0; i < count; i++)
        {
            var ppqn = _r.ReadU32();
            var pos = _r.ReadU32();
            var flags = _r.ReadU32();
            var key = _r.ReadU8();
            rawBeats.Add((ppqn, pos, flags, key));
        }

        if (_meta == null) return;

        // EOF only flags the FIRST beat of each new time signature; intermediate
        // measure starts are inferred. Walk through the beats once, applying any
        // explicit TS change at a beat, then mark a beat as a measure start when
        // its index relative to the most recent TS-change beat is divisible by
        // the current beats-per-measure value.
        var currentBeatsInMeasure = 4;
        var currentDenominator = 4;
        var beatsSinceTsChange = 0;
        for (var i = 0; i < rawBeats.Count; i++)
        {
            var (ppqn, pos, flags, _) = rawBeats[i];

            var prevBpm = currentBeatsInMeasure;
            var prevDen = currentDenominator;
            DeriveTimeSignatureFromFlags(flags, ref currentBeatsInMeasure, ref currentDenominator);
            var hasExplicitTs = HasTimeSignatureFlag(flags);
            if (i == 0 || hasExplicitTs ||
                currentBeatsInMeasure != prevBpm || currentDenominator != prevDen)
            {
                beatsSinceTsChange = 0;
            }

            var bpm = ppqn > 0 ? 60_000_000.0 / ppqn : 120.0;
            var isMeasureStart =
                i == 0 ||
                hasExplicitTs ||
                (currentBeatsInMeasure > 0 && beatsSinceTsChange % currentBeatsInMeasure == 0);

            _meta.Beats.Add(new EofBeatInfo
            {
                TimeMs = pos,
                Bpm = bpm,
                BeatsInMeasure = currentBeatsInMeasure,
                NoteDenominator = currentDenominator,
                IsMeasureStart = isMeasureStart,
                IsAnchor = (flags & EofConstants.BeatFlagAnchor) != 0
            });

            beatsSinceTsChange++;
        }
    }

    private static bool HasTimeSignatureFlag(uint flags)
    {
        return (flags & (EofConstants.BeatFlagStart24 | EofConstants.BeatFlagStart34 |
                         EofConstants.BeatFlagStart44 | EofConstants.BeatFlagStart54 |
                         EofConstants.BeatFlagStart64 | EofConstants.BeatFlagCustomTs)) != 0;
    }

    private static void DeriveTimeSignatureFromFlags(uint flags, ref int beatsInMeasure, ref int denominator)
    {
        if ((flags & EofConstants.BeatFlagCustomTs) != 0)
        {
            // High two bytes encode (numerator-1) and (denominator-1) per song.h custom TS contract.
            var num = (int)((flags >> 24) & 0xFF) + 1;
            var den = (int)((flags >> 16) & 0xFF) + 1;
            if (num > 0)
            {
                beatsInMeasure = num;
                denominator = den > 0 ? den : 4;
            }

            return;
        }

        if ((flags & EofConstants.BeatFlagStart24) != 0)
        {
            beatsInMeasure = 2; denominator = 4;
        }
        else if ((flags & EofConstants.BeatFlagStart34) != 0)
        {
            beatsInMeasure = 3; denominator = 4;
        }
        else if ((flags & EofConstants.BeatFlagStart44) != 0)
        {
            beatsInMeasure = 4; denominator = 4;
        }
        else if ((flags & EofConstants.BeatFlagStart54) != 0)
        {
            beatsInMeasure = 5; denominator = 4;
        }
        else if ((flags & EofConstants.BeatFlagStart64) != 0)
        {
            beatsInMeasure = 6; denominator = 4;
        }
    }

    public void ReadTextEvents()
    {
        var count = _r.ReadU32();
        for (var i = 0; i < count; i++)
        {
            var text = _r.ReadString();
            var pos = _r.ReadU32();
            var track = _r.ReadU16();
            var flags = _r.ReadU16();
            if (_meta == null) continue;
            _meta.TextEvents.Add(new EofTextEventInfo
            {
                Text = text,
                BeatPos = pos,
                IsMs = (flags & 16) != 0, // EOF_EVENT_FLAG_FLOATING_POS
                Track = track,
                Flags = flags
            });
        }
    }

    public void ReadGlobalCustomDataBlocks()
    {
        var blockCount = _r.ReadU32();
        for (var i = 0; i < blockCount; i++)
        {
            var size = _r.ReadU32();
            var type = _r.ReadU32();
            if (size == 0) continue;
            ReadGlobalCustomBlockBody(size, type);
        }
    }

    private void ReadGlobalCustomBlockBody(uint size, uint type)
    {
        switch (type)
        {
            case 1: // Raw MIDI data — size INCLUDES the 4-byte type ID per save logic, so skip size-4.
                SkipBytes(size);
                break;
            case 2: // Floating-point beat positions: per-beat string. Apply to existing beats.
                // NOTE: For this block type the saved size is the raw temp-file size of the strings
                // ONLY — it does NOT include the 4-byte type ID. So when skipping we must skip
                // exactly `size` bytes after the type has already been consumed.
                if (_meta != null)
                {
                    // The stored string count and the current beat count can
                    // disagree (truncated or externally edited file). Always
                    // reconcile against the declared block size: reading past
                    // it — or leaving a tail unread — desyncs the stream and
                    // silently corrupts every track that follows.
                    var blockStart = _r.Position;
                    for (var b = 0; b < _meta.Beats.Count && _r.Position - blockStart < size; b++)
                    {
                        var s = _r.ReadString();
                        if (double.TryParse(s, NumberStyles.Float,
                                CultureInfo.InvariantCulture, out var fpos))
                        {
                            _meta.Beats[b].TimeMs = fpos;
                        }
                    }

                    var consumed = _r.Position - blockStart;
                    if (consumed < size) _r.Skip((int)(size - consumed));
                }
                else
                {
                    _r.Skip((int)size);
                }

                break;
            case 3: // Start/end point pair (size = 12: 4 type + 4 start + 4 end).
                _r.ReadU32(); // start
                _r.ReadU32(); // end
                break;
            default:
                SkipBytes(size); // size includes the 4-byte type already consumed; subtract it conceptually.
                break;
        }
    }

    private void SkipBytes(uint sizeIncludingType)
    {
        // Block size includes the 4-byte type identifier already read by the caller.
        var remaining = (int)Math.Max(0L, (long)sizeIncludingType - 4);
        _r.Skip(remaining);
    }

    public EofTrackInfo ReadTrackHeader(int index)
    {
        var name = _r.ReadString();
        var format = _r.ReadU8();
        _r.ReadU8(); // behavior
        _r.ReadU8(); // type
        _r.ReadU8(); // difficulty
        var flags = _r.ReadU32();
        _r.ReadU16(); // compliance flags
        var altName = (flags & EofConstants.TrackFlagAltName) != 0 ? _r.ReadString() : "";

        return new EofTrackInfo
        {
            Id = index,
            Name = !string.IsNullOrWhiteSpace(altName) ? altName : name,
            Format = format
        };
    }

    /// <summary>
    ///     Skips the entire body of a track (format-specific note section, sections,
    ///     and track-level custom data blocks). Used both for non-pro-guitar tracks
    ///     and during the track-listing pre-pass.
    /// </summary>
    /// <returns>
    ///     The number of notes encountered in the track body. Only meaningful for
    ///     pro guitar / legacy / vocal tracks; 0 otherwise. Used by the import
    ///     dialog to hide empty arrangements.
    /// </returns>
    public int SkipTrackBody(int format)
    {
        var noteCount = format switch
        {
            0 => 0,
            EofConstants.TrackFormatLegacy => SkipLegacyNotes(),
            EofConstants.TrackFormatVocal => SkipVocalNotes(),
            EofConstants.TrackFormatProKeys => throw new NotSupportedException("Pro Keys EOF tracks are not supported."),
            EofConstants.TrackFormatProGuitar => SkipProGuitarNotes(),
            EofConstants.TrackFormatProVariableLegacy => throw new NotSupportedException("Variable-lane legacy EOF tracks are not supported."),
            _ => throw new NotSupportedException($"Unknown EOF track format: {format}.")
        };

        SkipSections();
        SkipTrackCustomDataBlocks();
        return noteCount;
    }

    private int SkipLegacyNotes()
    {
        _r.ReadU8(); // numlanes
        var count = _r.ReadU32();
        for (var i = 0; i < count; i++)
        {
            _r.ReadString(); // name
            _r.ReadU8(); // type/diff
            _r.ReadU8(); // note bitmask
            _r.ReadU32(); // pos
            _r.ReadU32(); // length
            var flags = _r.ReadU32();
            if ((flags & EofConstants.NoteFlagExtended) != 0) _r.ReadU32();
        }
        return (int)count;
    }

    private int SkipVocalNotes()
    {
        _r.ReadU8(); // tone set
        var count = _r.ReadU32();
        for (var i = 0; i < count; i++)
        {
            _r.ReadString(); // text
            _r.ReadU8(); // set
            _r.ReadU8(); // pitch
            _r.ReadU32(); // pos
            _r.ReadU32(); // length
            var flags = _r.ReadU32();
            if ((flags & EofConstants.NoteFlagExtended) != 0) _r.ReadU32();
        }
        return (int)count;
    }

    private int SkipProGuitarNotes()
    {
        _r.ReadU8(); // numfrets
        var numStrings = _r.ReadU8();
        for (var i = 0; i < numStrings; i++) _r.ReadU8(); // tuning
        var count = _r.ReadU32();
        for (var i = 0; i < count; i++)
            EofProGuitarNoteReader.SkipNote(_r);
        return (int)count;
    }

    private void SkipSections()
    {
        var typeCount = _r.ReadU16();
        for (var t = 0; t < typeCount; t++)
        {
            _r.ReadU16(); // section type
            var instances = _r.ReadU32();
            for (var i = 0; i < instances; i++)
            {
                _r.ReadString(); // name
                _r.ReadU8(); // diff
                _r.ReadU32(); // start
                _r.ReadU32(); // end
                _r.ReadU32(); // flags
            }
        }
    }

    private void SkipTrackCustomDataBlocks()
    {
        var count = _r.ReadU32();
        for (var i = 0; i < count; i++)
        {
            var size = _r.ReadU32();
            _r.ReadU32(); // id
            if (size == 0) continue;
            // Block size includes the 4-byte ID just consumed.
            var remaining = (int)Math.Max(0L, (long)size - 4);
            _r.Skip(remaining);
        }
    }
}
