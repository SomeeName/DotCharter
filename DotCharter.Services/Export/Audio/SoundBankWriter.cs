using System.Text;

namespace DotCharter.Services.Export.Audio;

/// <summary>
///     Generator for Wwise sound banks (.bnk) as consumed by Rocksmith 2014.
///     Writes the BKHD/DIDX/DATA/HIRC/STID chunk sequence that pairs a song
///     key with its streamed .wem audio. PC platform only (little-endian).
///     Ported from Rocksmith2014.NET SoundBank.fs (SoundBank.generate).
/// </summary>
public static class SoundBankWriter
{
    // Ported from Rocksmith2014.NET SoundBank.fs, module HierarchyID.
    private const sbyte HierarchySound = 2;
    private const sbyte HierarchyAction = 3;
    private const sbyte HierarchyEvent = 4;
    private const sbyte HierarchyActorMixer = 7;

    // Ported from Rocksmith2014.NET SoundBank.fs, module SoundParameters.
    private const sbyte VolumeParameter = 0;

    /// <summary>
    ///     Generates a sound bank for the audio stream into the output stream
    ///     and returns the streamed-audio file ID as a string (the .wem's
    ///     filename stem). Reads the first 51200 bytes (72000 for previews)
    ///     from <paramref name="audioStream" /> as the embedded audio header;
    ///     shorter input is zero-padded, exactly like the F# reference. The
    ///     bank/sound/action IDs are drawn from a shared RNG and the file ID
    ///     derives from the process-randomized string hash of
    ///     <paramref name="name" />, matching the reference's
    ///     <c>abs (hash name)</c>. Leaves <paramref name="output" /> flushed
    ///     and rewound to position 0.
    ///     Ported from Rocksmith2014.NET SoundBank.fs (generate), PC branch.
    /// </summary>
    public static string Generate(
        string name,
        Stream audioStream,
        Stream output,
        float volume,
        bool isPreview,
        WemCodec codec = WemCodec.Vorbis) =>
        Generate(name, audioStream, output, volume, isPreview, codec, pinnedIds: null);

    /// <summary>As above, with every id pinned when <paramref name="pinnedIds" /> is set
    /// (<see cref="RocksmithBuildIdentity" />).</summary>
    internal static string Generate(
        string name,
        Stream audioStream,
        Stream output,
        float volume,
        bool isPreview,
        WemCodec codec,
        SoundBankIds? pinnedIds)
    {
        // the bank's "Song_<name>" and its "Play_<name>" event must spell the key the
        // manifests name. Encoding.ASCII wrote '?' for a kana, so they never matched.
        if (!name.All(char.IsAscii))
            throw new ArgumentException($"Sound bank name '{name}' is not ASCII; the game could not match its events.", nameof(name));

        // F#: RandomGenerator.next() |> uint32 — Random.Next() is non-negative.
        // F#: abs <| hash name — F#'s hash on a string is String.GetHashCode().
        var ids = pinnedIds ?? new SoundBankIds((uint)Random.Shared.Next(), Random.Shared.Next(),
            Math.Abs(name.GetHashCode()), (uint)Random.Shared.Next(), (uint)Random.Shared.Next());
        var soundbankId = ids.BankId;
        var fileId = ids.FileId;
        var soundId = ids.SoundId;

        var dataLength = isPreview ? 72000 : 51200;

        // F#: audioReader.ReadBytes dataLength — the reference reader
        // zero-fills the tail when the stream is shorter than requested.
        var data = new byte[dataLength];
        var totalRead = 0;
        while (totalRead < data.Length)
        {
            var read = audioStream.Read(data, totalRead, data.Length - totalRead);
            if (read == 0) break;
            totalRead += read;
        }

        var writer = new BinaryWriter(output, Encoding.ASCII, leaveOpen: true);

        WriteChunk(writer, "BKHD"u8, Header(soundbankId));
        WriteChunk(writer, "DIDX"u8, DataIndex(fileId, dataLength));
        WriteChunk(writer, "DATA"u8, data);
        WriteChunk(writer, "HIRC"u8, Hierarchy(soundbankId, soundId, fileId, name, volume, isPreview, codec, ids));
        WriteChunk(writer, "STID"u8, StringId(soundbankId, name));

        writer.Flush();
        output.Flush();
        output.Position = 0;

        return fileId.ToString();
    }

    /// <summary>Writes the chunk name, length and data into the writer.</summary>
    private static void WriteChunk(BinaryWriter writer, ReadOnlySpan<byte> name, byte[] data)
    {
        writer.Write(name);
        writer.Write(data.Length);
        writer.Write(data);
    }

    /// <summary>
    ///     Creates the header chunk. Version 91, bank ID, language ID 0,
    ///     HasFeedback 0, plus three int32s of PC-platform padding.
    ///     Ported from Rocksmith2014.NET SoundBank.fs (header).
    /// </summary>
    private static byte[] Header(uint soundbankId)
    {
        using var memory = new MemoryStream();
        using var writer = new BinaryWriter(memory);

        writer.Write(91u); // Sound Bank Version
        writer.Write(soundbankId);
        writer.Write(0u); // Language ID
        writer.Write(0u); // HasFeedback

        // getHeaderPaddingSize: PC | Mac -> 3
        for (var i = 0; i < 3; i++)
            writer.Write(0);

        return memory.ToArray();
    }

    /// <summary>
    ///     Creates the data index chunk: [file ID][offset 0][data length].
    ///     Ported from Rocksmith2014.NET SoundBank.fs (dataIndex).
    /// </summary>
    private static byte[] DataIndex(int id, int dataLength)
    {
        using var memory = new MemoryStream();
        using var writer = new BinaryWriter(memory);

        writer.Write(id);
        writer.Write(0); // File Offset
        writer.Write(dataLength);

        return memory.ToArray();
    }

    /// <summary>
    ///     Creates the hierarchy chunk: object count followed by the sound,
    ///     actor-mixer, action and event objects, each prefixed with its
    ///     one-byte type ID and int32 length.
    ///     Ported from Rocksmith2014.NET SoundBank.fs (hierarchy).
    /// </summary>
    private static byte[] Hierarchy(
        uint bankId, int soundId, int fileId, string name, float volume, bool isPreview, WemCodec codec,
        SoundBankIds ids)
    {
        const uint mixerId = 650605636u;
        var actionId = ids.ActionId;
        const uint numObjects = 4u;

        using var memory = new MemoryStream();
        using var writer = new BinaryWriter(memory);

        writer.Write(numObjects);

        WriteHierarchyObject(writer, HierarchySound,
            HierarchySoundObject(soundId, fileId, mixerId, volume, isPreview, codec, ids.ParentBusId));
        WriteHierarchyObject(writer, HierarchyActorMixer, HierarchyActorMixerObject(mixerId, soundId));
        WriteHierarchyObject(writer, HierarchyAction, HierarchyActionObject(actionId, soundId, bankId));
        WriteHierarchyObject(writer, HierarchyEvent, HierarchyEventObject(actionId, name));

        return memory.ToArray();
    }

    private static void WriteHierarchyObject(BinaryWriter writer, sbyte id, byte[] data)
    {
        writer.Write((byte)id);
        writer.Write(data.Length);
        writer.Write(data);
    }

    /// <summary>
    ///     Creates a hierarchy sound section.
    ///     Ported from Rocksmith2014.NET SoundBank.fs (hierarchySound),
    ///     PC branch (directParentID 65536).
    /// </summary>
    private static byte[] HierarchySoundObject(
        int id, int fileId, uint mixerId, float volume, bool isPreview, WemCodec codec, uint parentBusId)
    {
        var soundId = (uint)id;
        // The codec plugin id MUST match the codec actually inside the .wem —
        // it is what selects the decoder. 262145 (Vorbis) was hardcoded here while
        // Wwise was the only encoder we had.
        var pluginId = codec.PluginId();
        const uint streamType = 2u; // 2 = Streamed, with zero Latency
        var fileIdU = (uint)fileId;
        var sourceId = fileIdU;
        const sbyte languageSpecific = 0; // Sound SFX
        const sbyte overrideParent = 0;
        const sbyte numFx = 0;
        const uint directParentId = 65536u; // PC | Mac
        var unkId1 = isPreview ? 4178100890u : 0u;
        const sbyte priorityOverrideParent = 0;
        const sbyte priorityApplyDist = 0;
        const sbyte overrideMidi = 0;
        const sbyte numParam = 3;
        const sbyte param1Type = VolumeParameter;
        const sbyte param2Type = 46;
        const sbyte param3Type = 47;
        var param1Value = volume;
        const int param2Value = 1;
        const int param3Value = 3;
        const sbyte numRange = 0;
        const sbyte positionOverride = 0;
        const sbyte overrideGameAux = 0;
        const sbyte useGameAux = 0;
        const sbyte overrideUserAux = 0;
        const sbyte hasAux = 0;
        const sbyte virtualQueueBehavior = 1; // 1 = Play from elapsed time
        var killNewest = (sbyte)(isPreview ? 1 : 0);
        const sbyte useVirtualBehavior = 0;
        var maxNumInstance = (short)(isPreview ? 1 : 0);
        const sbyte isGlobalLimit = 0;
        const sbyte belowThresholdBehavior = 0;
        var isMaxNumInstOverrideParent = (sbyte)(isPreview ? 1 : 0);
        const sbyte isVirtualVoiceOptOverrideParent = 0;
        const int stateGroupList = 0;
        const short rtpcList = 0;
        const int feedbackBus = 0;

        using var memory = new MemoryStream();
        using var writer = new BinaryWriter(memory);

        writer.Write(soundId);
        writer.Write(pluginId);
        writer.Write(streamType);
        writer.Write(fileIdU);
        writer.Write(sourceId);
        writer.Write((byte)languageSpecific);
        writer.Write((byte)overrideParent);
        writer.Write((byte)numFx);
        writer.Write(parentBusId);
        writer.Write(directParentId);
        writer.Write(unkId1);
        writer.Write(mixerId);
        writer.Write((byte)priorityOverrideParent);
        writer.Write((byte)priorityApplyDist);
        writer.Write((byte)overrideMidi);
        writer.Write((byte)numParam);
        writer.Write((byte)param1Type);
        writer.Write((byte)param2Type);
        writer.Write((byte)param3Type);
        writer.Write(param1Value);
        writer.Write(param2Value);
        writer.Write(param3Value);
        writer.Write((byte)numRange);
        writer.Write((byte)positionOverride);
        writer.Write((byte)overrideGameAux);
        writer.Write((byte)useGameAux);
        writer.Write((byte)overrideUserAux);
        writer.Write((byte)hasAux);
        writer.Write((byte)virtualQueueBehavior);
        writer.Write((byte)killNewest);
        writer.Write((byte)useVirtualBehavior);
        writer.Write(maxNumInstance);
        writer.Write((byte)isGlobalLimit);
        writer.Write((byte)belowThresholdBehavior);
        writer.Write((byte)isMaxNumInstOverrideParent);
        writer.Write((byte)isVirtualVoiceOptOverrideParent);
        writer.Write(stateGroupList);
        writer.Write(rtpcList);
        writer.Write(feedbackBus);

        return memory.ToArray();
    }

    /// <summary>
    ///     Creates a hierarchy actor mixer section.
    ///     Ported from Rocksmith2014.NET SoundBank.fs (hierarchyActorMixer).
    /// </summary>
    private static byte[] HierarchyActorMixerObject(uint mixerId, int soundId)
    {
        const sbyte overrideParent = 0;
        const sbyte numFx = 0;
        const uint parentBusId = 2616261673u;
        const uint directParentId = 0u;
        const uint unkId1 = 0u;
        const uint unkId2 = 65792u;
        const sbyte priorityOverrideParent = 0;
        const sbyte priorityApplyDist = 0;
        const sbyte numParam = 0;
        const sbyte numRange = 0;
        const sbyte positionOverride = 0;
        const sbyte overrideGameAux = 0;
        const sbyte useGameAux = 0;
        const sbyte overrideUserAux = 0;
        const sbyte hasAux = 0;
        const sbyte virtualQueueBehavior = 0;
        const sbyte killNewest = 0;
        const sbyte useVirtualBehavior = 0;
        const short maxNumInstance = 0;
        const sbyte isGlobalLimit = 0;
        const sbyte belowThresholdBehavior = 0;
        const sbyte isMaxNumInstOverrideParent = 0;
        const sbyte isVirtualVoiceOptOverrideParent = 0;
        const int stateGroupList = 0;
        const short rtpcList = 0;
        const int numChild = 1;
        var child1 = soundId;

        using var memory = new MemoryStream();
        using var writer = new BinaryWriter(memory);

        writer.Write(mixerId);
        writer.Write((byte)overrideParent);
        writer.Write((byte)numFx);
        writer.Write(parentBusId);
        writer.Write(directParentId);
        writer.Write(unkId1);
        writer.Write(unkId2);
        writer.Write((byte)priorityOverrideParent);
        writer.Write((byte)priorityApplyDist);
        writer.Write((byte)numParam);
        writer.Write((byte)numRange);
        writer.Write((byte)positionOverride);
        writer.Write((byte)overrideGameAux);
        writer.Write((byte)useGameAux);
        writer.Write((byte)overrideUserAux);
        writer.Write((byte)hasAux);
        writer.Write((byte)virtualQueueBehavior);
        writer.Write((byte)killNewest);
        writer.Write((byte)useVirtualBehavior);
        writer.Write(maxNumInstance);
        writer.Write((byte)isGlobalLimit);
        writer.Write((byte)belowThresholdBehavior);
        writer.Write((byte)isMaxNumInstOverrideParent);
        writer.Write((byte)isVirtualVoiceOptOverrideParent);
        writer.Write(stateGroupList);
        writer.Write(rtpcList);
        writer.Write(numChild);
        writer.Write(child1);

        return memory.ToArray();
    }

    /// <summary>
    ///     Creates a hierarchy action section (Play, game-object scope).
    ///     Ported from Rocksmith2014.NET SoundBank.fs (hierarchyAction).
    /// </summary>
    private static byte[] HierarchyActionObject(uint actionId, int objId, uint bankId)
    {
        const sbyte actionScope = 3; // 3 = Game object
        const sbyte actionType = 4; // 4 = Play
        const sbyte isBus = 0;
        const sbyte numParam = 0;
        const sbyte numRange = 0;
        const sbyte fadeCurve = 4;

        using var memory = new MemoryStream();
        using var writer = new BinaryWriter(memory);

        writer.Write(actionId);
        writer.Write((byte)actionScope);
        writer.Write((byte)actionType);
        writer.Write(objId);
        writer.Write((byte)isBus);
        writer.Write((byte)numParam);
        writer.Write((byte)numRange);
        writer.Write((byte)fadeCurve);
        writer.Write(bankId);

        return memory.ToArray();
    }

    /// <summary>
    ///     Creates a hierarchy event section with a Play event whose ID is
    ///     the FNV hash of "Play_{name}".
    ///     Ported from Rocksmith2014.NET SoundBank.fs (hierarchyEvent).
    /// </summary>
    private static byte[] HierarchyEventObject(uint actionId, string name)
    {
        var eventId = FnvHash($"Play_{name}");
        const uint numEvents = 1u;

        using var memory = new MemoryStream();
        using var writer = new BinaryWriter(memory);

        writer.Write(eventId);
        writer.Write(numEvents);
        writer.Write(actionId);

        return memory.ToArray();
    }

    /// <summary>
    ///     Creates the string ID chunk naming the bank "Song_{name}".
    ///     Ported from Rocksmith2014.NET SoundBank.fs (stringID).
    /// </summary>
    private static byte[] StringId(uint soundbankId, string name)
    {
        var soundbankName = Encoding.ASCII.GetBytes($"Song_{name}");

        using var memory = new MemoryStream();
        using var writer = new BinaryWriter(memory);

        writer.Write(1u); // String Type
        writer.Write(1u); // NumNames
        writer.Write(soundbankId);
        writer.Write((byte)(sbyte)soundbankName.Length);
        writer.Write(soundbankName);

        return memory.ToArray();
    }

    /// <summary>
    ///     Calculates a Fowler–Noll–Vo hash for a string (lower-cased first).
    ///     Ported from Rocksmith2014.NET SoundBankUtils.fs (fnvHash).
    /// </summary>
    internal static uint FnvHash(string str)
    {
        var hash = 2166136261u;
        foreach (var ch in str.ToLowerInvariant())
            hash = (hash * 16777619u) ^ ch;
        return hash;
    }
}
